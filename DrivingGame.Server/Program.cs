// Console host: Sim + REST API on :5001 (headless testing without Godot).
// (Not 5000: macOS AirPlay Receiver squats on it.)
// Port of the car/src/main.py entry point. Same flags: --map, --start,
// --port, --smoke. The sim runs on a dedicated background thread with its
// own 60 Hz pacing (sleep + spin window) - fixed-timestep physics stays
// independent of API/render hitches.

using System.Diagnostics;
using System.Runtime.InteropServices;
using DrivingGame.Sim;
using DrivingGame.Api;

// --- Argument parsing (same flags as the Python entry point) -----------------

string? mapName = null, startName = null;
int apiPort = 5001, smokeFrames = 0;
bool measureSleep = false;
bool collisionsEnabled = true;
double warp = 1.0;      // --warp N: N sim seconds per wall second
bool fastMode = false;  // --fast: no pacing at all (headless / CI runs)
bool benchMode = false; // --bench [phases]: capacity sweep, in-process (no API)
string benchPhases = "50,100,150,200,300";
string benchLoop = "fig8_n";  // --bench-loop: node-name prefix of the loop
// NB: C# top-level `args` does NOT include the program name (unlike Python's
// sys.argv) - start at 0.
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--map": if (i + 1 < args.Length) mapName = args[++i]; break;
        case "--start": if (i + 1 < args.Length) startName = args[++i]; break;
        case "--port": if (i + 1 < args.Length) apiPort = int.Parse(args[++i]); break;
        case "--smoke": if (i + 1 < args.Length) smokeFrames = int.Parse(args[++i]); break;
        case "--measure-sleep": measureSleep = true; break;
        case "--warp": if (i + 1 < args.Length) warp = System.Math.Max(1.0, double.Parse(args[++i])); break;
        case "--fast": fastMode = true; break;
        case "--bench":
            benchMode = true;
            // An optional phase list ("50,100,200"); the next flag must stay
            // a flag, so only take an all-digits-and-commas argument.
            if (i + 1 < args.Length && args[i + 1].All(c => char.IsDigit(c) || c == ','))
                benchPhases = args[++i];
            break;
        case "--bench-loop": if (i + 1 < args.Length) benchLoop = args[++i]; break;
        case "--no-collisions": collisionsEnabled = false; break;
    }
}

// --- Phase 5 pacing re-measurement (.NET Thread.Sleep on this Mac) -------------
if (measureSleep)
{
    const double TargetMs = 1000.0 / 60.0;
    const int N = 300;
    var swSleep = Stopwatch.StartNew();
    var overshoots = new double[N];
    for (int k = 0; k < N; k++)
    {
        double t0 = swSleep.Elapsed.TotalMilliseconds;
        Thread.Sleep(TimeSpan.FromMilliseconds(TargetMs));
        overshoots[k] = swSleep.Elapsed.TotalMilliseconds - t0 - TargetMs;
    }
    Array.Sort(overshoots);
    double Mean(double[] a) => a.Sum() / a.Length;
    Console.WriteLine($"Thread.Sleep({TargetMs:F1}ms) x{N}:");
    Console.WriteLine($"  overshoot ms: mean={Mean(overshoots):F2} p50={overshoots[N / 2]:F2} " +
                      $"p95={overshoots[(int)(N * 0.95)]:F2} max={overshoots[^1]:F2}");

    // Full production-style pacing loop (sleep + spin window), ~3 s each,
    // empty work - measures the pacing floor (best-case fps) per window size:
    foreach (double spinS in new[] { 0.012, 0.008, 0.005, 0.004, 0.0 })
    {
        var swLoop = Stopwatch.StartNew();
        long iters = 0;
        while (swLoop.Elapsed.TotalMilliseconds < 3000.0)
        {
            double nowMs = swLoop.Elapsed.TotalMilliseconds;
            // (no work - measures the pacing floor, i.e. best-case fps)
            double remS = TargetMs / 1000.0 - (swLoop.Elapsed.TotalMilliseconds - nowMs) / 1000.0;
            if (remS > 0)
            {
                if (remS > spinS)
                    Thread.Sleep(TimeSpan.FromSeconds(remS - spinS));
                while (swLoop.Elapsed.TotalMilliseconds < nowMs + TargetMs) { }
            }
            iters++;
        }
        Console.WriteLine($"Pacing loop (SpinS={spinS * 1000:F1}ms, no work): " +
                          $"{iters / swLoop.Elapsed.TotalSeconds:F1} Hz over {swLoop.Elapsed.TotalMilliseconds / 1000.0:F2} s");
    }
    return;
}

// --- Load road data ------------------------------------------------------------

RoadNetwork network;
if (mapName is not null)
{
    Console.WriteLine($"Loading synthetic test map: '{mapName}'");
    network = TestMaps.BuildTestMap(mapName);
    Console.WriteLine($"  {network.Nodes.Count} nodes, {network.Segments.Count} segments");
}
else
{
    Console.WriteLine("Loading OSM data…");
    var osmData = OsmLoader.LoadFromCache(
        Config.BOUNDING_BOX_NORTH, Config.BOUNDING_BOX_SOUTH,
        Config.BOUNDING_BOX_WEST, Config.BOUNDING_BOX_EAST)
        ?? throw new InvalidOperationException(
            "no cached OSM data - run the Python fetch once to populate data/osm_cache/");
    network = RoadNetwork.FromOsmData(osmData,
        Config.BOUNDING_BOX_NORTH, Config.BOUNDING_BOX_SOUTH,
        Config.BOUNDING_BOX_WEST, Config.BOUNDING_BOX_EAST);
}

// --- Obstacles (docs/OBSTACLES.md): layouts save per map name -------------------

string mapLabel = mapName ?? "kleinmachnow";
var obstacleMgr = new ObstacleManager(mapLabel);

// --- Capacity benchmark (--bench): the sim's own throughput measure -------------
// This measures the SIM, not the client, so it lives on the C# side: no REST
// API, no Godot, no HTTP round-trips - just the engine and a stopwatch. It
// replaces tools/capacity_sweep.py, which measured the same thing over the
// wire (and paid the client's cost on top).
//
// For each phase: N cars evenly around a named loop, rolling start, throttle
// held, then as many fixed 60 Hz steps as the CPU can do. pace = sim time
// advanced / wall time spent. pace >= 1 means the sim still holds real time;
// below 1 it runs in slow motion (the accumulator caps substeps, so it
// degrades instead of leaping). The loop is found by node-name prefix so the
// benchmark says which track it actually drove.
if (benchMode)
{
    if (mapName is null)
    {
        Console.WriteLine("--bench needs --map <name> (the loop is found by node-name prefix)");
        return;
    }
    var loop = new List<int>();
    for (int i = 0; i < network.Segments.Count; i++)
    {
        var s = network.Segments[i];
        if (s.StartNode.StartsWith(benchLoop) && s.EndNode.StartsWith(benchLoop))
            loop.Add(i);
    }
    if (loop.Count == 0)
    {
        Console.WriteLine($"--bench: no loop of '{benchLoop}*' segments on this map");
        return;
    }

    const double BenchDt = 1.0 / 60.0;
    const double WarmupSimS = 8.0;      // spawns settle, raceline cache builds
    const double MeasureSimS = 25.0;    // sim seconds per measurement window
    const double WallCapS = 90.0;       // hang guard per window
    const double RealtimeTolerance = 0.97;
    const double SpawnMps = 50.0 / 3.6;

    var phases = new List<int>();
    foreach (var part in benchPhases.Split(","))
    {
        var t = part.Trim();
        if (t.Length == 0) continue;
        try { phases.Add(int.Parse(t)); }
        catch (Exception) { Console.WriteLine($"--bench: ignoring bad phase '{t}'"); }
    }

    Console.WriteLine($"Capacity benchmark on the '{benchLoop}' loop ({loop.Count} segments):");
    Console.WriteLine($"  phases {benchPhases}, {SpawnMps * 3.6:F0} km/h rolling start, " +
                      "throttle held, no pacing (as many 60 Hz steps as the CPU can do).");
    Console.WriteLine("  in-process: no REST API, no client - this measures the sim alone.\n");

    var ceiling = 0;
    foreach (var n in phases)
    {
        Console.WriteLine($"--- {n} cars");
        // A fresh engine per phase: no leftovers, and the raceline cache starts
        // cold like a real run does.
        var eng = new SimEngine(network, obstacleMgr) { CollisionsEnabled = collisionsEnabled };
        for (int i = 0; i < n; i++)
        {
            int seg = loop[(int)System.Math.Round(i * (double)loop.Count / n) % loop.Count];
            eng.EnqueueCommand(new TeleportCommand(null, seg, 0.5, true, SpawnMps));
        }
        for (int k = 0; k < (int)(WarmupSimS / BenchDt); k++)
            eng.Tick(BenchDt);
        foreach (var uid in eng.Cars.Keys.ToList())
            eng.SetControl(uid, new Dictionary<string, object?> { ["accelerate"] = true });

        double sim = 0.0;
        var swBench = Stopwatch.StartNew();
        while (sim < MeasureSimS)
        {
            eng.Tick(BenchDt);
            sim += BenchDt;
            if (swBench.Elapsed.TotalSeconds > WallCapS) break;
        }
        double wall = swBench.Elapsed.TotalSeconds;
        double pace = wall > 0.0 ? sim / wall : 0.0;
        bool holds = pace >= RealtimeTolerance;
        if (holds)
            ceiling = n;
        Console.WriteLine($"   pace {pace:F3}  ({sim:.1f} s sim / {wall:.1f} s wall) -> " +
                          $"{(holds ? "real-time" : "slow motion")}");
    }

    Console.WriteLine();
    Console.WriteLine(ceiling > 0
        ? $"REAL-TIME CEILING (pace >= {RealtimeTolerance:F2}): {ceiling} cars"
        : "No phase held real time.");
    Console.WriteLine($"NB: the sim is single-threaded - this is ONE core's speed, " +
                      $"not the machine's ({phases[^1]} cars is where it stops holding).");
    return;
}

// --- Engine + initial car(s) ------------------------------------------------------

var engine = new SimEngine(network, obstacleMgr) { CollisionsEnabled = collisionsEnabled };

Car? initialCar = null;
if (mapName is not null)
{
    // Test maps do NOT auto-spawn a car: the e2e suite teleports its own car
    // in, and an idle AI car driving around before the first teleport is
    // noise that makes every run less reproducible. The view is the client's
    // Camera2D - point it with the client's own --center/--zoom.
    if (startName is not null && !network.StartPoints.ContainsKey(startName))
        Console.WriteLine($"⚠️  Unknown start point '{startName}' - ignoring it");
    Console.WriteLine("No car spawned (test map - the e2e suite teleports one in; " +
                      "use POST /teleport)");
}
else
{
    // Real OSM data: random spawn as before.
    initialCar = engine.SpawnInitialCar(startName);
    if (startName is not null)
        Console.WriteLine($"Spawn: '{startName}' (deterministic; --start <name> to change)");
}
Console.WriteLine("Navigation model: BICYCLE (kinematic, free particle)");

// --- View ---------------------------------------------------------
// The camera is a client feature (Godot Camera2D): the sim has no view
// state, so nothing here opens a window or moves a camera.

// --- REST API (embedded; the Python server's port-5001 contract) ---------------------

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();                 // keep stdout clean for tests
builder.WebHost.UseUrls($"http://127.0.0.1:{apiPort}");
var webApp = builder.Build();
GameApi.MapEndpoints(webApp, engine);
// Observe the start task: a discarded StartAsync() hides port conflicts -
// the process would print "started" and then serve nothing on :5001 while
// an old instance keeps answering (bit us during Phase 5 gate testing).
var startTask = webApp.StartAsync();
await Task.Delay(200);   // Kestrel binds during StartAsync; give it a moment
if (startTask.IsFaulted)
    throw startTask.Exception!.InnerException!;
startTask.ContinueWith(t =>
{
    if (t.IsFaulted)
        Console.Error.WriteLine($"⚠️  REST API failed: {t.Exception!.GetBaseException().Message}");
}, TaskScheduler.Default);
Console.WriteLine($"🌐 REST API started on http://127.0.0.1:{apiPort}");
Console.WriteLine($"   Health check: http://127.0.0.1:{apiPort}/health");
Console.WriteLine($"   Game state:   http://127.0.0.1:{apiPort}/state");

// --- Sim thread (60 Hz pacing) ------------------------------------------------------

// SIGTERM/SIGINT must stop the process: the WebApplication's own graceful
// shutdown only stops Kestrel - with an infinite sim loop in Main, a plain
// `kill <pid>` would leave the process alive holding :5001 (observed during
// Phase 5 gate testing).
var stopCts = new CancellationTokenSource();
using var termReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
    ctx => { ctx.Cancel = true; stopCts.Cancel(); });
using var intReg = PosixSignalRegistration.Create(PosixSignal.SIGINT,
    ctx => { ctx.Cancel = true; stopCts.Cancel(); });

const double DtFixed = 1.0 / 60.0;
// Spin window: absorbs the Thread.Sleep overshoot. Measured on this Mac with
// --measure-sleep (2026-09-05): .NET sleep(16.7 ms) overshoots by ~3.3 ms
// consistently (p50 3.34, max 3.38) - much tighter than CPython's ~8 ms.
// Pacing holds exactly 60.0 Hz for any spin window >= 4 ms; 0 ms (pure sleep)
// drifts to ~51 Hz. 4 ms is the smallest window that stays on-target, and it
// busy-waits less than Python's 12 ms.
const double SpinS = 0.004;

// The engine publishes this in /state so the client and the e2e suite can
// pace on SIM time; --fast reports 0 = "unbounded, measure it".
engine.TimeWarp = fastMode ? 0.0 : warp;
if (fastMode)
    Console.WriteLine("⚡ Time warp: UNBOUNDED (--fast) - as many 60 Hz steps as the CPU can do. " +
                      "Headless/CI only: at that rate the picture would only strobe.");
else if (warp > 1.0)
    Console.WriteLine($"⚡ Time warp: {warp:F1}x — {warp:F1} s of sim time per wall second " +
                      $"({(int)System.Math.Round(warp)} fixed steps per cycle, step still {DtFixed:F4} s).");

var sw = Stopwatch.StartNew();
double lastCycleStartMs = 0.0;
long frame = 0;
while (!stopCts.IsCancellationRequested)
{
    frame++;
    if (smokeFrames > 0 && frame > smokeFrames) break;

    // Pacing: measure the whole previous cycle (work + sleep) at the top, do
    // the work, then sleep out the remainder of THIS frame's 60 Hz budget at
    // the bottom. Anchoring the sleep target one cycle early instead would
    // halve the steady-state period (~107 fps).
    double nowMs = sw.Elapsed.TotalMilliseconds;
    double dtS = (nowMs - lastCycleStartMs) / 1000.0;
    lastCycleStartMs = nowMs;
    if (smokeFrames > 0)
    {
        // Smoke test: fixed step, no pacing, simulated inputs (the C# port of
        // main.py's smoke keys: UP held, RIGHT after half the run).
        dtS = DtFixed;
        engine.ExternalKeys ??= new Dictionary<string, bool>();
        engine.ExternalKeys[Config.KEY_UP] = true;
        engine.ExternalKeys[Config.KEY_RIGHT] = frame > smokeFrames / 2;
    }

    if (smokeFrames > 0 || warp > 1.0 || fastMode)
    {
        // Fixed steps, no real-time pacing: the smoke test, --warp N (N
        // steps per N-frame budget - the step stays DtFixed, only more of
        // them run per wall second) and --fast (as many as the CPU can do).
        int ticks = fastMode || smokeFrames > 0 ? 1 : (int)System.Math.Round(warp);
        for (int k = 0; k < ticks; k++)
            engine.Tick(DtFixed);
        if (!fastMode && smokeFrames == 0)
        {
            // The budget stays ONE frame (16.7 ms): running N fixed steps
            // inside it is what makes N seconds of sim time pass per wall
            // second. Stretching the budget would just be real time.
            double remS = DtFixed - (sw.Elapsed.TotalMilliseconds - nowMs) / 1000.0;
            if (remS > 0)
            {
                if (remS > SpinS)
                    Thread.Sleep(TimeSpan.FromSeconds(remS - SpinS));
                while (sw.Elapsed.TotalMilliseconds < nowMs + DtFixed * 1000.0) { }
            }
        }
    }
    else
    {
        engine.Tick(dtS);

        double remS = DtFixed - (sw.Elapsed.TotalMilliseconds - nowMs) / 1000.0;
        if (remS > 0)
        {
            if (remS > SpinS)
                Thread.Sleep(TimeSpan.FromSeconds(remS - SpinS));
            while (sw.Elapsed.TotalMilliseconds < nowMs + DtFixed * 1000.0) { }
        }
    }
}

if (smokeFrames > 0)
{
    var primary = engine.FollowUid is not null && engine.Cars.TryGetValue(engine.FollowUid.Value, out var pc)
                  ? pc : engine.Cars.Values.FirstOrDefault();
    if (primary is not null)
        Console.WriteLine($"Smoke test OK: {frame} frames, {engine.Cars.Count} car(s), " +
                          $"primary #{primary.Uid} at ({primary.X:F0}, {primary.Y:F0}), " +
                          $"speed={primary.Speed:F1} m/s");
    else
        Console.WriteLine($"Smoke test OK: {frame} frames, no car on map.");
}

await webApp.StopAsync();
