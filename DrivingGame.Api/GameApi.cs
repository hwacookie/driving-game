// REST API for remote car control and testing — port of car/src/rest_api.py.
// Same endpoints & payloads as the Python server; the sim engine is driven
// through its command queue + control buckets (thread-safe), state comes
// back as a /state-shaped dict. Implemented ONCE here, wired by BOTH hosts
// (Phase 7): the console host (DrivingGame.Server) and the Godot app
// (embedded, --source embedded). Port 5001 (macOS AirPlay Receiver squats
// on 5000) so external test injection keeps working — all existing test
// scripts run unchanged.

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
                ["idx"] = idx, ["name"] = s.Name,
                ["x1"] = s.X1, ["y1"] = s.Y1, ["x2"] = s.X2, ["y2"] = s.Y2,
                ["length"] = s.Length, ["width"] = s.Width, ["oneway"] = s.Oneway,
                ["lanes"] = s.Lanes, ["start_node"] = s.StartNode, ["end_node"] = s.EndNode,
            });
        });

        // Symbolic segment anchors (docs/DRIVING_MANEUVERS.md §8 "Segment
        // names"): a test that anchors to "straight_n→straight_s" says WHAT it
        // drives, not where that road happens to sit in the segment list - so
        // adding or deleting a track can never silently move an anchor.
        // An ambiguous name (same node pair twice) answers 400 with the
        // candidates instead of picking one.
        app.MapGet("/segment/by/{name}", (string name) =>
        {
            var net = engine.Network;
            if (net.SegmentNameAmbiguous.TryGetValue(name, out var amb))
                return Json(new Dictionary<string, object?>
                {
                    ["error"] = "ambiguous segment name",
                    ["candidates"] = amb.Select(i => net.Segments[i].Name).ToList(),
                }, 400);
            if (!net.SegmentByName.TryGetValue(name, out var idx))
                return Json(new Dictionary<string, object?>
                    { ["error"] = "unknown segment name" }, 404);
            var s2 = net.Segments[idx];
            return Results.Json(new Dictionary<string, object?>
            {
                ["idx"] = idx, ["name"] = s2.Name,
                ["x1"] = s2.X1, ["y1"] = s2.Y1, ["x2"] = s2.X2, ["y2"] = s2.Y2,
                ["length"] = s2.Length, ["width"] = s2.Width, ["oneway"] = s2.Oneway,
                ["lanes"] = s2.Lanes, ["start_node"] = s2.StartNode, ["end_node"] = s2.EndNode,
            });
        });

        app.MapGet("/segment_names", () =>
            Results.Json(engine.Network.SegmentByName));

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

        // Park a car WHERE IT STANDS (docs §8 'Parked car'): engine off, no
        // route, it stays put and is an obstruction the other cars must pass
        // (R32). Two-step on purpose - the scenario teleports the car into a
        // lane first, then parks it; commands apply in arrival order.
        app.MapPost("/park", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            if (!data.TryGetProperty("uid", out var u))
                return Json(new Dictionary<string, object?>
                    { ["error"] = "usage: {'uid': N}" }, 400);
            var uid = u.GetInt32();
            if (!engine.Cars.TryGetValue(uid, out var car))
                return Json(new Dictionary<string, object?>
                    { ["error"] = $"unknown car {uid}" }, 404);
            engine.EnqueueCommand(new ParkCommand(uid));
            return Results.Json(new Dictionary<string, object?>
                { ["ok"] = true, ["parked"] = uid });
        });

        // Re-point the PRIMARY (followed) car: /state's top level and the
        // cockpit describe it, and every teleport moves it to the newest
        // car - a multi-car scenario must say which car it monitors.
        app.MapPost("/follow", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            if (!data.TryGetProperty("uid", out var u))
                return Json(new Dictionary<string, object?>
                    { ["error"] = "usage: {'uid': N}" }, 400);
            var uid = u.GetInt32();
            if (!engine.Cars.TryGetValue(uid, out var car))
                return Json(new Dictionary<string, object?>
                    { ["error"] = $"unknown car {uid}" }, 404);
            engine.EnqueueCommand(new FollowCommand(uid));
            return Results.Json(new Dictionary<string, object?>
                { ["ok"] = true, ["follow"] = uid });
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
            {
                if (sg.ValueKind == JsonValueKind.String)
                {
                    // Symbolic anchor (see RoadSegment.Name): "start→end".
                    // An unknown or ambiguous name is a 400, never a silent
                    // fallback - a test must not spawn somewhere else.
                    var nm = sg.GetString();
                    var netT = engine.Network;
                    if (netT.SegmentNameAmbiguous.TryGetValue(nm, out var ambT))
                        return Json(new Dictionary<string, object?>
                        {
                            ["error"] = $"ambiguous segment name '{nm}'",
                            ["candidates"] = ambT,
                        }, 400);
                    if (!netT.SegmentByName.TryGetValue(nm, out var idxT))
                        return Json(new Dictionary<string, object?>
                            { ["error"] = $"unknown segment name '{nm}'" }, 400);
                    segment = idxT;
                }
                else
                    segment = sg.GetInt32();
            }
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
            // A slow vehicle: the km/h this car chooses to drive at (its class
            // ceiling still applies). Staging real slow traffic - R32 passes a
            // crawler, it does not pass a queue lead.
            double? cruiseKmh = null;
            if (data.TryGetProperty("cruise_kmh", out var ck) && ck.ValueKind != JsonValueKind.Null)
                cruiseKmh = ck.GetDouble();
            // The car's NAME (docs/TESTING.md): carried from the spawn, so
            // the scenario's role ("slow_car", "oncoming_car", "parked_car")
            // is on the car before its first tick, and the client can address
            // it by what it IS rather than by what it looks like.
            string? name = null;
            if (data.TryGetProperty("name", out var nmv) &&
                nmv.ValueKind == JsonValueKind.String)
                name = nmv.GetString();

            if (color is not null && !Config.CAR_COLORS.Contains(color))
                return Json(new Dictionary<string, object?>
                {
                    ["error"] = $"invalid color '{color}': expected one of " +
                                string.Join(", ", Config.CAR_COLORS),
                }, 400);

            engine.EnqueueCommand(
                new TeleportCommand(startPoint, segment, progress, add, speed, color,
                                    reverse, cruiseKmh, name));
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

        // --- the tape (docs/RECORD_PLAYBACK.md) ---------------------------------
        // Recording is a RUN property, not a per-scenario one: the e2e runner
        // starts it once after the host is up and saves it when the suite
        // finishes, so a failure report can be re-watched frame by frame later.
        app.MapPost("/tape", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            string? mapName = data.TryGetProperty("map", out var mp) ? mp.GetString() : null;
            RunTape.Start(mapName ?? "", SimEngine.DtFixed,
                          RoadNetwork.MapFingerprint(engine.Network));
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["command"] = "tape",
                ["tape"] = RunTape.StatusPayload(),
            });
        });

        app.MapPost("/tape/clear", () =>
        {
            RunTape.Clear();
            return Results.Json(new Dictionary<string, object?>
                { ["ok"] = true, ["command"] = "tape/clear" });
        });

        // One recorded substep (-1 = the last one). Null when the tape does not
        // reach that far - a caller must not invent a frame.
        app.MapPost("/tape/frame", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            long step = data.TryGetProperty("step", out var st) ? (long)st.GetDouble() : -1L;
            var f = RunTape.FrameAt(step);
            if (f is null)
                return Results.Json(new Dictionary<string, object?>
                    { ["ok"] = false, ["error"] = $"no tape frame at step {step}" });
            var cars = new List<object?>(f.Cars.Count);
            foreach (var cf in f.Cars)
                cars.Add(new Dictionary<string, object?>
                {
                    ["uid"] = cf.Uid, ["x"] = cf.X, ["y"] = cf.Y,
                    ["heading"] = cf.HeadingDeg, ["speed"] = cf.SpeedMps,
                    ["blinkers"] = cf.Blinkers, ["braking"] = cf.Brake,
                    ["wrong_side"] = cf.WrongSide, ["crashes"] = cf.CrashEpisodes,
                    ["cap_key"] = cf.CapKey,
                });
            var evs = new List<object?>(f.Events.Count);
            foreach (var ev in f.Events)
                evs.Add(new Dictionary<string, object?>
                    { ["uid"] = ev.Uid, ["decision"] = ev.Msg });
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["step"] = f.Step,
                ["fluidity_mps_per_car"] = f.FluidityMpsPerCar,
                ["moving_share"] = f.MovingShare,
                ["events"] = evs,
                ["units"] = "meters", ["cars"] = cars,
            });
        });

        // The recorded runs on disk - a review client picks one of these and
        // loads it, so an old run can be re-watched without re-running the
        // scenario.
        app.MapGet("/tape/list", () =>
            Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dir"] = RunTape.DefaultDir(),
                ["runs"] = RunTape.List(),
            }));

        // Load a recorded run into memory for scrubbing. Recording stays OFF:
        // the loaded tape is under review, the running sim keeps its own.
        app.MapPost("/tape/load", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            string path = data.TryGetProperty("path", out var p) ? p.GetString() : "";
            if (string.IsNullOrEmpty(path))
                return Results.Json(new Dictionary<string, object?>
                    { ["ok"] = false, ["error"] = "path required" });
            var loaded = RunTape.Load(path);
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = loaded,
                ["path"] = path,
                ["tape"] = RunTape.StatusPayload(),
            });
        });

        // Where the tape is and which substeps carry an event - what makes
        // "jump to the next decision/crash" cheap.
        app.MapGet("/tape/index", () =>
            Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["tape"] = RunTape.StatusPayload(),
                ["event_steps"] = RunTape.EventSteps(),
            }));

        // Write the whole tape next to the run log (best-effort: a disk error
        // answers, it never breaks the sim).
        app.MapPost("/tape/save", async (HttpRequest req) =>
        {
            var data = await ReadJsonBody(req);
            string path = data.TryGetProperty("path", out var p) ? p.GetString() : "";
            if (string.IsNullOrEmpty(path))
                return Results.Json(new Dictionary<string, object?>
                    { ["ok"] = false, ["error"] = "path required" });
            var written = RunTape.Save(path);
            return Results.Json(new Dictionary<string, object?>
            {
                ["ok"] = written is not null,
                ["path"] = written,
                ["tape"] = RunTape.StatusPayload(),
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
                    // METRES: /state reports positions in metres (like /map),
                    // so the target and its tolerance are metres too.
                    double x = value.GetProperty("x").GetDouble();
                    double y = value.GetProperty("y").GetDouble();
                    double dist = Math.Sqrt(
                        Math.Pow(Convert.ToDouble(state.GetValueOrDefault("x", 0.0)) - x, 2) +
                        Math.Pow(Convert.ToDouble(state.GetValueOrDefault("y", 0.0)) - y, 2));
                    if (dist < (value.TryGetProperty("threshold", out var th) ? th.GetDouble() : 5.0))
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

            // "interrupt": true -> kill a running test first, then start the
            // new one. The Godot "Run Test" button always sends this: a
            // click means "interrupt whatever is running, start this test".
            bool interrupt = data.TryGetProperty("interrupt", out var iEl) &&
                             iEl.ValueKind == JsonValueKind.True;

            var rows = GetTestList();
            if (number > 0 && rows is not null && number > rows.Count)
                return Json(new Dictionary<string, object?>
                    { ["error"] = $"no test {number} (1..{rows.Count})" }, 404);

            // <repo>/tests/test_turning.py -> two levels up = repo root
            string repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(RunnerPath)!)!;

            lock (TestLock)
            {
                if (_testRun is not null && _testRun.Proc.HasExited == false)
                {
                    if (!interrupt)
                        return Json(new Dictionary<string, object?>
                            { ["error"] = $"test #{_testRun.Number} is still running (pid {_testRun.Pid})" }, 409);
                    // Interrupt: kill the running test (whole process tree)
                    // so the new one starts immediately. The old run's log
                    // keeps its partial output - closing its stdout finishes
                    // the copy task, which disposes the log stream.
                    try { _testRun.Proc.Kill(true); }
                    catch (System.IO.IOException) { /* already gone */ }
                    catch (System.InvalidOperationException) { }
                    _testRun = null;
                }

                // Run tag: a fresh, persisted counter for every start (see
                // RunTag - the e2e runner restarts the host per run, so the
                // counter must survive in logs/run_counter). This log and
                // every crash dump written while the run is active share
                // the tag, so a dump can be attributed to its run.
                RunTag.Current = $"T_{RunTag.NextRunNumber(CrashDumper.LogsDir()):D3}";
                var logFile = Path.Combine(Path.GetDirectoryName(RunnerPath)!,
                    $"run_test_{RunTag.Current}_{number}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                Process proc;
                // NOTE: no `using` here - the stream must stay open for the
                // whole life of the subprocess; CopyToStreamAsync disposes it
                // when the process exits.
                var logStream = File.Open(logFile, FileMode.Append);
                try
                {
                    // -u: unbuffered stdout - the suite's output must reach
                    // the log immediately (an interrupted test would lose
                    // everything still sitting in Python's block buffer).
                    string cli = "-u " + (number > 0 ? $"\"{RunnerPath}\" --tests {number}"
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
                    RunTag.Current = RunTag.Idle;   // no run to tag dumps with
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
                if (exited)
                    RunTag.Current = RunTag.Idle;   // run over: idle tag again
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
