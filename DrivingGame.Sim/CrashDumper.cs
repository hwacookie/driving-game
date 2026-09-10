// Crash forensics: on a FRESH contact (crash episode) the sim dumps the
// full decision log of BOTH cars involved to <repo root>/logs/. The per-car
// decision log lives only in memory (Car.Decisions, capped at 100 entries),
// so without this a crash's forensic trail is lost when the process exits or
// the car is cleared. Each car is dumped at most ONCE: a car that already
// appeared in a dump is not dumped again (the stress runs freeze on the
// first crash anyway, so a run normally produces exactly one file). Tracking
// is by Car object identity, so a cleared+respawned car (new object) is
// eligible again.

using System.Text.Json;

namespace DrivingGame.Sim;

/// <summary>Writes crash decision-log dumps to &lt;repo root&gt;/logs/
/// (best-effort: a disk error never breaks the sim).</summary>
public static class CrashDumper
{
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
    // Cars that already appeared in a dump (reference semantics: a respawned
    // car is a new object and therefore eligible again).
    private static readonly HashSet<Car> Dumped = new();

    /// <summary>Dump the decision log + state of both cars of a fresh crash.
    /// pose: the cars' positions are the PRE-STEP (post-rollback) pose - the
    /// pose just before impact - and Speed is already zeroed, so the impact
    /// speed comes from the impactSpeed map instead.</summary>
    public static void Dump(Car a, Car b, double simTime,
                            double? speedA, double? speedB,
                            RoadNetwork? net = null)
    {
        try
        {
            string path;
            string json;
            lock (Lock)
            {
                if (Dumped.Contains(a) && Dumped.Contains(b))
                {
                    Console.WriteLine("[crash-dump] skipped: both cars already dumped");
                    return;
                }
                // RunTag.Current prefixes the dump with the test run that
                // produced it (T_000 = no run: unit tests / manual drives).
                path = Path.Combine(LogsDir(),
                    $"{RunTag.Current}_crash_{(int)Math.Round(simTime):D3}s_car{Math.Min(a.Uid, b.Uid):D2}_car{Math.Max(a.Uid, b.Uid):D2}.json");
                json = JsonSerializer.Serialize(new
                {
                    sim_time = Math.Round(simTime, 3),
                    @event = "fresh_contact",
                    cars = new[] { CarPayload(a, speedA, net), CarPayload(b, speedB, net) },
                }, JsonOpts);
                File.WriteAllText(path, json);
                Dumped.Add(a);
                Dumped.Add(b);
            }
            Console.WriteLine($"[crash-dump] {path}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[crash-dump] FAILED: {e.Message}");
        }
    }

    private static Dictionary<string, object?> CarPayload(Car c, double? impactMps,
                                                          RoadNetwork? net) =>
        new()
        {
            ["uid"] = c.Uid,
            ["color"] = c.Color,
            ["driver"] = c.Driver?.GetName() ?? "FREE",
            ["pose"] = "pre-step (post-rollback)",
            ["x"] = Math.Round(c.X, 3),
            ["y"] = Math.Round(c.Y, 3),
            ["heading"] = Math.Round(c.Heading, 3),
            ["impact_speed_kmh"] = impactMps is null
                ? null : Math.Round(impactMps.Value * 3.6, 2),
            ["segment"] = c.SegIdx,
            ["level"] = net is null ? 0 : net.Segments[c.SegIdx].Level,
            ["progress"] = Math.Round(c.Progress, 4),
            ["contact_with"] = c.ContactWith,
            ["hazard"] = (c.Driver as BicycleDriver)?.Hazard ?? false,
            ["decisions"] = c.DecisionsSnapshot()
                .Select(d => new { t = Math.Round(d.T, 3), msg = d.Msg })
                .ToList(),
        };

    // <repo root>/logs, derived the same way ObstacleManager finds <root>/data:
    // walk up from the app base dir to the project.godot marker. Public so
    // the server can store the run counter (RunTag.NextRunNumber) beside
    // the dumps.
    public static string LogsDir()
    {
        string root = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot")))
            {
                root = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }
        string logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        return logs;
    }
}
