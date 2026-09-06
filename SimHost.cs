using Godot;
using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using DrivingGame.Api;
using DrivingGame.Sim;

/// <summary>
/// Phase 7 (ALTERNATIVE run mode, selected at startup with the "source" user
/// arg): runs the simulation IN-PROCESS inside the Godot app and embeds the
/// REST API on the given port. The default "http" source keeps the existing
/// double pipeline to an external server - both communication methods
/// coexist; this node is only created in embedded mode.
///
/// Mirrors the console host (DrivingGame.Server/Program.cs) 1:1:
/// - same map / spawn / camera initialisation
/// - sim on a DEDICATED background thread with its own 60 Hz pacing
///   (sleep + spin window, re-measured for .NET on this machine) — fixed-
///   timestep physics stays independent of render hitches (tile generation,
///   window drags, GC); "in one app" does NOT mean same thread
/// - REST API embedded so external test injection keeps working (run_e2e.sh
///   --embedded, the Run-Test UI)
/// The renderer reads Engine.StateSnapshot() directly per frame — no HTTP.
/// </summary>
public partial class SimHost : Node
{
    public SimEngine Engine { get; private set; }

    private readonly string _mapName;
    private readonly int _port;
    private WebApplication _webApp;
    private Thread _simThread;
    private volatile bool _running;

    public SimHost(string mapName, int port)
    {
        _mapName = mapName;
        _port = port;
    }

    public override void _Ready()
    {
        // --- Load road data (mirror of Program.cs) -------------------------
        RoadNetwork network;
        if (_mapName is not null)
        {
            GD.Print($"Loading synthetic test map: '{_mapName}'");
            network = TestMaps.BuildTestMap(_mapName);
            GD.Print($"  {network.Nodes.Count} nodes, {network.Segments.Count} segments");
        }
        else
        {
            GD.Print("Loading OSM data…");
            var osmData = OsmLoader.LoadFromCache(
                Config.BOUNDING_BOX_NORTH, Config.BOUNDING_BOX_SOUTH,
                Config.BOUNDING_BOX_WEST, Config.BOUNDING_BOX_EAST)
                ?? throw new InvalidOperationException(
                    "no cached OSM data - run the Python fetch once to populate data/osm_cache/");
            network = RoadNetwork.FromOsmData(osmData,
                Config.BOUNDING_BOX_NORTH, Config.BOUNDING_BOX_SOUTH,
                Config.BOUNDING_BOX_WEST, Config.BOUNDING_BOX_EAST);
        }

        // Obstacle layouts save per map name (same label as the console host).
        var obstacleMgr = new ObstacleManager(_mapName ?? "kleinmachnow");
        Engine = new SimEngine(network, obstacleMgr);

        // --- Spawn + camera (mirror of Program.cs) --------------------------
        if (_mapName is not null)
        {
            // Test maps do NOT auto-spawn a car: the e2e suite teleports its
            // own car in (same as the console host). Open at driving zoom.
            Engine.Camera.Zoom = 7.0;
            Engine.Camera.SnapTo(network.WorldWidth / 2, network.WorldHeight / 2,
                                 network.WorldWidth, network.WorldHeight);
        }
        else
        {
            var initialCar = Engine.SpawnInitialCar(null);
            Engine.Camera.SnapTo(initialCar.X, initialCar.Y,
                                 network.WorldWidth, network.WorldHeight);
        }
        GD.Print("Navigation model: BICYCLE (kinematic, free particle)");

        // --- Embedded REST API (external test injection) --------------------
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();   // keep stdout clean for tests
            builder.WebHost.UseUrls($"http://127.0.0.1:{_port}");
            _webApp = builder.Build();
            GameApi.MapEndpoints(_webApp, Engine);
            // Returns once Kestrel is up (throws on bind failure).
            _webApp.StartAsync().GetAwaiter().GetResult();
            GD.Print($"🌐 REST API embedded on http://127.0.0.1:{_port} " +
                     $"(health: /health, state: /state)");
        }
        catch (Exception e)
        {
            // A squatted port must not kill the game - but external test
            // injection is then unavailable in this process.
            GD.PrintErr($"⚠️  embedded REST API FAILED on :{_port}: " +
                        $"{e.GetBaseException().Message}");
        }

        // --- Sim thread (60 Hz pacing, mirror of Program.cs) -----------------
        _running = true;
        _simThread = new Thread(SimLoop) { IsBackground = true, Name = "sim" };
        _simThread.Start();
    }

    private void SimLoop()
    {
        const double DtFixed = 1.0 / 60.0;
        // Spin window: absorbs the .NET Thread.Sleep overshoot (~3.3 ms on
        // this Mac, measured with --measure-sleep). Same value as the console
        // host - holds exactly 60.0 Hz for any window >= 4 ms.
        const double SpinS = 0.004;

        var sw = Stopwatch.StartNew();
        double lastCycleStartMs = 0.0;
        while (_running)
        {
            // Pacing: measure the whole previous cycle at the top, do the
            // work, then sleep out the remainder of THIS frame's budget.
            double nowMs = sw.Elapsed.TotalMilliseconds;
            double dtS = (nowMs - lastCycleStartMs) / 1000.0;
            lastCycleStartMs = nowMs;

            Engine.Tick(dtS);

            double remS = DtFixed - (sw.Elapsed.TotalMilliseconds - nowMs) / 1000.0;
            if (remS > 0)
            {
                if (remS > SpinS)
                    Thread.Sleep(TimeSpan.FromSeconds(remS - SpinS));
                while (sw.Elapsed.TotalMilliseconds < nowMs + DtFixed * 1000.0) { }
            }
        }
    }

    public override void _ExitTree()
    {
        _running = false;
        _simThread?.Join(TimeSpan.FromSeconds(2));
        // Fire-and-forget: the process is going down anyway, this just keeps
        // Kestrel from logging shutdown noise.
        try { _webApp?.StopAsync(); } catch { }
    }
}
