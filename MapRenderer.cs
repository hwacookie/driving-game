using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// M1 (car/docs/GODOT_FRONTEND.md): fetches GET /map from the world
/// simulator and renders the static world — roads, markings, arrows,
/// P-marks, junction dots. Zero driving logic: pure layer-3 presentation
/// of the layer-2 contract.
///
/// Coordinates: the sim's world is in METRES with x = east, y = north.
/// Godot 2D has y growing DOWN, so every point is flipped: W(x, y) = (x, -y).
/// Camera zoom is therefore directly px-per-metre.
/// </summary>
public partial class MapRenderer : Node2D
{
    const string MapUrl = "http://127.0.0.1:5000/map";
    const string StateUrl = "http://127.0.0.1:5000/state";
    const string RunTestUrl = "http://127.0.0.1:5000/run_test";
    const float Ppm = 2f;   // config.PIXELS_PER_METER (/state is in world pixels)

    static Vector2 W(float x, float y) => new(x, -y);

    /// <summary>One line of marking work (dashed or solid).</summary>
    class MarkingLine
    {
        public List<Vector2> Pts;
        public float DashM, GapM;   // 0/0 for solid
        public Color Color;
        public float WidthM;        // physical width (Breitstrich check)
        public bool Solid;
        public float FadeRefM;      // dash length driving pygame's low-zoom fade
        public bool Elevated;       // drawn above bridge decks (z 21)
    }

    private HttpRequest _http;
    // Test-runner UI (bottom left): number field + "Run Test" button.
    // The sim's POST /run_test launches the EXTERNAL test runner process
    // (tests live outside the sim - they drive it via this API).
    private HttpRequest _cmdHttp;
    private string _pendingCmd = "";      // "post" | "poll"
    private bool _pollActive;
    private LineEdit _testInput;
    private Button _runTestBtn;
    private Label _runStatus;
    private Camera2D _cam;
    private Node2D _world;                 // container for all map nodes
    private Node2D _markLayer;             // rebuilt on zoom change (px floor)
    private Node2D _dotLayer;              // rebuilt on zoom change (px floor)
    private Rect2 _bounds;                  // world bounds in metres
    private readonly HashSet<Key> _keys = new();
    private string _screenshotPath;
    private float _shotDelay = 0.5f;
    // Dev hook: --motionlog <file> writes "ms x y" per car per frame so
    // rendering smoothness can be measured (delta variance = stutter).
    private string _motionLogPath;
    private int _autoRunTest = -1;   // --run-test <N>: auto-click Run Test
    private StreamWriter _motionLog;
    private bool _testPan;
    private Vector2? _overrideCenter;
    private float? _overrideZoom;

    private readonly List<MarkingLine> _markings = new();
    private readonly List<Vector2> _junctionCenters = new();
    private float _dotRadiusM = 0.15f;
    private int _lastLinePx = -1, _lastDotPx = -1;

    // Dash patterns from the sim (set in BuildMap; RebuildDynamic needs them).
    private float _cDash = 3f, _cGap = 3f, _lDash = 2f, _lGap = 4f,
                  _pDash = 1f, _pGap = 1f;

    // pygame fades markings out as their on-screen dash length drops below
    // ~1 px (alpha = 255 * clamp((dash_px - 1) / 3, 0, 1)). The marking
    // meshes and the arrow/P-mark polygons remember which dash length
    // drives their fade; _Process applies it via modulate (no rebuild).
    // Fade refs split in two: static (arrows/P marks, built once) and
    // dynamic (marking meshes — freed on every RebuildDynamic, so the list
    // must be rebuilt with them; holding stale refs throws ObjectDisposed).
    private readonly List<(CanvasItem ci, float refM)> _fadedStatic = new();
    private readonly List<(CanvasItem ci, float refM)> _fadedDyn = new();

    // Minimap (pygame parity: config.MINIMAP_*): screen-space overlay on a
    // CanvasLayer, redrawn whenever the camera moves.
    private MinimapNode _minimap;
    private Label _zoomLabel;
    private Vector2 _lastCamPos = new(float.NaN, float.NaN);
    private float _lastCamZoom = float.NaN;
    // Minimap road network: raw segments in sim coords (x east, y north)
    // with physical width — the minimap draws each as a line with a 1 px
    // floor so every road stays visible at any map scale.
    private readonly List<(Vector2 A, Vector2 B, float W)> _mmSegs = new();

    // Paved-edge outline: fixed 2 screen px white line (pygame parity), so
    // the world-space width must track the zoom every frame.
    private readonly List<Line2D> _edgeLines = new();
    private readonly List<Line2D> _bridgeEdges = new();   // level-1 decks

    // --- M2: car tracking (multi-car ready) ----------------------------
    // Render this far BEHIND the newest sample (interpolation buffer):
    // we always have a full sample pair to lerp between, so network jitter
    // never shows up as a snap; 100 ms is imperceptible in top-down view.
    const float RenderDelaySec = 0.10f;
    const float CarLengthM = 4.4f, CarWidthM = 1.8f;   // config.CAR_*
    const float RearAxleOffsetM = 1.276f;  // config.REAR_AXLE_OFFSET_M
    const float SpriteWheelbaseM = 2.64f;  // config.SPRITE_WHEELBASE_M
    const float TireOutboardM = 0.648f;    // config.TIRE_OUTBOARD_M

    class CarSample
    {
        public double Time;      // sim time (s) from /state
        public float X, Y;       // METRES (converted on parse)
        public float HeadingDeg; // 0 = north, clockwise (sim convention)
        public float SpeedKmh;
        public int Level;
    }

    // DOUBLE pipeline: two HTTPRequest nodes, each response triggers the
    // next request on the OTHER node. A single node can only hold one
    // request in flight and a round trip takes exactly one frame (p50 =
    // 17 ms), so samples landed every other frame; with depth 2 there is
    // always a request in flight while the previous response is processed
    // -> a new sample every render frame.
    private HttpRequest _stateHttpA, _stateHttpB;
    private bool _busyA, _busyB;   // GodotSharp 4.7 has no status query
    private ulong _reqMsA, _reqMsB;

    void RequestState(HttpRequest node)
    {
        bool busy = ReferenceEquals(node, _stateHttpA) ? _busyA : _busyB;
        if (busy) return;   // that node already has a request in flight
        var now = Time.GetTicksMsec();
        if (ReferenceEquals(node, _stateHttpA)) { _busyA = true; _reqMsA = now; }
        else { _busyB = true; _reqMsB = now; }
        node.Request(StateUrl);
    }
    private readonly Dictionary<long, List<CarSample>> _carBuf = new();
    private readonly Dictionary<long, Node2D> _carNodes = new();
    private long? _followUid;      // camera bound to this car (null = free)
    private bool _autoFollow = true;   // until the user takes manual control
    private double _lastMaxTime = double.NegativeInfinity;
    // Wall-clock driven render target: rt advances with REAL time between
    // packet arrivals (clamped to received data) instead of jumping forward
    // whenever a /state response lands - that jump/freeze pattern was the
    // stutter. The 100 ms delay guarantees ~6 samples of history behind
    // the target to interpolate from (classic netcode buffer).
    // Smooth sim-time estimate: advances every frame at an estimated
    // sim-rate that is updated ONCE PER SECOND from a 1 s window. Fast
    // arrival jitter (bursts, empty frames) never touches it - chasing
    // the instantaneous newest-sample error made the target oscillate
    // (jump after bursts, decelerate after gaps). Only sustained drift
    // (sim slower/faster than real time) corrects the rate.
    private double _simNow = double.NegativeInfinity;
    private double _lastFrameWall = 0;
    private double _rateRefSim = -1;     // newest sample at last rate update
    private double _rateRefWall = 0;
    private double _simRate = 1.0;       // sim seconds per wall second
    private float _renderTarget = float.NegativeInfinity;
    private bool _rtClamped;             // render target waiting for data
    // Camera samples for the same interpolation (world px + zoom).
    private readonly List<(double T, float X, float Y, float Zoom)> _camBuf
        = new();
    private Label _speedLabel;
    private string _lastSpeedText = "";
    private Label _testLabel;          // "5/21" from POST /label (via /state)
    private string _lastTestText = null;

    // Follow mode mirrors the SIM's own camera (pygame parity: same lerp
    // follow, snap on teleport, zoom level - all computed in the sim).
    private float _simCamX, _simCamY;   // world pixels
    private float _simCamZoom;          // pygame multiplier (x Ppm -> px/m)
    private bool _simCamValid;
    // Manual camera input pauses the mirror temporarily (pygame: follow
    // resumes as soon as the drag ends) instead of releasing it forever.
    private ulong _lastManualMs = 0;
    private float _localZoom = -1f;     // wheel/pinch override while following

    // Per-car display state from /state (multi-car, docs/MULTI_CAR_PLAN.md):
    // color name, blinker/hazard lights, the car's OWN test flags + label.
    private class CarMeta
    {
        public string Color = "red";
        public bool BlinkL, BlinkR, Hazard;
        public (float X, float Y, float Hdg)? FlagGreen, FlagRed;
        public string HudLabel;
    }
    private readonly Dictionary<long, CarMeta> _carMeta = new();
    // Small per-car HUD label in world space above the car's nose.
    private readonly Dictionary<long, Label> _carLabels = new();
    // Corner light nodes per car (Variant can't hold object arrays, so no
    // SetMeta - plain dictionaries).
    private readonly Dictionary<long, Polygon2D[]> _blinkL = new();
    private readonly Dictionary<long, Polygon2D[]> _blinkR = new();

    // Car colors: the old pygame palette (red = player, then the obstacle
    // palette). Sprite variants are pre-tinted PNGs generated with the old
    // pygame tint formula (see driving-game asset generation note in the
    // plan doc).
    static readonly Dictionary<string, Color> CarColors = new()
    {
        ["red"] = new(0.706f, 0.118f, 0.118f),      // #B41E1E
        ["blue"] = new(65f / 255f, 105f / 255f, 220f / 255f),
        ["yellow"] = new(235f / 255f, 195f / 255f, 45f / 255f),
        ["white"] = new(238f / 255f, 238f / 255f, 238f / 255f),
    };
    static readonly Dictionary<string, Texture2D> _carTextures = new();
    static Texture2D CarTexture(string color)
    {
        if (_carTextures.Count == 0)
        {
            _carTextures["red"] = GD.Load<Texture2D>("res://assets/car_64x128.png");
            _carTextures["blue"] = GD.Load<Texture2D>("res://assets/car_64x128_blue.png");
            _carTextures["yellow"] = GD.Load<Texture2D>("res://assets/car_64x128_yellow.png");
            _carTextures["white"] = GD.Load<Texture2D>("res://assets/car_64x128_white.png");
        }
        return color != null && _carTextures.TryGetValue(color, out var t)
            ? t : _carTextures["red"];
    }

    // Breadcrumb trail (rear-axle points in metres + heading deg) and the
    // four wheel-track Line2Ds derived from it.
    // Breadcrumbs are a pure visual: recorded CLIENT-side from the samples
    // we already receive (no /state payload). Gated on sim time so freezes
    // record nothing; cleared on teleport (big position jump).
    private const int TrailMaxPoints = 500;
    private const float TrailIntervalSec = 0.1f;
    private readonly Dictionary<long, List<(float X, float Y, float Hdg)>> _trails
        = new();
    private readonly Dictionary<long, double> _lastTrailTime = new();
    private Node2D[] _trailLines = new Node2D[4];
    private (float X, float Y, float Hdg)? _lastTrailTail;
    private float _lastTrailZoom = -1f;

    // Test flags (green start / red end pennants), world metres + heading.
    // Legacy single-car sim: root-level pennants. Multi-car sims carry the
    // flags per car in _carMeta instead.
    private (float X, float Y, float Hdg)? _flagGreen, _flagRed;
    private readonly List<CanvasItem> _flagNodes = new();
    private string _lastFlagFp = "";   // fingerprint of all visible pennants

    public override void _Ready()
    {
        _http = GetNode<HttpRequest>("Http");
        // use_threads: without it, Godot's HTTPRequest does ONE socket read
        // per rendered frame (godotengine/godot#120425) - every request then
        // costs several frames (~85-210 ms measured on this machine for a
        // localhost reply that takes 0.4 ms from Python). Threaded mode
        // drains the socket at OS speed; callbacks still fire on main.
        _http.UseThreads = true;
        _cam = GetNode<Camera2D>("Cam");
        _world = new Node2D { Name = "World" };
        AddChild(_world);
        _markLayer = new Node2D { Name = "Markings" };
        _dotLayer = new Node2D { Name = "JunctionDots" };
        _world.AddChild(_markLayer);
        _world.AddChild(_dotLayer);
        _http.RequestCompleted += OnMapResponse;
        FetchMap();

        // /state polling (pipelined, see below):
        // UseThreads: without it, Godot's HTTPRequest does ONE socket read
        // per rendered frame (godotengine/godot#120425) - every request then
        // costs several frames (~85-210 ms measured on this machine for a
        // localhost reply that takes 0.4 ms from Python). Threaded mode
        // drains the socket at OS speed; callbacks still fire on main.
        //
        // PIPLINED polling: the next request goes out IMMEDIATELY when a
        // response lands (no timer alignment). A 60 Hz timer + one-request-
        // in-flight guard left 2-10 frame gaps whenever a round trip took
        // two frames (p50 latency == one frame), and each gap showed up as
        // freeze-then-catch-up stutter.
        _stateHttpA = new HttpRequest { UseThreads = true, Timeout = 1.0 };
        _stateHttpB = new HttpRequest { UseThreads = true, Timeout = 1.0 };
        AddChild(_stateHttpA);
        AddChild(_stateHttpB);
        _stateHttpA.RequestCompleted += (id, code, hdr, body) =>
            OnStateResponse(code, body, _stateHttpB);
        _stateHttpB.RequestCompleted += (id, code, hdr, body) =>
            OnStateResponse(code, body, _stateHttpA);
        RequestState(_stateHttpA);

        // Test-runner UI + its own HTTP node (the map/state nodes are busy
        // with their pipelines).
        _cmdHttp = new HttpRequest { UseThreads = true, Timeout = 5.0 };
        AddChild(_cmdHttp);
        _cmdHttp.RequestCompleted += (id, code, hdr, body) =>
            OnCmdResponse(code, body);
        BuildRunTestUi();


        // User args after "--": --screenshot <path> saves the viewport a
        // moment after the map is built (parity check vs pygame) and then
        // QUITS — screenshot runs are self-terminating, so nothing has to
        // be killed from outside (a SIGTERM kills .NET's exit path into an
        // abort; see 2026-08-31 crash reports). --center <x> <y> and
        // --zoom <z> override the initial fit view.
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--screenshot")
                _screenshotPath = args[i + 1];
            else if (args[i] == "--delay" && i + 1 < args.Length)
                _shotDelay = float.Parse(args[i + 1]);
            else if (args[i] == "--center" && i + 3 < args.Length)
            {
                _overrideCenter = W(float.Parse(args[i + 1]), float.Parse(args[i + 2]));
                i += 2;
            }
            else if (args[i] == "--zoom" && i + 1 < args.Length)
                _overrideZoom = float.Parse(args[i + 1]);
            else if (args[i] == "--test-pan")
                _testPan = true;
            else if (args[i] == "--motionlog")
                _motionLogPath = args[i + 1];
            else if (args[i] == "--run-test" && i + 1 < args.Length)
                _autoRunTest = int.Parse(args[i + 1]);
        }

        // --run-test <N>: fill the field and fire the same code path as a
        // button click, a moment after the UI is built.
        if (_autoRunTest > 0)
            GetTree().CreateTimer(1.0).Timeout += () =>
            {
                _testInput.Text = _autoRunTest.ToString();
                RunTestFromUi();
            };

        if (_motionLogPath != null)
            _motionLog = new StreamWriter(_motionLogPath, append: false);

        // pygame renders locked to 60 fps; an uncapped Godot loop (113 fps
        // on this machine) has 4-20 ms frame pacing which reads as micro-
        // stutter. Cap to the sim's own rate.
        Engine.MaxFps = 60;

        if (_testPan && _screenshotPath != null)
        {
            // Dev hook: hold Key.D for 1.5 s after load, then shoot —
            // verifies the key -> camera pan path end to end.
            GetTree().CreateTimer(2.0).Timeout += () => _keys.Add(Key.D);
            GetTree().CreateTimer(3.5).Timeout += () => _keys.Remove(Key.D);
            GetTree().CreateTimer(4.0).Timeout += () => SaveShot(_screenshotPath);
        }
    }

    private void FetchMap() => _http.Request(MapUrl);

    private void OnMapResponse(long requestId, long responseCode, string[] headers, byte[] body)
    {
        if (responseCode != 200)
        {
            GD.PrintErr($"GET /map failed (HTTP {responseCode}) — retry in 2 s");
            GetTree().CreateTimer(2.0).Timeout += FetchMap;
            return;
        }
        try
        {
            BuildMap(JsonDocument.Parse(body).RootElement);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"GET /map: bad payload: {e.Message}");
        }
    }

    // ------------------------------------------------------------- map build

    private void BuildMap(JsonElement root)
    {
        // Free the static geometry, but keep the two dynamic layers alive
        // (they are rebuilt by RebuildDynamic, not re-created here).
        foreach (var child in _world.GetChildren())
            if (child != _markLayer && child != _dotLayer)
                child.QueueFree();
        _markings.Clear();
        _junctionCenters.Clear();
        _fadedStatic.Clear();
        _fadedDyn.Clear();
        _lastLinePx = _lastDotPx = -1;
        // The child sweep above also kills trail lines / flag nodes created
        // before the map arrived (state polls start immediately) - drop the
        // dangling references and force a rebuild on the next frame.
        _trailLines = new Node2D[4];
        _lastTrailTail = null;
        _flagNodes.Clear();
        _lastFlagFp = "";
        ClearCars();

        var bg = new Color(34 / 255f, 120 / 255f, 34 / 255f);
        if (root.TryGetProperty("bg_color", out var bgEl))
            bg = FromRgb(bgEl);
        RenderingServer.SetDefaultClearColor(bg);

        var roadColor = new Color(120 / 255f, 120 / 255f, 124 / 255f);
        if (root.TryGetProperty("road_color", out var rcEl))
            roadColor = FromRgb(rcEl);

        // bounds: [xmin, ymin, xmax, ymax] in metres.
        float bx0 = 0, by0 = 0, bx1 = 100, by1 = 100;
        if (root.TryGetProperty("bounds", out var bEl))
        {
            bx0 = bEl[0].GetSingle(); by0 = bEl[1].GetSingle();
            bx1 = bEl[2].GetSingle(); by1 = bEl[3].GetSingle();
        }
        _bounds = new Rect2(bx0, by0, bx1 - bx0, by1 - by0);

        // --- Roads (z 0); holes punched with the bg colour (z 1) — exactly
        // what pygame does (it cannot fill a polygon with a hole in one go).
        if (root.TryGetProperty("roads", out var roadsEl))
            foreach (var r in roadsEl.EnumerateArray())
            {
                AddPolygon(Pts(r.GetProperty("exterior")), roadColor, 0);
                if (r.TryGetProperty("holes", out var holes))
                    foreach (var h in holes.EnumerateArray())
                        AddPolygon(Pts(h), bg, 1);
            }

        // Minimap segments (sim coords, metres).
        _mmSegs.Clear();
        if (root.TryGetProperty("segments", out var segsEl))
            foreach (var s in segsEl.EnumerateArray())
                _mmSegs.Add((new Vector2(s[0].GetSingle(), s[1].GetSingle()),
                             new Vector2(s[2].GetSingle(), s[3].GetSingle()),
                             s[4].GetSingle()));

        // --- Dash patterns from the sim (single source of truth).
        _cDash = 3f; _cGap = 3f; _lDash = 2f; _lGap = 4f; _pDash = 1f; _pGap = 1f;
        if (root.TryGetProperty("marking_style", out var ms))
        {
            _cDash = ms.GetProperty("center_dash_m").GetSingle();
            _cGap = ms.GetProperty("center_gap_m").GetSingle();
            _lDash = ms.GetProperty("lane_dash_m").GetSingle();
            _lGap = ms.GetProperty("lane_gap_m").GetSingle();
            _pDash = ms.GetProperty("park_dash_m").GetSingle();
            _pGap = ms.GetProperty("park_gap_m").GetSingle();
        }

        var white = new Color(1f, 1f, 1f);
        var cream = new Color(251 / 255f, 251 / 255f, 245 / 255f);
        var rail = new Color(183 / 255f, 189 / 255f, 186 / 255f);

        // Centerlines: dashed white, 0.15 m wide (pygame parity).
        if (root.TryGetProperty("centerlines", out var cl))
            foreach (var line in cl.EnumerateArray())
                _markings.Add(new MarkingLine { Pts = Pts(line), DashM = _cDash, GapM = _cGap,
                                                 Color = white, WidthM = 0.15f,
                                                 FadeRefM = _cDash });

        // Elevated centrelines: the ground-level ones under a bridge deck
        // are covered by it (z 2 < 20), so the sim exports the deck's own
        // centreline to be drawn ABOVE the deck.
        if (root.TryGetProperty("elevated_centerlines", out var ecl))
            foreach (var line in ecl.EnumerateArray())
                _markings.Add(new MarkingLine { Pts = Pts(line), DashM = _cDash, GapM = _cGap,
                                                 Color = white, WidthM = 0.15f,
                                                 FadeRefM = _cDash, Elevated = true });

        // Lane markings (RQ 31 styles from the sim).
        if (root.TryGetProperty("lane_markings", out var lm))
            foreach (var mark in lm.EnumerateArray())
            {
                string style = mark.GetProperty("style").GetString();
                float widthM = mark.TryGetProperty("width_m", out var wm) ? wm.GetSingle() : 0.15f;
                var pts = Pts(mark.GetProperty("pts"));
                switch (style)
                {
                    case "dashed":
                        _markings.Add(new MarkingLine { Pts = pts, DashM = _lDash, GapM = _lGap,
                                                         Color = white, WidthM = 0.15f,
                                                         FadeRefM = _lDash });
                        break;
                    case "p_dash":
                        _markings.Add(new MarkingLine { Pts = pts, DashM = _pDash, GapM = _pGap,
                                                         Color = white, WidthM = 0.15f,
                                                         FadeRefM = _pDash });
                        break;
                    case "solid":
                        _markings.Add(new MarkingLine { Pts = pts, Solid = true,
                                                        Color = cream, WidthM = widthM,
                                                        FadeRefM = _lDash });
                        break;
                    case "guardrail":
                        _markings.Add(new MarkingLine { Pts = pts, Solid = true,
                                                        Color = rail, WidthM = 0.15f,
                                                        FadeRefM = _lDash });
                        break;
                }
            }

        // --- Arrows + painted P marks (z 2): plain polygons. Fade refs
        // mirror pygame: arrows ~3 m long, P letters ~2 m tall.
        foreach (var kv in new[] { ("oneway_arrows", 3f), ("parking_marks", 2f) })
            if (root.TryGetProperty(kv.Item1, out var arr))
                foreach (var poly in arr.EnumerateArray())
                    if (AddPolygon(Pts(poly), white, 2) is { } p)
                        _fadedStatic.Add((p, kv.Item2));

        // --- Paved-edge outline (z 3, above markings — pygame draws it
        // after the road tiles): one closed Line2D per unioned ring.
        // Physical width like the centreline - NO 1 px floor (user
        // decision): at low zoom it thins out naturally.
        foreach (var l in _edgeLines) l.QueueFree();
        _edgeLines.Clear();
        foreach (var l in _bridgeEdges) l.QueueFree();
        _bridgeEdges.Clear();
        if (root.TryGetProperty("paved_edge_rings", out var pe))
            foreach (var ring in pe.EnumerateArray())
            {
                var line = new Line2D
                {
                    Points = Pts(ring).ToArray(),
                    Closed = true,
                    DefaultColor = white,
                    Width = EdgeLineWidthM,
                    ZIndex = 3,
                };
                _world.AddChild(line);
                _edgeLines.Add(line);
            }

        // --- Elevated decks (bridges, level >= 1): drawn ABOVE the
        // ground roads AND above ground-level cars (z 20+), so a car
        // driving under a bridge disappears behind the deck. The deck is
        // concrete incl. a 1 m sidewalk per side; the asphalt carriageway
        // (elevated_roadways) sits on top of it; the white boundary line
        // (elevated_edge_lines, OPEN - no transverse cap at the deck
        // ends) follows the same 15 cm inset rule as the ground roads.
        var sidewalk = new Color(0.63f, 0.63f, 0.65f);
        if (root.TryGetProperty("elevated_roads", out var er))
            foreach (var r in er.EnumerateArray())
            {
                AddPolygon(Pts(r.GetProperty("exterior")), sidewalk, 20);
                if (r.TryGetProperty("holes", out var eholes))
                    foreach (var h in eholes.EnumerateArray())
                        AddPolygon(Pts(h), bg, 21);
            }
        if (root.TryGetProperty("elevated_roadways", out var erw))
            foreach (var r in erw.EnumerateArray())
            {
                AddPolygon(Pts(r.GetProperty("exterior")), roadColor, 20);
                if (r.TryGetProperty("holes", out var erholes))
                    foreach (var h in erholes.EnumerateArray())
                        AddPolygon(Pts(h), bg, 21);
            }
        if (root.TryGetProperty("elevated_edge_lines", out var eer))
            foreach (var line in eer.EnumerateArray())
            {
                var edge = new Line2D
                {
                    Points = Pts(line).ToArray(),
                    Closed = false,
                    DefaultColor = white,
                    Width = EdgeLineWidthM,     // physical, no 1 px floor
                    ZIndex = 22,
                };
                _world.AddChild(edge);
                _bridgeEdges.Add(edge);
            }

        // --- Junction dots: physical radius with a 1 px screen floor
        // (pygame: max(1, round(r*pppm*zoom)) px) — rebuilt on zoom change.
        if (root.TryGetProperty("junction_dot_radius_m", out var jEl))
            _dotRadiusM = jEl.GetSingle();
        if (root.TryGetProperty("junctions", out var js))
            foreach (var j in js.EnumerateArray())
                _junctionCenters.Add(W(j.GetProperty("x").GetSingle(),
                                       j.GetProperty("y").GetSingle()));

        // Camera: fit the whole map into the viewport (or use overrides).
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float zoom = Mathf.Min(vp.X / _bounds.Size.X, vp.Y / _bounds.Size.Y) * 0.95f;
        var center = W(_bounds.Position.X + _bounds.Size.X / 2f,
                       _bounds.Position.Y + _bounds.Size.Y / 2f);
        if (_overrideZoom.HasValue) zoom = _overrideZoom.Value;
        if (_overrideCenter.HasValue) center = _overrideCenter.Value;
        _cam.Zoom = new Vector2(zoom, zoom);
        _cam.GlobalPosition = center;
        RebuildDynamic();

        // Screen-space overlays: minimap (top right) + zoom indicator
        // (top left, pygame parity: renderer.draw_zoom_indicator).
        if (_minimap == null)
        {
            var layer = new CanvasLayer { Layer = 10 };
            AddChild(layer);
            _minimap = new MinimapNode();
            layer.AddChild(_minimap);

            var sb = new StyleBoxFlat
            {
                BgColor = new Color(20 / 255f, 20 / 255f, 20 / 255f, 180f / 255f),
                ContentMarginLeft = 7, ContentMarginRight = 7,
                ContentMarginTop = 6, ContentMarginBottom = 6,
            };
            _zoomLabel = new Label();
            _zoomLabel.AddThemeStyleboxOverride("normal", sb);
            _zoomLabel.AddThemeColorOverride("font_color",
                new Color(220 / 255f, 220 / 255f, 220 / 255f));
            _zoomLabel.AddThemeFontSizeOverride("font_size", 20);
            _zoomLabel.Position = new Vector2(8, 8); // pygame: (8, 8)
            layer.AddChild(_zoomLabel);

            // Speed readout under the minimap: bound car (or first car).
            _speedLabel = new Label();
            _speedLabel.AddThemeStyleboxOverride("normal", sb);
            _speedLabel.AddThemeColorOverride("font_color",
                new Color(220 / 255f, 220 / 255f, 220 / 255f));
            _speedLabel.AddThemeFontSizeOverride("font_size", 20);
            _speedLabel.Visible = false;
            layer.AddChild(_speedLabel);

            // Test number label (pygame parity: yellow text on dark panel,
            // under the minimap; set by the e2e suite via POST /label).
            _testLabel = new Label();
            _testLabel.AddThemeStyleboxOverride("normal", sb);
            _testLabel.AddThemeColorOverride("font_color",
                new Color(1f, 1f, 0f));
            _testLabel.AddThemeFontSizeOverride("font_size", 24);
            _testLabel.Visible = false;
            layer.AddChild(_testLabel);
        }
        _minimap.Setup(_mmSegs, _bounds, roadColor, _cam);
        _lastCamPos = _cam.GlobalPosition;
        _lastCamZoom = zoom;
        UpdateZoomLabel();
        _minimap.QueueRedraw();

        GD.Print($"Map ready: bounds {_bounds}, zoom {zoom:F3}");

        if (_screenshotPath != null && !_testPan)
            GetTree().CreateTimer(_shotDelay).Timeout += () => SaveShot(_screenshotPath);
    }

    // ------------------------------------------- zoom-dependent (px floor)

    /// <summary>pygame floors marking line widths and junction-dot radii to
    /// whole screen pixels (max(1, ...) px). The world-space meshes cannot
    /// express that directly, so whenever the pixel size changes we rebuild
    /// the marking + dot layers at width = max(physical, 1/zoom) metres.</summary>
    private float _lastRebuildS = 0f;

    private void RebuildDynamic()
    {
        float s = _cam.Zoom.X;                       // px per metre
        int linePx = Mathf.Max(1, (int)(0.15f * s)); // pygame: max(1, int(0.15*pppm*zoom))
        int dotPx = Mathf.Max(1, (int)Mathf.Round(_dotRadiusM * s));
        // The 1 px floor is stored in METRES (linePx / s at build time), so
        // the on-screen width drifts with zoom between integer changes.
        // Pygame recomputes the pixel width every frame; we rebuild whenever
        // the drift exceeds ~8 % of a pixel (invisible) — e.g. the whole
        // range 0.24..6.7 px/m sits in bucket linePx=1 and would otherwise
        // freeze at the fit-zoom width (~2.3 m wide slabs).
        bool drifted = _lastRebuildS > 0f && Mathf.Abs(s - _lastRebuildS) / s > 0.08f;
        if (linePx == _lastLinePx && dotPx == _lastDotPx && !drifted) return;
        _lastLinePx = linePx;
        _lastDotPx = dotPx;
        _lastRebuildS = s;

        foreach (var child in _markLayer.GetChildren())
            child.QueueFree();
        foreach (var child in _dotLayer.GetChildren())
            child.QueueFree();
        _fadedDyn.Clear();

        float wDashM = linePx / s;
        // Keyed by (width, fade ref, COLOR) so each pattern group gets its
        // own mesh + pygame-fade reference. Color must be in the key -
        // without it all markings merge into one white mesh.
        var verts = new Dictionary<(float w, float refM, Color c, bool elev), List<Vector3>>();
        void Quad(Color c, float w, float refM, Vector2 a, Vector2 b, bool elev)
        {
            if (!verts.TryGetValue((w, refM, c, elev), out var list))
            {
                list = new List<Vector3>();
                verts[(w, refM, c, elev)] = list;
            }
            Vector2 d = b - a;
            float len = d.Length();
            if (len < 1e-6f) return;
            Vector2 n = new Vector2(-d.Y, d.X) * (1f / len) * (w / 2f);
            list.Add(new(a.X + n.X, a.Y + n.Y, 0));
            list.Add(new(b.X + n.X, b.Y + n.Y, 0));
            list.Add(new(b.X - n.X, b.Y - n.Y, 0));
            list.Add(new(a.X + n.X, a.Y + n.Y, 0));
            list.Add(new(b.X - n.X, b.Y - n.Y, 0));
            list.Add(new(a.X - n.X, a.Y - n.Y, 0));
        }

        foreach (var line in _markings)
        {
            if (line.Solid)
            {
                // Breitstrich (width_m >= 0.25): twice the normal thickness,
                // with its own 2 px floor — mirrors renderer._draw_solid_polyline.
                float w = line.WidthM >= 0.25f
                    ? Mathf.Max(2, 2 * linePx) / s
                    : wDashM;
                for (int i = 0; i < line.Pts.Count - 1; i++)
                    Quad(line.Color, w, line.FadeRefM, line.Pts[i], line.Pts[i + 1],
                         line.Elevated);
            }
            else if (line.DashM > 0f)
                AddDashes(line.Elevated
                    ? (Color c, float w, float r, Vector2 a, Vector2 b) =>
                        Quad(c, w, r, a, b, true)
                    : (Color c, float w, float r, Vector2 a, Vector2 b) =>
                        Quad(c, w, r, a, b, false),
                    line.Color, wDashM, line.FadeRefM,
                    line.Pts, line.DashM, line.GapM);
        }

        foreach (var kv in verts)
        {
            var mesh = new ImmediateMesh();
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            mesh.SurfaceSetColor(kv.Key.c);
            foreach (var v in kv.Value)
                mesh.SurfaceAddVertex(v);
            mesh.SurfaceEnd();
            // Elevated markings sit above the bridge decks (z 20-22).
            var mi = new MeshInstance2D { Mesh = mesh,
                                          ZIndex = kv.Key.elev ? 21 : 2 };
            _markLayer.AddChild(mi);
            _fadedDyn.Add((mi, kv.Key.refM));
        }

        float rM = dotPx / s;
        foreach (var c in _junctionCenters)
        {
            var pts = new List<Vector2>(24);
            for (int i = 0; i < 24; i++)
            {
                float a = Mathf.Tau * i / 24f;
                pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rM);
            }
            _dotLayer.AddChild(new Polygon2D { Polygon = pts.ToArray(),
                                               Color = new Color(1, 1, 1), ZIndex = 3 });
        }
    }

    // ---------------------------------------------------------------- helpers

    static Color FromRgb(JsonElement el) => new(
        el[0].GetSingle() / 255f, el[1].GetSingle() / 255f, el[2].GetSingle() / 255f);

    static List<Vector2> Pts(JsonElement arr)
    {
        var list = new List<Vector2>(arr.GetArrayLength());
        foreach (var p in arr.EnumerateArray())
            list.Add(W(p[0].GetSingle(), p[1].GetSingle()));
        return list;
    }

    /// <summary>Same as Pts but WITHOUT the y-flip: sim coords (x east,
    /// y north) for screen-space consumers like the minimap.</summary>
    static List<Vector2> SimPts(JsonElement arr)
    {
        var list = new List<Vector2>(arr.GetArrayLength());
        foreach (var p in arr.EnumerateArray())
            list.Add(new Vector2(p[0].GetSingle(), p[1].GetSingle()));
        return list;
    }

    Polygon2D AddPolygon(List<Vector2> pts, Color color, int z)
    {
        if (pts.Count < 3) return null;
        var poly = new Polygon2D { Polygon = pts.ToArray(), Color = color, ZIndex = z };
        _world.AddChild(poly);
        return poly;
    }

    /// <summary>pygame's dash_alpha: fully opaque at >= 4 px dash length,
    /// fully transparent at <= 1 px, linear between.</summary>
    static float DashAlpha(float refM, float pxPerMetre)
    {
        float d = refM * pxPerMetre;
        return Mathf.Clamp((d - 1f) / 3f, 0f, 1f);
    }

    delegate void QuadFn(Color c, float w, float refM, Vector2 a, Vector2 b);

    /// <summary>Walk a polyline at constant arc length emitting dash quads —
    /// mirrors renderer._draw_dashed_polyline so the pattern is identical.</summary>
    static void AddDashes(QuadFn quad, Color c, float widthM, float refM,
                          List<Vector2> pts, float dashM, float gapM)
    {
        float period = dashM + gapM;
        if (period <= 0f) return;
        float intoPeriod = 0f;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 a = pts[i], b = pts[i + 1];
            float segLen = a.DistanceTo(b);
            if (segLen < 1e-6f) continue;
            Vector2 u = (b - a) * (1f / segLen);
            float traveled = 0f;
            while (traveled < segLen)
            {
                float phase = intoPeriod % period;
                bool drawing = phase < dashM;
                float remaining = drawing ? dashM - phase : period - phase;
                float step = Mathf.Min(remaining, segLen - traveled);
                if (drawing)
                    quad(c, widthM, refM, a + u * traveled, a + u * (traveled + step));
                traveled += step;
                intoPeriod += step;
            }
        }
    }

    // ---------------------------------------------------------------- camera

    // Parity with pygame: config.MIN_ZOOM/MAX_ZOOM × PPPM=2 → 0.24–32 px/m.
    const float ZoomMin = 0.24f;
    const float ZoomMax = 64f;   // = 32x in the label (pygame units)
    // Edge line width in METRES: same as the lane centreline. No pixel
    // floor - it scales purely with zoom (user decision).
    const float EdgeLineWidthM = 0.15f;
    // Edge lines are hidden entirely below this zoom (6 px/m = 3x in
    // the label) instead of fading out as sub-pixel noise (user decision).
    const float EdgeLineMinZoom = 6.0f;
    const float ZoomStep = 1.15f; // config.ZOOM_STEP

    bool _dragging;
    Vector2 _dragStartMouse, _dragStartCam;

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey k)
        {
            // NOTE: Key.A.ToString() is "A", not "KeyA" — match on the enum.
            if (k.Pressed)
                _keys.Add(k.Keycode);
            else
                _keys.Remove(k.Keycode);
            // (No ESC-quit: in pygame ESC means freeze, users press it
            // reflexively - closing the window is how you quit.)
            if (k.Pressed && !k.Echo)
            {
                // Pan keys pause the mirror temporarily (pygame parity: it
                // resumes once the input stops) - F releases for real.
                if (k.Keycode is Key.A or Key.D or Key.W or Key.S or
                    Key.Left or Key.Right or Key.Up or Key.Down)
                    _lastManualMs = Time.GetTicksMsec();
                else if (k.Keycode == Key.Tab)   // cycle camera binding
                {
                    if (_carNodes.Count > 0)
                    {
                        var uids = _carNodes.Keys.OrderBy(u => u).ToArray();
                        int i = _followUid.HasValue
                            ? Array.IndexOf(uids, _followUid.Value) : -1;
                        _followUid = uids[(i + 1) % uids.Length];
                    }
                }
                else if (k.Keycode == Key.F)     // toggle follow
                {
                    if (_followUid.HasValue) { _followUid = null; _autoFollow = false; }
                    else if (_carNodes.Count > 0) _followUid = _carNodes.Keys.Min();
                }
            }
        }
        else if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
                ZoomBy(ZoomStep, mb.Position);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
                ZoomBy(1f / ZoomStep, mb.Position);
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                // Drag the map with the left button (same as pygame).
                _dragging = mb.Pressed;
                if (mb.Pressed) _lastManualMs = Time.GetTicksMsec();
                _dragStartMouse = mb.Position;
                _dragStartCam = _cam.GlobalPosition;
            }
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            // Grab behavior: the world point under the cursor stays put.
            // (screen and Godot world both have +y down, so minus on both axes)
            var d = mm.Position - _dragStartMouse;
            _cam.GlobalPosition = new Vector2(
                _dragStartCam.X - d.X / _cam.Zoom.X,
                _dragStartCam.Y - d.Y / _cam.Zoom.Y);
            _lastManualMs = Time.GetTicksMsec();
        }
        else if (e is InputEventMagnifyGesture mag) // two-finger trackpad pinch
            ZoomBy(mag.Factor, mag.Position);
        else if (e is InputEventPanGesture pg) // two-finger trackpad movement
        {
            // The display server scales raw trackpad deltas by 0.03; Godot's
            // own GUI compensates with a x32 multiplier -> screen pixels.
            var px = pg.Delta * 32f;
            if (pg.CtrlPressed || pg.MetaPressed)
            {
                // Ctrl/Cmd + two-finger scroll = zoom (reliable alternative
                // to pinch, which AppKit only classifies as magnify when the
                // fingers spread immediately without sliding). Finger down
                // (= wheel-toward-you parity) zooms in: Delta.Y < 0 then.
                ZoomBy(Mathf.Exp(-px.Y / 600f), pg.Position);
            }
            else
            {
                // Content follows the fingers (natural scrolling).
                // Godot flips the raw macOS delta in processPanEvent, so
                // the camera moves in +Delta direction.
                if (px != Vector2.Zero) _lastManualMs = Time.GetTicksMsec();
                _cam.GlobalPosition += px / _cam.Zoom.X;
            }
        }
    }

    /// <summary>Zoom by f, keeping the world point under `at` fixed.</summary>
    /// <summary>Top-left readout, pygame parity: 'zoom N.Nx (X m wide)'.
    /// The number is in PYGAME zoom units (px/m divided by PPPM=2), so the
    /// same view shows the same value in both frontends.</summary>
    void UpdateZoomLabel()
    {
        if (_zoomLabel == null) return;
        float g = _cam.Zoom.X;                       // px per metre
        float zPygame = g / 2f;                      // PPPM = 2
        float viewWm = GetViewport().GetVisibleRect().Size.X / g;
        _zoomLabel.Text = $"zoom {zPygame:F1}x  ({viewWm:F0} m wide)";
    }

    void ZoomBy(float f, Vector2? at = null)
    {
        float z0 = _cam.Zoom.X;
        float z1 = Mathf.Clamp(z0 * f, ZoomMin, ZoomMax);
        if (Mathf.IsEqualApprox(z1, z0)) return;
        var vp = GetViewport().GetVisibleRect().Size;
        Vector2 s = at ?? vp / 2f;
        _cam.GlobalPosition += (s - vp / 2f) * (1f / z0 - 1f / z1);
        _cam.Zoom = new Vector2(z1, z1);
        // While following, the mirror must keep THIS zoom (pygame: wheel
        // zoom changes the shared camera and follow continues).
        if (_followUid.HasValue) { _localZoom = z1; _lastManualMs = Time.GetTicksMsec(); }
    }

    public override void _Process(double delta)
    {
        var dir = Vector2.Zero;
        if (_keys.Contains(Key.A) || _keys.Contains(Key.Left)) dir.X -= 1;
        if (_keys.Contains(Key.D) || _keys.Contains(Key.Right)) dir.X += 1;
        // "up" is north in the sim's world = -y in Godot.
        if (_keys.Contains(Key.W) || _keys.Contains(Key.Up)) dir.Y -= 1;
        if (_keys.Contains(Key.S) || _keys.Contains(Key.Down)) dir.Y += 1;
        if (dir != Vector2.Zero)
        {
            _cam.GlobalPosition += dir.Normalized() * 600f / _cam.Zoom.X * (float)delta;
            _lastManualMs = Time.GetTicksMsec();
        }

        // M2: interpolate cars from the /state buffer + camera follow.
        UpdateCars();

        if (_motionLog != null)
            foreach (var kv in _carNodes)
                _motionLog.WriteLine($"{Time.GetTicksMsec()} {kv.Key} " +
                    $"{kv.Value.GlobalPosition.X:F2} {kv.Value.GlobalPosition.Y:F2} " +
                    $"{_simNow:F3} {(_rtClamped ? 1 : 0)} {_renderTarget:F4}");

        // Breadcrumb trails + test flags (rebuild on data/zoom change).
        UpdateOverlays();

        // Rebuild markings/dots only when the pixel floor changes.
        RebuildDynamic();

        // Apply pygame's low-zoom fade (cheap: a handful of modulates).
        float s = _cam.Zoom.X;

        // Edge lines: hard cut-off below 1.5x instead of sub-pixel fade.
        bool edgeVisible = s >= EdgeLineMinZoom;
        foreach (var l in _edgeLines)
            l.Visible = edgeVisible;
        foreach (var l in _bridgeEdges)
            l.Visible = edgeVisible;
        foreach (var kv in _fadedStatic)
            kv.ci.Modulate = new Color(1, 1, 1, DashAlpha(kv.refM, s));
        foreach (var kv in _fadedDyn)
            kv.ci.Modulate = new Color(1, 1, 1, DashAlpha(kv.refM, s));

        // Minimap only changes when the camera moves.
        if (_minimap != null &&
            (_cam.GlobalPosition != _lastCamPos || _cam.Zoom.X != _lastCamZoom))
        {
            _lastCamPos = _cam.GlobalPosition;
            _lastCamZoom = s;
            UpdateZoomLabel();
            _minimap.QueueRedraw();
        }
    }

    // ------------------------------------------------- M2: car tracking

    private void OnStateResponse(long responseCode, byte[] body,
                                 HttpRequest next)
    {
        // The node that just completed is the OTHER one (next = its partner).
        if (ReferenceEquals(next, _stateHttpB)) _busyA = false; else _busyB = false;
        if (_motionLog != null)
            _motionLog.WriteLine($"RESP " +
                $"{Time.GetTicksMsec() - (ReferenceEquals(next, _stateHttpB) ? _reqMsA : _reqMsB)}ms");
        if (responseCode == 200)
            RequestState(next);   // double pipeline: next request immediately
        else
            // Failure/timeout: back off briefly (no tight retry loop while
            // the sim is down), then continue the pipeline.
            GetTree().CreateTimer(0.1).Timeout += () => RequestState(next);
        if (responseCode != 200) return;   // keep last state
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            // A response without "time" is not a state frame (e.g. the
            // sim's startup window, where /state was briefly just static
            // data): skip it silently instead of erroring.
            if (!root.TryGetProperty("time", out var tEl)) return;
            double t = tEl.GetDouble();
            // Time went backwards => the sim restarted: wipe everything.
            if (t < _lastMaxTime - 0.5) ClearCars();
            double prevMax = _lastMaxTime;
            _lastMaxTime = Math.Max(_lastMaxTime, t);

            // Snap only on the very first arrival or a big discontinuity
            // (restart/teleport).
            bool newFrame = t > prevMax;
            if (newFrame &&
                (_simNow == double.NegativeInfinity || Math.Abs(t - _simNow) > 1.0))
            {
                _simNow = t;
                _rateRefSim = t;
                _rateRefWall = Time.GetTicksMsec() / 1000.0;
                _simRate = 1.0;
            }

            var seen = new HashSet<long>();
            void Add(long uid, float xPx, float yPx, float hdg,
                     float kmh, int level)
            {
                seen.Add(uid);
                if (!_carBuf.TryGetValue(uid, out var buf))
                    _carBuf[uid] = buf = new List<CarSample>();
                while (buf.Count > 2 && buf[0].Time < t - 1.0) buf.RemoveAt(0);
                // Skip duplicate timestamps: when the sim loop runs slower
                // than 60 Hz, several polls see the SAME frame (time =
                // frame/60). A zero-dt pair would make the interpolation
                // divide by zero -> NaN rotation (sprite spins wildly).
                if (buf.Count > 0 && buf[^1].Time >= t) return;
                buf.Add(new CarSample
                {
                    Time = t, X = xPx / Ppm, Y = yPx / Ppm,
                    HeadingDeg = hdg, SpeedKmh = kmh, Level = level,
                });
                RecordTrail(uid, t, xPx / Ppm, yPx / Ppm, hdg);
                if (_motionLog != null)
                    _motionLog.WriteLine($"SAMP {t:F3} {xPx / Ppm:F2} {yPx / Ppm:F2}");
            }

            void RecordTrail(long uid, double t, float xm, float ym, float hdg)
            {
                if (!_trails.TryGetValue(uid, out var list))
                {
                    list = new List<(float, float, float)>(TrailMaxPoints);
                    _trails[uid] = list;
                    _lastTrailTime[uid] = t - TrailIntervalSec;  // record now
                }
                if (t < _lastTrailTime[uid] + TrailIntervalSec - 1e-4) return;
                if (list.Count > 0)
                {
                    var (lx, ly, _) = list[^1];
                    float dx = xm - lx, dy = ym - ly;
                    if (dx * dx + dy * dy > 20f * 20f) list.Clear();  // teleport
                }
                list.Add((xm, ym, hdg));
                while (list.Count > TrailMaxPoints) list.RemoveAt(0);
                _lastTrailTime[uid] = t;
            }

            if (root.TryGetProperty("cars", out var cars))   // multi-car shape
            {
                bool anyNewCar = false;
                long newestUid = 0;
                foreach (var c in cars.EnumerateArray())
                {
                    long uid = c.GetProperty("car_uid").GetInt64();
                    if (!_carBuf.ContainsKey(uid)) anyNewCar = true;
                    if (uid > newestUid) newestUid = uid;
                    Add(uid,
                        c.GetProperty("x").GetSingle(), c.GetProperty("y").GetSingle(),
                        c.GetProperty("heading").GetSingle(),
                        c.GetProperty("speed_kmh").GetSingle(),
                        c.GetProperty("level").GetInt32());
                    // Per-car display state: color, lights, OWN flags/label.
                    if (!_carMeta.TryGetValue(uid, out var m))
                        _carMeta[uid] = m = new CarMeta();
                    m.Color = c.TryGetProperty("color", out var cc)
                               && cc.ValueKind == JsonValueKind.String
                           ? cc.GetString() : "red";
                    m.BlinkL = c.TryGetProperty("blinker_left", out var bl) && bl.GetBoolean();
                    m.BlinkR = c.TryGetProperty("blinker_right", out var br) && br.GetBoolean();
                    m.Hazard = c.TryGetProperty("hazard", out var hz) && hz.GetBoolean();
                    m.FlagGreen = ParseFlag(c, "green");
                    m.FlagRed = ParseFlag(c, "red");
                    m.HudLabel = c.TryGetProperty("hud_label", out var hlc)
                                  && hlc.ValueKind == JsonValueKind.String
                           ? hlc.GetString() : null;
                }
                // Multi-car auto-follow: a freshly spawned car takes over
                // the camera binding - mirrors the sim, which follows the
                // LAST teleported car. Skipped after F (follow released) or
                // while no new car arrived (TAB stays in effect).
                if (_autoFollow && anyNewCar && newestUid > 0)
                    _followUid = newestUid;
            }
            else if (root.TryGetProperty("has_car", out var hc) && hc.GetBoolean())
                Add(root.GetProperty("car_uid").GetInt64(),
                    root.GetProperty("x").GetSingle(), root.GetProperty("y").GetSingle(),
                    root.GetProperty("heading").GetSingle(),
                    root.GetProperty("speed_kmh").GetSingle(),
                    root.GetProperty("level").GetInt32());

            // Sim camera: follow mode mirrors it exactly (pygame parity -
            // the lerp follow, snap-on-teleport and zoom live in the sim).
            if (root.TryGetProperty("camera_x", out var cx) &&
                root.TryGetProperty("camera_y", out var cy) &&
                root.TryGetProperty("camera_zoom", out var cz))
            {
                _simCamX = cx.GetSingle();
                _simCamY = cy.GetSingle();
                _simCamZoom = cz.GetSingle();
                _simCamValid = true;
                if (newFrame)
                {
                    _camBuf.Add((t, _simCamX, _simCamY, _simCamZoom));
                    while (_camBuf.Count > 2 && _camBuf[0].T < t - 1.0)
                        _camBuf.RemoveAt(0);
                }
            }

            // Blinker state: the multi-car shape carries it per car above;
            // the legacy single-car form uses the top-level fields.
            if (!root.TryGetProperty("cars", out _) &&
                root.TryGetProperty("car_uid", out var bu))
            {
                long uid = bu.GetInt64();
                if (!_carMeta.TryGetValue(uid, out var m))
                    _carMeta[uid] = m = new CarMeta();
                m.BlinkL = root.TryGetProperty("blinker_left", out var b0) && b0.GetBoolean();
                m.BlinkR = root.TryGetProperty("blinker_right", out var b1) && b1.GetBoolean();
                m.Hazard = root.TryGetProperty("hazard", out var b2) && b2.GetBoolean();
            }

            // HUD test label ("5/21") - the PRIMARY car's label, string or null.
            if (root.TryGetProperty("hud_label", out var hl))
                _lastTestText = hl.ValueKind == JsonValueKind.String
                    ? hl.GetString() : null;

            // Test flags: legacy single-car sim puts them at the root; the
            // multi-car shape carries them per car (parsed above), so skip
            // the root pair then - it is the primary's copy and would
            // double-draw.
            if (!root.TryGetProperty("cars", out _))
            {
                _flagGreen = ParseFlag(root, "green");
                _flagRed = ParseFlag(root, "red");
            }

            // Cars missing from this response are gone.
            foreach (var uid in _carBuf.Keys.Where(u => !seen.Contains(u)).ToArray())
                RemoveCar(uid);
        }
        catch (System.Exception e)
        {
            // A state frame always has "time" (checked above), so anything
            // that still throws is an unexpected payload shape - log and
            // keep the last good state.
            GD.PrintErr($"GET /state: bad payload: {e.Message}");
        }
    }

    static (float X, float Y, float Hdg)? ParseFlag(JsonElement root, string key)
    {
        if (!root.TryGetProperty("flags", out var fl) ||
            fl.ValueKind != JsonValueKind.Object || !fl.TryGetProperty(key, out var f))
            return null;
        if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() < 3) return null;
        var a = f.EnumerateArray().ToArray();
        return (a[0].GetSingle() / Ppm, a[1].GetSingle() / Ppm, a[2].GetSingle());
    }

    void RemoveCar(long uid)
    {
        _carBuf.Remove(uid);
        _trails.Remove(uid);
        _lastTrailTime.Remove(uid);
        _carMeta.Remove(uid);
        if (_carLabels.TryGetValue(uid, out var lab))
        { lab.QueueFree(); _carLabels.Remove(uid); }
        _blinkL.Remove(uid);
        _blinkR.Remove(uid);
        if (_carNodes.TryGetValue(uid, out var n))
        { n.QueueFree(); _carNodes.Remove(uid); }
        if (_followUid == uid) _followUid = null;
    }

    void ClearCars()
    {
        foreach (var uid in _carBuf.Keys.ToArray()) RemoveCar(uid);
        _lastFlagFp = "";
        _lastMaxTime = double.NegativeInfinity;
        _simNow = double.NegativeInfinity;
        _rateRefSim = -1;
        _simRate = 1.0;
        _camBuf.Clear();
        _renderTarget = float.NegativeInfinity;
    }

    Node2D MakeCarNode(long uid)
    {
        // Same sprite as pygame (assets/car_64x128.png): nose points UP
        // (north), so rotation_degrees = heading directly. The node sits
        // on the REAR AXLE (/state x/y); the sprite is centred on the BODY
        // centre, RearAxleOffsetM ahead of it.
        var root = new Node2D { Name = $"car_{uid}" };
        // Multi-car: each car gets its own pre-tinted sprite (red = the
        // original; blue/yellow/white generated with the old pygame tint
        // formula). Meta may not exist yet on the very first sample - red
        // is the safe default.
        string color = _carMeta.TryGetValue(uid, out var meta) ? meta.Color : "red";
        // The 64x128 px texture is stretched to the car's physical size
        // (pygame: transform.scale to CAR_WIDTH x CAR_LENGTH in world px).
        // Nose = texture top; with rotation_degrees = heading the nose
        // points along travel (verified pixel-wise against pygame).
        root.AddChild(new Sprite2D
        {
            Texture = CarTexture(color),
            Scale = new Vector2(CarWidthM / 64f, CarLengthM / 128f),
            Position = new Vector2(0, -RearAxleOffsetM),
        });
        // Blinker corner lights (pygame parity): one at each body corner,
        // +/-0.85*L/2 fore-aft and +/-0.75*W/2 lateral around the body
        // centre; orange, 0.5 s blink period.
        float fore = CarLengthM / 2f * 0.85f;
        float lat = CarWidthM / 2f * 0.75f;
        var blinkerL = new Polygon2D[2];
        var blinkerR = new Polygon2D[2];
        for (int i = 0; i < 2; i++)
        {
            float y = -fore * (i == 0 ? 1f : -1f) - RearAxleOffsetM;
            blinkerL[i] = MakeBlinker(new Vector2(-lat, y));
            blinkerR[i] = MakeBlinker(new Vector2(lat, y));
        }
        root.AddChild(blinkerL[0]); root.AddChild(blinkerL[1]);
        root.AddChild(blinkerR[0]); root.AddChild(blinkerR[1]);
        _blinkL[uid] = blinkerL;
        _blinkR[uid] = blinkerR;
        _world.AddChild(root);
        return root;
    }

    static Polygon2D MakeBlinker(Vector2 pos)
    {
        // Unit-radius 8-gon; scaled per frame to the pygame radius.
        var pts = new Vector2[8];
        for (int i = 0; i < 8; i++)
        {
            float a = Mathf.Tau * i / 8f;
            pts[i] = pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }
        return new Polygon2D
        {
            Color = new Color(1f, 0.706f, 0f),   // (255,180,0)
            Polygon = pts,
        };
    }

    void UpdateCars()
    {
        if (_carBuf.Count == 0) { FollowCamera(); return; }
        // Advance at the slowly-updated sim rate. The rate itself is
        // refreshed once per second from a 1 s window, so bursty arrivals
        // (2 samples in one frame, then 3 empty ones) cannot wiggle it.
        double nowWall = Time.GetTicksMsec() / 1000.0;
        if (_simNow > double.NegativeInfinity)
        {
            if (nowWall - _rateRefWall >= 1.0 && _rateRefSim > 0)
            {
                double r = (_lastMaxTime - _rateRefSim) / (nowWall - _rateRefWall);
                if (r > 0.05 && r < 2.0) _simRate = r;   // else keep old
                _rateRefSim = _lastMaxTime;
                _rateRefWall = nowWall;
            }
            _simNow += Math.Max(0, nowWall - _lastFrameWall)
                       * Mathf.Clamp(_simRate, 0.0, 1.5);
        }
        _lastFrameWall = nowWall;
        // Render target: the smooth estimate minus the delay buffer.
        // (No hard clamp to the newest sample - that re-introduced the
        // step; the >200 ms extrapolation cap below bounds starvation.)
        float rt = (float)(_simNow - RenderDelaySec);
        _rtClamped = rt >= _lastMaxTime;
        _renderTarget = rt;

        long? firstUid = null; float firstKmh = 0f;
        foreach (var kv in _carBuf)
        {
            var buf = kv.Value;
            if (buf.Count == 0) continue;

            Vector2 pos; float hdg, kmh; int level;
            var last = buf[^1];
            if (buf.Count < 2 || rt <= buf[0].Time)
            {
                // Not enough history to bracket: freeze on the newest.
                pos = W(last.X, last.Y); hdg = last.HeadingDeg;
                kmh = last.SpeedKmh; level = last.Level;
            }
            else if (rt >= last.Time && rt - last.Time > 0.2)
            {
                // Target ran >200 ms ahead of all data (starvation): hold.
                pos = W(last.X, last.Y); hdg = last.HeadingDeg;
                kmh = last.SpeedKmh; level = last.Level;
            }
            else
            {
                int i = buf.Count - 2;
                // Walk back until a.Time <= rt (the old check on buf[i+1]
                // stopped one step late when sample gaps are uneven, giving
                // f < 0 extrapolation).
                while (i > 0 && buf[i].Time > rt) i--;
                var a = buf[i];
                var b = buf[i + 1];
                double dt = b.Time - a.Time;
                if (dt <= 0)
                {
                    // Defensive: identical timestamps must never divide.
                    pos = W(b.X, b.Y); hdg = b.HeadingDeg;
                    kmh = b.SpeedKmh; level = b.Level;
                }
                else
                {
                    // f in [0,1] = interpolation; f > 1 (rt past the newest
                    // sample) = EXTRAPOLATION / dead reckoning. Freezing on
                    // the newest sample while waiting for the next one made
                    // the car stutter at the effective sample rate (~30 Hz:
                    // move-freeze-move); extrapolating keeps motion smooth
                    // and the PLL's capped pull re-syncs gently when the
                    // next sample lands.
                    float f = (float)((rt - a.Time) / dt);
                    pos = W(Mathf.Lerp(a.X, b.X, f), Mathf.Lerp(a.Y, b.Y, f));
                    hdg = LerpAngleDeg(a.HeadingDeg, b.HeadingDeg, f);
                    kmh = Mathf.Lerp(a.SpeedKmh, b.SpeedKmh, f);
                    level = b.Level;
                }
            }

            if (!_carNodes.TryGetValue(kv.Key, out var node))
                _carNodes[kv.Key] = node = MakeCarNode(kv.Key);
            node.GlobalPosition = pos;
            // Sprite nose points up (north) -> rotation = heading directly.
            node.RotationDegrees = hdg;
            // Above its own deck, below any higher level (M2 occlusion).
            node.ZIndex = 10 + 20 * level;

            // Blinker corner lights: pygame's 0.5 s period, radius clamped.
            if (_carMeta.TryGetValue(kv.Key, out var meta))
            {
                bool on = Time.GetTicksMsec() % 500 <= 250;
                float rPx = Mathf.Clamp(_cam.Zoom.X * 0.4f, 2f, 7f);
                var sc = new Vector2(rPx / _cam.Zoom.X, rPx / _cam.Zoom.X);
                if (_blinkL.TryGetValue(kv.Key, out var bl))
                    foreach (var b in bl)
                        { b.Visible = on && (meta.BlinkL || meta.Hazard); b.Scale = sc; }
                if (_blinkR.TryGetValue(kv.Key, out var br))
                    foreach (var b in br)
                        { b.Visible = on && (meta.BlinkR || meta.Hazard); b.Scale = sc; }

                // Small per-car HUD label above the nose (parallel tests):
                // world-space so it never rotates with the car. The default
                // font is ~16 px = 16 METRES in world space, so scale down
                // to ~1.6 m tall (readable from driving zooms up).
                string lbl = meta.HudLabel;
                if (!string.IsNullOrEmpty(lbl))
                {
                    if (!_carLabels.TryGetValue(kv.Key, out var lab))
                    {
                        lab = new Label
                        {
                            ZIndex = 100,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Scale = new Vector2(0.1f, 0.1f),
                        };
                        _world.AddChild(lab);
                        _carLabels[kv.Key] = lab;
                    }
                    if (lab.Text != lbl) lab.Text = lbl;
                    // Center on the car's axis (Label origin is top-left).
                    lab.Position = pos + new Vector2(
                        -lab.Size.X * 0.1f * 0.5f,
                        -(RearAxleOffsetM + CarLengthM / 2f + 0.6f));
                }
                else if (_carLabels.TryGetValue(kv.Key, out var dead))
                { dead.QueueFree(); _carLabels.Remove(kv.Key); }
            }

            if (firstUid == null) { firstUid = kv.Key; firstKmh = kmh; }
        }

        FollowCamera();

        // Minimap dots: every car in its own color, the bound one gets a
        // yellow ring.
        if (_minimap != null)
        {
            _minimap.Cars.Clear();
            foreach (var kv in _carNodes)
            {
                string col = _carMeta.TryGetValue(kv.Key, out var m)
                    ? m.Color : "red";
                _minimap.Cars.Add((new Vector2(kv.Value.GlobalPosition.X,
                                               -kv.Value.GlobalPosition.Y),
                                   kv.Key == _followUid,
                                   CarColors[col]));
            }
            _minimap.QueueRedraw();
        }

        // Test number under the speed readout (pygame parity).
        if (_testLabel != null && _lastTestText != null)
        {
            bool show = _lastTestText.Length > 0;
            _testLabel.Visible = show;
            if (show && _lastTestText != _testLabel.Text)
            {
                _testLabel.Text = _lastTestText;
                var vp = GetViewport().GetVisibleRect();
                _testLabel.Position = new Vector2(
                    vp.Size.X - MinimapNode.BoxSize - MinimapNode.Margin,
                    MinimapNode.Margin + MinimapNode.BoxSize + 6f
                    + _speedLabel.Size.Y + 6f);
            }
        }

        // Speed readout under the minimap: bound car (or first car).
        if (_speedLabel != null)
        {
            long? pick = _followUid ?? firstUid;
            bool show = pick.HasValue;
            string txt = show ? $"#{pick}  {firstKmh:F0} km/h" : "";
            if (show || _lastSpeedText != "")
            {
                _speedLabel.Visible = show;
                if (txt != _lastSpeedText)
                {
                    _lastSpeedText = txt;
                    _speedLabel.Text = txt;
                    var vp = GetViewport().GetVisibleRect();
                    _speedLabel.Position = new Vector2(
                        vp.Size.X - MinimapNode.BoxSize - MinimapNode.Margin,
                        MinimapNode.Margin + MinimapNode.BoxSize + 6f);
                }
            }
        }
    }

    /// <summary>Follow mode mirrors the SIM's own camera (pygame parity:
    /// same lerp follow, snap on teleport, zoom level). Free after any
    /// manual pan/zoom; Tab/F rebind.</summary>
    void FollowCamera()
    {
        if (_followUid == null && _autoFollow && _carNodes.Count > 0)
            _followUid = _carNodes.Keys.Min();
        // Manual input pauses the mirror for 500 ms (pygame: follow
        // resumes as soon as the drag ends).
        bool holding = Time.GetTicksMsec() - _lastManualMs < 500;
        if (_followUid.HasValue && _simCamValid && !holding)
        {
            // Interpolate the camera like the car (same render target):
            // copying the newest packet value made the view step with
            // every /state response.
            float cxp, cyp, czm;
            var cb = _camBuf;
            if (cb.Count >= 2 && _renderTarget > cb[0].T &&
                _renderTarget < cb[^1].T + 0.2)
            {
                int i = cb.Count - 2;
                while (i > 0 && cb[i].T > _renderTarget) i--;
                var a = cb[i]; var b = cb[i + 1];
                float f = (float)((_renderTarget - a.T) / (b.T - a.T));
                // f > 1 = extrapolate (same as the car: no freeze-stutter)
                cxp = Mathf.Lerp(a.X, b.X, f);
                cyp = Mathf.Lerp(a.Y, b.Y, f);
                czm = Mathf.Lerp(a.Zoom, b.Zoom, f);
            }
            else { cxp = _simCamX; cyp = _simCamY; czm = _simCamZoom; }
            _cam.GlobalPosition = new Vector2(cxp / Ppm, -cyp / Ppm);
            float z = _localZoom > 0
                ? Mathf.Clamp(_localZoom, ZoomMin, ZoomMax)
                : Mathf.Clamp(czm * Ppm, ZoomMin, ZoomMax);
            _cam.Zoom = new Vector2(z, z);
        }
    }

    static float LerpAngleDeg(float a, float b, float f)
    {
        // Shortest signed delta in (-180, 180]. The "- 180" is essential:
        // (b-a+540) % 360 alone maps a small positive diff to ~+180,
        // which made the sprite spin wildly whenever f != 0 (it only
        // looked right at uniform 60 Hz samples where f happened to be 0).
        float d = (b - a + 540f) % 360f - 180f;
        return a + d * f;
    }

    // --- Breadcrumb trails + test flags (pygame parity) ---

    void UpdateOverlays()
    {
        // Detect new points via the TAIL, not the count: once the trail
        // hits its 500-point cap the count never changes again while the
        // oldest point slides out - a count check would freeze the lines.
        var trail = ActiveTrail();
        (float X, float Y, float Hdg)? tail = null;
        if (trail != null && trail.Count > 0) tail = trail[^1];
        float z = _cam.Zoom.X;
        bool drift = _lastTrailZoom > 0 &&
                     Math.Abs(z - _lastTrailZoom) / _lastTrailZoom > 0.08f;
        if (tail != _lastTrailTail || drift)
        {
            RebuildTrails();
            _lastTrailTail = tail;
            _lastTrailZoom = z;
        }
        // Trails follow the owning car's level (hidden under bridges too),
        // one BELOW the car so the sprite paints above its breadcrumbs.
        int lvl = 0;
        foreach (var buf in _carBuf.Values) { lvl = buf[^1].Level; break; }
        foreach (var l in _trailLines)
            if (l != null) l.ZIndex = 9 + 20 * lvl;

        // Flag change detection: legacy root pair, or every car's own
        // pennants (multi-car).
        var fp = new System.Text.StringBuilder();
        if (_carMeta.Count > 0)
            foreach (var kv in _carMeta)
                fp.Append(kv.Key).Append(':')
                  .Append(kv.Value.FlagGreen?.ToString() ?? "-").Append(',')
                  .Append(kv.Value.FlagRed?.ToString() ?? "-").Append(';');
        else
            fp.Append(_flagGreen?.ToString() ?? "-")
              .Append(',').Append(_flagRed?.ToString() ?? "-");
        if (fp.ToString() != _lastFlagFp || drift)
        {
            RebuildFlags();
            _lastFlagFp = fp.ToString();
        }
    }

    List<(float X, float Y, float Hdg)> ActiveTrail()
        => _followUid > 0 && _trails.TryGetValue(_followUid.Value, out var l)
           ? l : null;

    void RebuildTrails()
    {
        // Wheel tracks from rear-axle points: front tyres one wheelbase
        // ahead, each pair TIRE_OUTBOARD_M outboard (pygame draw_trail).
        var trail = ActiveTrail();
        Vector2[][] tracks = null;
        if (trail != null && trail.Count > 1)
        {
            // The car renders at latest_time - RenderDelaySec (100 ms), but
            // the trail's newest point is "now" - without dropping it the
            // trail tip pokes out AHEAD of the delayed sprite. One point is
            // exactly one 0.1 s recording interval, matching the delay.
            int nPts = trail.Count - 1;
            var lists = new List<Vector2>[4];
            for (int i = 0; i < 4; i++) lists[i] = new List<Vector2>(nPts);
            for (int pi = 0; pi < nPts; pi++)
            {
                var (x, y, h) = trail[pi];
                float rad = Mathf.DegToRad(h);
                float fx = Mathf.Sin(rad), fy = Mathf.Cos(rad);    // forward
                float rx = Mathf.Cos(rad), ry = -Mathf.Sin(rad);   // right
                lists[0].Add(W(x + SpriteWheelbaseM * fx - TireOutboardM * rx,
                               y + SpriteWheelbaseM * fy - TireOutboardM * ry));
                lists[1].Add(W(x + SpriteWheelbaseM * fx + TireOutboardM * rx,
                               y + SpriteWheelbaseM * fy + TireOutboardM * ry));
                lists[2].Add(W(x - TireOutboardM * rx, y - TireOutboardM * ry));
                lists[3].Add(W(x + TireOutboardM * rx, y + TireOutboardM * ry));
            }
            tracks = new Vector2[4][];
            for (int i = 0; i < 4; i++) tracks[i] = lists[i].ToArray();
        }

        var colors = new[]
        {
            new Color(1f, 0.314f, 0.314f),    // FL red     (255,80,80)
            new Color(0.314f, 1f, 0.314f),    // FR green   (80,255,80)
            new Color(0.314f, 0.549f, 1f),    // RL blue    (80,140,255)
            new Color(1f, 0.863f, 0f),        // RR yellow  (255,220,0)
        };
        for (int i = 0; i < 4; i++)
        {
            var line = _trailLines[i] as Line2D;
            if (tracks == null)
            {
                if (line != null && GodotObject.IsInstanceValid(line)) line.QueueFree();
                _trailLines[i] = null;
                continue;
            }
            if (line == null || !GodotObject.IsInstanceValid(line))
            {
                line = new Line2D
                {
                    DefaultColor = colors[i],
                    Closed = false,
                };
                _world.AddChild(line);
                _trailLines[i] = line;
            }
            // Update in place (the trail grows ~10x/s; recreating the node
            // every time double-frees it - QueueFree is deferred).
            line.Points = tracks[i];
            line.Width = 2f / _cam.Zoom.X;   // pygame: fixed 2 screen px
        }
    }

    void RebuildFlags()
    {
        foreach (var n in _flagNodes)
            if (GodotObject.IsInstanceValid(n)) n.QueueFree();
        _flagNodes.Clear();
        // Multi-car: every car's OWN pennants; legacy single-car sim: the
        // root-level pair.
        if (_carMeta.Count > 0)
            foreach (var kv in _carMeta)
            {
                DrawFlag(kv.Value.FlagGreen, new Color(0f, 0.784f, 0.314f),
                                         new Color(0f, 0.353f, 0.157f));
                DrawFlag(kv.Value.FlagRed, new Color(0.902f, 0.196f, 0.196f),
                                    new Color(0.471f, 0.078f, 0.078f));
            }
        else
        {
            DrawFlag(_flagGreen, new Color(0f, 0.784f, 0.314f),     // (0,200,80)
                                 new Color(0f, 0.353f, 0.157f));    // (0,90,40)
            DrawFlag(_flagRed, new Color(0.902f, 0.196f, 0.196f),   // (230,50,50)
                                new Color(0.471f, 0.078f, 0.078f)); // (120,20,20)
        }
    }

    void DrawFlag((float X, float Y, float Hdg)? f, Color fill, Color outline)
    {
        if (f == null) return;
        var (x, y, h) = f.Value;
        float rad = Mathf.DegToRad(h);
        float fx = Mathf.Sin(rad), fy = Mathf.Cos(rad);    // forward
        float rx = Mathf.Cos(rad), ry = -Mathf.Sin(rad);   // right
        Vector2 b = W(x + rx * 3f, y + ry * 3f);           // base centre (3 m off)
        var p1 = W(x + rx * 3f - fx * 1.3f, y + ry * 3f - fy * 1.3f);
        var p2 = W(x + rx * 3f + fx * 1.3f, y + ry * 3f + fy * 1.3f);
        var ap = W(x + rx * 5.2f, y + ry * 5.2f);          // apex (2.2 m further)
        var poly = new Polygon2D
        {
            Color = fill,
            Polygon = new[] { b, p1, p2, ap },
            ZIndex = 3,
        };
        var line = new Line2D
        {
            Points = new[] { b, p1, p2, ap },
            DefaultColor = outline,
            Width = 2f / _cam.Zoom.X,   // pygame: fixed 2 screen px
            Closed = true,
            ZIndex = 3,
        };
        _world.AddChild(poly);
        _world.AddChild(line);
        _flagNodes.Add(poly);
        _flagNodes.Add(line);
    }

    void SaveShot(string path)
    {
        _motionLog?.Dispose();
        var img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(path);
        GD.Print(err == Error.Ok ? $"Screenshot saved: {path}" : $"SavePng failed: {err}");
        // Screenshot runs are one-shot: quit cleanly (no external kill).
        GetTree().Quit();
    }

    // ---------------- test-runner UI (bottom left) ----------------

    void BuildRunTestUi()
    {
        var layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);

        var sb = new StyleBoxFlat
        {
            BgColor = new Color(20 / 255f, 20 / 255f, 20 / 255f, 180f / 255f),
            ContentMarginLeft = 7, ContentMarginRight = 7,
            ContentMarginTop = 5, ContentMarginBottom = 5,
        };

        var box = new VBoxContainer();
        // Bottom-left corner (stretch mode is disabled: viewport == window).
        var vp = GetViewport().GetVisibleRect().Size;
        box.Position = new Vector2(8, vp.Y - 96);
        layer.AddChild(box);

        var row = new HBoxContainer();
        box.AddChild(row);

        var cap = new Label { Text = "Test-Nr:" };
        cap.AddThemeFontSizeOverride("font_size", 20);
        row.AddChild(cap);

        _testInput = new LineEdit
        {
            PlaceholderText = "z.B. 22 (Stresstest)",
            MaxLength = 4,
        };
        _testInput.CustomMinimumSize = new Vector2(170, 0);
        row.AddChild(_testInput);

        _runTestBtn = new Button { Text = "Run Test" };
        _runTestBtn.Pressed += RunTestFromUi;
        row.AddChild(_runTestBtn);

        // Enter in the field also starts the test.
        _testInput.TextSubmitted += _ => RunTestFromUi();

        _runStatus = new Label();
        _runStatus.AddThemeStyleboxOverride("normal", sb);
        _runStatus.AddThemeColorOverride("font_color",
            new Color(1f, 1f, 0f));
        _runStatus.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_runStatus);
    }

    void RunTestFromUi()
    {
        if (_testInput == null) return;
        if (!int.TryParse(_testInput.Text.Trim(), out int n) || n < 1)
        {
            SetRunStatus("Bitte eine Test-Nummer eingeben (Liste: GET /tests)",
                ok: false);
            return;
        }
        var body = System.Text.Encoding.UTF8.GetBytes($"{{\"number\": {n}}}");
        _pendingCmd = "post";
        _runTestBtn.Disabled = true;
        SetRunStatus($"Starte Test #{n} …", ok: true);
        // RequestRaw: raw byte body + custom headers (Godot 4.7).
        _cmdHttp.RequestRaw(RunTestUrl,
            new[] { "Content-Type: application/json" },
            HttpClient.Method.Post, body);
    }

    void OnCmdResponse(long code, byte[] body)
    {
        string txt = System.Text.Encoding.UTF8.GetString(body);
        if (_pendingCmd == "post")
        {
            _runTestBtn.Disabled = false;
            int reqNum = int.TryParse(_testInput?.Text.Trim(), out int nn)
                ? nn : 0;
            if (code == 200)
            {
                SetRunStatus($"Test #{reqNum} läuft …", ok: true);
                StartPolling();
            }
            else
            {
                string err = txt;
                try
                {
                    using var doc = JsonDocument.Parse(txt);
                    if (doc.RootElement.TryGetProperty("error", out var e))
                        err = e.GetString() ?? txt;
                }
                catch { /* non-JSON error body: show raw */ }
                SetRunStatus($"Fehler: {err}", ok: false);
            }
            return;
        }
        if (_pendingCmd != "poll") return;

        bool running = false, haveNumber = false;
        int n = 0, rc = -1;
        string logFile = "";
        try
        {
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            running = root.GetProperty("running").GetBoolean();
            if (root.TryGetProperty("number", out var numEl))
            { n = numEl.GetInt32(); haveNumber = true; }
            if (root.TryGetProperty("returncode", out var r) &&
                r.ValueKind == JsonValueKind.Number)
                rc = r.GetInt32();
            if (root.TryGetProperty("log_file", out var lf))
                logFile = lf.GetString() ?? "";
        }
        catch { /* malformed: keep polling */ }

        if (running && haveNumber)
        {
            SetRunStatus($"Test #{n} läuft …", ok: true);
            GetTree().CreateTimer(3.0).Timeout += PollRunStatus;
        }
        else if (!haveNumber)
        {
            // No run has ever been recorded - nothing to track.
            _pollActive = false;
        }
        else if (!running && haveNumber)
        {
            _pollActive = false;
            SetRunStatus(rc == 0
                ? $"Test #{n} fertig ✅ (Log: {logFile})"
                : $"Test #{n} beendet, rc={rc} ❌ (Log: {logFile})",
                ok: rc == 0);
        }
    }

    void StartPolling()
    {
        if (_pollActive) return;
        _pollActive = true;
        PollRunStatus();
    }

    void PollRunStatus()
    {
        _pendingCmd = "poll";
        _cmdHttp.Request(RunTestUrl);
    }

    void SetRunStatus(string text, bool ok)
    {
        if (_runStatus == null) return;
        _runStatus.Text = text;
        _runStatus.AddThemeColorOverride("font_color",
            ok ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.55f, 0.4f));
    }
}

/// <summary>
/// Top-right overview map, pygame parity (renderer.draw_minimap): 180 px
/// box, dark-green background, gray road network, red car dot (M2), yellow
/// viewport rectangle. Lives on a CanvasLayer so it ignores the camera.
/// All input is in SIM coords (x east, y north); the y-flip happens here.
/// </summary>
public partial class MinimapNode : Node2D
{
    public const int BoxSize = 360;  // big enough for the whole track to stay readable
    public const int Margin = 15;
    static readonly Color Bg = new(20 / 255f, 60 / 255f, 20 / 255f);       // MINIMAP_BG
    static readonly Color Border = new(80 / 255f, 80 / 255f, 80 / 255f);  // MINIMAP_BORDER
    static readonly Color CarColor = new(1f, 0f, 0f);                     // MINIMAP_CAR_COLOR


    List<(Vector2 A, Vector2 B, float W)> _segs;
    Rect2 _bounds;      // sim coords: position = (xmin, ymin), size = extent
    Color _road;
    Camera2D _cam;

    // All live cars in SIM coords; the second flag marks the car the
    // camera is bound to (drawn with a yellow ring); the color is the
    // car's own display color (multi-car).
    public List<(Vector2 Pos, bool Followed, Color Col)> Cars = new();

    public void Setup(List<(Vector2 A, Vector2 B, float W)> segs,
                      Rect2 bounds, Color road, Camera2D cam)
    {
        _segs = segs; _bounds = bounds; _road = road; _cam = cam;
    }

    public override void _Draw()
    {
        if (_segs == null || _cam == null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        float mmx = vp.X - BoxSize - Margin, mmy = Margin;
        var box = new Rect2(mmx, mmy, BoxSize, BoxSize);
        DrawRect(box, Bg);

        float sx = BoxSize / _bounds.Size.X;
        float sy = BoxSize / _bounds.Size.Y;
        Vector2 Mm(Vector2 p) => new(
            mmx + (p.X - _bounds.Position.X) * sx,
            mmy + BoxSize - (p.Y - _bounds.Position.Y) * sy);

        // Every road must stay visible: draw each segment with a 1 px floor
        // (wider roads keep their true width when it exceeds 1 px).
        foreach (var (a, b, w) in _segs)
        {
            float lw = Mathf.Max(1f, w * Math.Min(sx, sy));
            DrawLine(Mm(a), Mm(b), _road, lw);
        }

        foreach (var (p, followed, col) in Cars)
        {
            var cp = Mm(p);
            DrawCircle(cp, followed ? 4.5f : 3f, col);
            if (followed)
                DrawCircle(cp, 6.5f, new Color(1f, 1f, 0f), false, 2f);
        }

        // Yellow viewport rectangle (2 px outline). No clip API in C#, so
        // clamp it to the box — at low zoom it alone can exceed the minimap.
        float z = _cam.Zoom.X;
        var c = _cam.GlobalPosition;                 // Godot world (y down)
        Vector2 camSim = new(c.X, -c.Y);             // sim coords
        float wpx = vp.X / z * sx, hpx = vp.Y / z * sy;
        float left = mmx + (camSim.X - vp.X / 2f / z - _bounds.Position.X) * sx;
        float top = mmy + BoxSize - (camSim.Y + vp.Y / 2f / z - _bounds.Position.Y) * sy;
        var vpr = new Rect2(left, top, wpx, hpx).Intersection(box);
        if (vpr.Size != Vector2.Zero)
            DrawRect(vpr, new Color(1f, 1f, 0f), false, 2f);

        DrawRect(box, Border, false, 2f);
    }

}
