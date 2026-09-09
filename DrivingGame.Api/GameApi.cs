// REST API for remote car control and testing — port of car/src/rest_api.py.
// Same endpoints & payloads as the Python server; the sim engine is driven
// through its command queue + control buckets (thread-safe), state comes
// back as a /state-shaped dict. Implemented ONCE here, wired by BOTH hosts
// (Phase 7): the console host (DrivingGame.Server) and the Godot app
// (embedded, --source embedded). Port 5000 so external test injection keeps
// working — all existing test scripts run unchanged.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DrivingGame.Sim;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using Math = System.Math;

namespace DrivingGame.Api;

public static class GameApi
{
    // The sustained control keys (POST /control). One-shot commands (hazard,
    // uturn consumption) are handled separately — see SimEngine.GetControlFor.
    private static readonly string[] ControlKeys =
        { "accelerate", "brake", "steer_left", "steer_right",
          "blinker_left", "blinker_right", "uturn" };

    // External test runner: the test scenarios live OUTSIDE the sim -
    // tests/test_turning.py (in this repo) drives this very API. POST
    // /run_test launches it as a subprocess (one run at a time). Path is
    // overridable via env; the default resolves <repo>/tests relative to
    // the app binary (<root>/DrivingGame.Server/bin/<config>/net9.0/ ->
    // four levels up).
    private static string RunnerPath =>
        Environment.GetEnvironmentVariable("DRIVING_GAME_TEST_RUNNER")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..",
                                        "..", "..", "..",
                                        "tests", "test_turning.py"));

    private static string PythonExe =>
        Environment.GetEnvironmentVariable("PYTHON") ?? "python3";

    private static readonly object TestLock = new();
    private static (List<Dictionary<string, object?>>? Rows, double Ts) _testListCache = (null, 0);
    private static TestRunState? _testRun;

    // Results.Json has no status-code overload in any .NET version — small stand-in.
    private sealed class StatusJsonResult : IResult
    {
        public object? Data { get; }
        public int StatusCode { get; }
        public StatusJsonResult(object? data, int statusCode)
        { Data = data; StatusCode = statusCode; }
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCode;
            httpContext.Response.ContentType = "application/json";
            return httpContext.Response.WriteAsync(JsonSerializer.Serialize(Data));
        }
    }

    private sealed class TestRunState
    {
        public required int Number;
        public required int Pid;
        public required Process Proc;
        public required double StartedAt;
        public required string LogFile;
    }

    public static void MapEndpoints(WebApplication app, SimEngine engine)
    {
        // --- health ---------------------------------------------------------

        app.MapGet("/health", () => Results.Json(new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        }));

        // --- state ------------------------------------------------------------

        app.MapGet("/state", () => Results.Json(engine.StateSnapshot()));

        app.MapGet("/segment/{idx:int}", (int idx) =>
        {
            var net = engine.Network;
            if (idx < 0 || idx >= net.Segments.Count)
                return Json(new Dictionary<string, object?> { ["error"] = "unknown segment" }, 404);
            var s = net.Segments[idx];
            return Results.Json(new Dictionary<string, object?>
            {
                ["idx"] = idx,
                ["x1"] = s.X1, ["y1"] = s.Y1, ["x2"] = s.X2, ["y2"] = s.Y2,
                ["length"] = s.Length, ["width"] = s.Width, ["oneway"] = s.Oneway,
            });
        });

        // --- per-car driver decision log (car detail window) ---------------------
        // EVENTS, not samples: one entry per change of braking reason, e.g.
        // "brake: yield sign: car 26 at the crossing (19 m out)". Oldest
        // first; the newest (last) entry is WHY the car is behaving now.
        app.MapGet("/car/{uid:int}/decisions", (int uid) =>
        {
            if (!engine.Cars.TryGetValue(uid, out var c))
                return Json(new Dictionary<string, object?>
                    { ["error"] = "unknown car" }, 404);
            return Results.Json(new Dictionary<string, object?>
            {
                ["uid"] = uid,
                ["decisions"] = c.DecisionsSnapshot()
                    .Select(d => new Dictionary<string, object?>
                    {
                        ["t"] = Math.Round(d.T, 2),
                        ["msg"] = d.Msg,
                    }).ToList(),
            });
        });

        // --- map export ---------------------------------------------------------

        app.MapGet("/map", () => Results.Json(MapPayload.Build(engine.Network)));

        // --- control -------------------------------------------------------------

        app.MapPost("/control", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            int? uid = null;
            if (data.TryGetProperty("uid", out var u) && u.ValueKind != JsonValueKind.Null)
                uid = u.GetInt32();

            var flags = new Dictionary<string, object?>();
            foreach (var key in ControlKeys)
                if (data.TryGetProperty(key, out var v))
                    flags[key] = v.GetBoolean();
            var bucket = engine.SetControl(uid, flags);

            // Hazard is a one-shot command (both on AND off are explicit), so
            // it goes through the commands channel, not control_input.
            if (data.TryGetProperty("hazard", out var hz))
                engine.EnqueueCommand(new HazardCommand(hz.GetBoolean(), uid));

            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["control"] = bucket,
            });
        });

        // --- one-shot commands (queued, applied in arrival order) ------------------

        app.MapPost("/teleport", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            string? startPoint = null;
            int? segment = null;
            double? progress = null;
            bool add = false;
            double? speed = null;
            if (data.TryGetProperty("start_point", out var sp) && sp.ValueKind == JsonValueKind.String)
                startPoint = sp.GetString();
            if (data.TryGetProperty("segment", out var sg))
                segment = sg.GetInt32();
            if (data.TryGetProperty("progress", out var pr))
                progress = pr.GetDouble();
            if (data.TryGetProperty("add", out var ad))
                add = ad.GetBoolean();
            if (data.TryGetProperty("speed", out var sd) && sd.ValueKind != JsonValueKind.Null)
                speed = sd.GetDouble();
            bool reverse = false;
            if (data.TryGetProperty("reverse", out var rv))
                reverse = rv.GetBoolean();
            string? color = null;
            if (data.TryGetProperty("color", out var cd) && cd.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(cd.GetString()))
                color = cd.GetString();
            if (color is not null && !Config.CAR_COLORS.Contains(color))
                return Json(new Dictionary<string, object?>
                {
                    ["error"] = $"invalid color '{color}': expected one of " +
                                string.Join(", ", Config.CAR_COLORS),
                }, 400);

            engine.EnqueueCommand(
                new TeleportCommand(startPoint, segment, progress, add, speed, color, reverse));
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["command"] = "teleport",
                ["params"] = data,
            });
        });

        app.MapPost("/flags", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            int? uid = null;
            if (data.TryGetProperty("uid", out var u) && u.ValueKind != JsonValueKind.Null)
                uid = u.GetInt32();

            bool hasGreen = data.TryGetProperty("green", out var gEl);
            double[]? green = null;
            if (hasGreen && gEl.ValueKind == JsonValueKind.Array)
                green = gEl.EnumerateArray().Select(e => e.GetDouble()).ToArray();

            bool hasRed = data.TryGetProperty("red", out var rEl);
            (int SegIdx, double Progress)? red = null;
            if (hasRed && rEl.ValueKind == JsonValueKind.Array)
            {
                var items = rEl.EnumerateArray().ToArray();
                red = (items[0].GetInt32(), items[1].GetDouble());
            }

            bool? redNav = data.TryGetProperty("red_nav", out var rn) ? rn.GetBoolean() : null;

            engine.EnqueueCommand(new FlagsCommand(uid, hasGreen, green, hasRed, red, redNav));
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["command"] = "flags",
                ["params"] = data,
            });
        });

        app.MapPost("/label", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            int? uid = null;
            if (data.TryGetProperty("uid", out var u) && u.ValueKind != JsonValueKind.Null)
                uid = u.GetInt32();
            string? text = data.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                           ? t.GetString() : null;

            engine.EnqueueCommand(new LabelCommand(uid, text));
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["command"] = "label",
                ["params"] = data,
            });
        });

        app.MapGet("/start_points", () => Results.Json(engine.StartPoints));

        app.MapPost("/toggle", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            int? uid = null;
            if (data.TryGetProperty("uid", out var u) && u.ValueKind != JsonValueKind.Null)
                uid = u.GetInt32();
            bool? breadcrumbs = data.TryGetProperty("breadcrumbs", out var b) ? b.GetBoolean() : null;
            bool? validator = data.TryGetProperty("validator", out var v) ? v.GetBoolean() : null;
            string? mode = data.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
                           ? m.GetString() : null;
            bool? headlights = data.TryGetProperty("headlights", out var hl) ? hl.GetBoolean() : null;
            bool? taillights = data.TryGetProperty("taillights", out var tl) ? tl.GetBoolean() : null;
            bool? clearBlinker = data.TryGetProperty("clear_blinker", out var cb) ? cb.GetBoolean() : null;

            engine.EnqueueCommand(new ToggleCommand(breadcrumbs, validator, mode, uid, headlights, taillights, clearBlinker));
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["command"] = "toggle",
                ["params"] = data,
            });
        });

        app.MapPost("/freeze", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            bool frozen = data.TryGetProperty("frozen", out var f) ? f.GetBoolean() : true;
            engine.EnqueueCommand(new FreezeCommand(frozen));
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["command"] = "freeze",
            });
        });

        // --- wait --------------------------------------------------------------------

        app.MapPost("/wait", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            double timeout = data.TryGetProperty("timeout", out var t) ? t.GetDouble() : 5.0;
            string? condition = data.TryGetProperty("condition", out var c) ? c.GetString() : null;
            JsonElement value = default;
            if (data.TryGetProperty("value", out var v)) value = v.Clone();

            double start = Environment.TickCount64 / 1000.0;
            while (Environment.TickCount64 / 1000.0 - start < timeout)
            {
                var state = engine.StateSnapshot();

                if (condition == "segment_changed" &&
                    !(state.TryGetValue("segment", out var segV) && segV is int si && si == value.GetInt32()))
                    return WaitMet(state);
                else if (condition == "speed_reached" &&
                         (state.TryGetValue("speed", out var spV) ? Convert.ToDouble(spV) : 0.0) >= value.GetDouble())
                    return WaitMet(state);
                else if (condition == "position_reached")
                {
                    double x = value.GetProperty("x").GetDouble();
                    double y = value.GetProperty("y").GetDouble();
                    double dist = Math.Sqrt(
                        Math.Pow(Convert.ToDouble(state.GetValueOrDefault("x", 0.0)) - x, 2) +
                        Math.Pow(Convert.ToDouble(state.GetValueOrDefault("y", 0.0)) - y, 2));
                    if (dist < (value.TryGetProperty("threshold", out var th) ? th.GetDouble() : 10.0))
                        return WaitMet(state);
                }

                await Task.Delay(50);
            }
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["condition_met"] = false,
                ["timeout"] = true,
            });
        });

        // Results.Json has no status-code overload in any .NET version — local stand-in.
        static IResult Json(object? data, int statusCode) => new StatusJsonResult(data, statusCode);

        static IResult WaitMet(Dictionary<string, object?> state) => Results.Json(
            new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["condition_met"] = true,
                ["state"] = state,
            });

        // --- reset / cars ---------------------------------------------------------------

        app.MapPost("/reset", () =>
        {
            engine.ResetControls();
            return Results.Json(new Dictionary<string, object?> { ["ok"] = true });
        });

        app.MapPost("/cars", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            string? action = data.TryGetProperty("action", out var a) ? a.GetString() : null;
            int? uid = data.TryGetProperty("uid", out var u) && u.ValueKind != JsonValueKind.Null
                       ? u.GetInt32() : null;
            engine.EnqueueCommand(new CarsCommand(action ?? "", uid));
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["command"] = "cars",
                ["params"] = data,
            });
        });

        // --- external test runner ---------------------------------------------------------

        app.MapGet("/tests", () =>
        {
            var rows = GetTestList();
            if (rows is null)
                return Json(new Dictionary<string, object?> { ["error"] = "could not list tests" }, 500);
            return Results.Json(rows);
        });

        app.MapPost("/run_test", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            // "number" is optional: absent/null = full suite, >=1 = single
            // test. (0 or negative = invalid.)
            int number = 0;
            if (data.TryGetProperty("number", out var nEl) &&
                nEl.ValueKind == JsonValueKind.Number)
            {
                number = nEl.GetInt32();
                if (number < 1)
                    return Json(new Dictionary<string, object?>
                        { ["error"] = "\"number\" must be >= 1 (omit it for the full suite)" }, 400);
            }
            else if (data.TryGetProperty("number", out var bad) &&
                     bad.ValueKind != JsonValueKind.Null)
                return Json(new Dictionary<string, object?>
                    { ["error"] = "\"number\" must be an integer or null" }, 400);

            // Optional extra CLI args for test_turning.py (e.g.
            // ["--stress-cars", "250"]). Only plain strings, no shell.
            var extraArgs = new List<string>();
            if (data.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Array)
                foreach (var a in aEl.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()))
                        extraArgs.Add(a.GetString()!);

            var rows = GetTestList();
            if (number > 0 && rows is not null && number > rows.Count)
                return Json(new Dictionary<string, object?>
                    { ["error"] = $"no test {number} (1..{rows.Count})" }, 404);

            // <repo>/tests/test_turning.py -> two levels up = repo root
            string repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(RunnerPath)!)!;
            var logFile = Path.Combine(Path.GetDirectoryName(RunnerPath)!,
                $"run_test_{number}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            lock (TestLock)
            {
                if (_testRun is not null && _testRun.Proc.HasExited == false)
                    return Json(new Dictionary<string, object?>
                        { ["error"] = $"test #{_testRun.Number} is still running (pid {_testRun.Pid})" }, 409);

                Process proc;
                // NOTE: no `using` here - the stream must stay open for the
                // whole life of the subprocess; CopyToStreamAsync disposes it
                // when the process exits.
                var logStream = File.Open(logFile, FileMode.Append);
                try
                {
                    string cli = (number > 0 ? $"\"{RunnerPath}\" --tests {number}"
                                             : $"\"{RunnerPath}\"") +
                        string.Join(" ", extraArgs.Select(a => " \"" + a.Replace("\"", "\\\"") + "\""));
                    var psi = new ProcessStartInfo(PythonExe, cli)
                    {
                        WorkingDirectory = repoRoot,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                    };
                    proc = Process.Start(psi)!;
                    _ = CopyToStreamAsync(proc.StandardOutput, logStream);
                }
                catch (Exception e)
                {
                    logStream.Dispose();
                    return Json(new Dictionary<string, object?>
                        { ["error"] = $"launch failed: {e.Message}" }, 500);
                }

                _testRun = new TestRunState
                {
                    Number = number,
                    Pid = proc.Id,
                    Proc = proc,
                    StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    LogFile = logFile,
                };
            }
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["number"] = number,
                ["pid"] = _testRun.Pid,
                ["log_file"] = _testRun.LogFile,
            });
        });

        app.MapGet("/run_test", () =>
        {
            lock (TestLock)
            {
                if (_testRun is null)
                    return Results.Json(new Dictionary<string, object?> { ["running"] = false });
                bool exited = _testRun.Proc.HasExited;
                return Results.Json(new Dictionary<string, object?>
                {
                    ["running"] = !exited,
                    ["number"] = _testRun.Number,
                    ["pid"] = _testRun.Pid,
                    ["started_at"] = _testRun.StartedAt,
                    ["finished_at"] = exited ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 : null,
                    ["returncode"] = exited ? _testRun.Proc.ExitCode : null,
                    ["log_file"] = _testRun.LogFile,
                });
            }
        });

        // --- obstacles ---------------------------------------------------------------------

        app.MapGet("/obstacles", () => Results.Json(
            engine.Obstacles.SnapshotDicts().Select(d => (object)new Dictionary<string, object?>(d)).ToList()));

        app.MapPost("/obstacles", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            try
            {
                string type = data.TryGetProperty("type", out var t) ? t.GetString()! : "car";
                string color = data.TryGetProperty("color", out var c) ? c.GetString()! : "";
                double x = data.GetProperty("x").GetDouble();
                double y = data.GetProperty("y").GetDouble();
                var ob = engine.Obstacles.Place(engine.Network, type, color, x, y);
                return Json(ob.ToDict(), 201);
            }
            catch (KeyNotFoundException e)
            {
                return Json(new Dictionary<string, object?> { ["error"] = $"invalid request: {e.Message}" }, 400);
            }
            catch (FormatException e)
            {
                return Json(new Dictionary<string, object?> { ["error"] = $"invalid request: {e.Message}" }, 400);
            }
            catch (PlacementError e)
            {
                return Json(new Dictionary<string, object?> { ["error"] = e.Message }, 400);
            }
        });

        app.MapDelete("/obstacles/{obId:int}", (int obId) =>
        {
            if (!engine.Obstacles.Remove(obId))
                return Json(new Dictionary<string, object?>
                    { ["error"] = $"no obstacle with id {obId}" }, 404);
            return Results.Json(new Dictionary<string, object?> { ["ok"] = true, ["id"] = obId });
        });
    }

    // --- helpers ---------------------------------------------------------------------------

    private static async Task<JsonElement> ReadJsonBody(HttpRequest req)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        return doc.RootElement.Clone();
    }

    /// <summary>Numbered scenario list from the runner (--list-json), cached 5 min.
    /// Returns null if the runner can't be reached (a fresh cache is still
    /// served in that case).</summary>
    private static List<Dictionary<string, object?>>? GetTestList()
    {
        lock (TestLock)
        {
            var (data, ts) = _testListCache;
            if (data is not null && Environment.TickCount64 / 1000.0 - ts <= 300)
                return data;
        }
        try
        {
            // <repo>/tests/test_turning.py -> two levels up = repo root
            string repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(RunnerPath)!)!;
            var psi = new ProcessStartInfo(PythonExe, $"\"{RunnerPath}\" --list-json")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { proc.Kill(); return StaleTestList(); }

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) return StaleTestList();
            using var doc = JsonDocument.Parse(lines[^1]);
            var rows = doc.RootElement.EnumerateArray()
                .Select(e => e.EnumerateObject()
                    .ToDictionary(kv => kv.Name, kv => (object?)BoxValue(kv.Value)))
                .ToList();
            lock (TestLock) _testListCache = (rows, Environment.TickCount64 / 1000.0);
            return rows;
        }
        catch
        {
            return StaleTestList();
        }
    }

    private static List<Dictionary<string, object?>>? StaleTestList()
    {
        lock (TestLock) return _testListCache.Rows;
    }

    private static object? BoxValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.GetRawText(),
    };

    private static async Task CopyToStreamAsync(StreamReader reader, Stream target)
    {
        try
        {
            var buffer = new char[4096];
            var bytes = new byte[8192];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                int n = System.Text.Encoding.UTF8.GetBytes(buffer, 0, read, bytes, 0);
                await target.WriteAsync(bytes.AsMemory(0, n));
            }
            await target.FlushAsync();
        }
        catch { /* process exited */ }
        finally { target.Dispose(); }
    }
}
