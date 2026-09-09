// Console host: Sim + REST API on :5000 (headless testing without Godot).
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
int apiPort = 5000, smokeFrames = 0;
bool measureSleep = false;
bool collisionsEnabled = true;
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

// --- Engine + initial car(s) ------------------------------------------------------

var engine = new SimEngine(network, obstacleMgr) { CollisionsEnabled = collisionsEnabled };

Car? initialCar = null;
double? focusX = null, focusY = null;   // (x, y) world position for the initial camera view
if (mapName is not null)
{
    // Test maps do NOT auto-spawn a car: the e2e suite teleports its own car
    // in, and an idle AI car driving around before the first teleport is
    // noise that makes every run less reproducible. --start <name> focuses
    // the CAMERA on that start point (the suite's car then appears exactly
    // there).
    if (startName is not null)
    {
        if (network.StartPoints.ContainsKey(startName))
        {
            var sp = engine.SpawnPosition(startName, 0.5);
            focusX = sp.X; focusY = sp.Y;
            Console.WriteLine($"Camera focused at '{startName}' " +
                              "(no car - the e2e suite teleports one in)");
        }
        else
        {
            Console.WriteLine($"⚠️  Unknown start point '{startName}' - ignoring it");
        }
    }
    else
    {
        Console.WriteLine("No car spawned (test map - the e2e suite teleports one in; " +
                          "use --start <name> to focus the camera, or POST /teleport)");
    }
}
else
{
    // Real OSM data: random spawn as before.
    initialCar = engine.SpawnInitialCar(startName);
    if (startName is not null)
        Console.WriteLine($"Spawn: '{startName}' (deterministic; --start <name> to change)");
}
Console.WriteLine("Navigation model: BICYCLE (kinematic, free particle)");

// --- Camera (mirrored by the remote renderer via /state) --------------------------

if (initialCar is not null)
    engine.Camera.SnapTo(initialCar.X, initialCar.Y, network.WorldWidth, network.WorldHeight);
if (mapName is not null)
{
    // Test maps open at a close driving zoom (7x), focused on the --start
    // point when given, else on the world centre.
    engine.Camera.Zoom = 7.0;
    if (focusX is not null && focusY is not null)
        engine.Camera.SnapTo(focusX.Value, focusY.Value, network.WorldWidth, network.WorldHeight);
    else
        engine.Camera.SnapTo(network.WorldWidth / 2, network.WorldHeight / 2,
                             network.WorldWidth, network.WorldHeight);
}

// --- REST API (embedded; the Python server's port-5000 contract) --------------------

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();                 // keep stdout clean for tests
builder.WebHost.UseUrls($"http://127.0.0.1:{apiPort}");
var webApp = builder.Build();
GameApi.MapEndpoints(webApp, engine);
// Observe the start task: a discarded StartAsync() hides port conflicts -
// the process would print "started" and then serve nothing on :5000 while
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
// `kill <pid>` would leave the process alive holding :5000 (observed during
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

    engine.Tick(dtS);

    if (smokeFrames == 0)
    {
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
                          $"speed={primary.Speed:F1} m/s, zoom={engine.Camera.Zoom:F2}");
    else
        Console.WriteLine($"Smoke test OK: {frame} frames, no car on map, " +
                          $"zoom={engine.Camera.Zoom:F2}");
}

await webApp.StopAsync();
