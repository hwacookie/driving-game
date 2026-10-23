using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// M1 (car/docs/GODOT_FRONTEND.md): fetches GET /map from the world
/// simulator and renders the static world — roads, markings, arrows,
/// P-marks. Zero driving logic: pure layer-3 presentation of the
/// layer-2 contract.
///
/// Coordinates: the sim's world is in METRES with x = east, y = north.
/// Godot 2D has y growing DOWN, so every point is flipped: W(x, y) = (x, -y).
/// Camera zoom is therefore directly px-per-metre.
/// </summary>
public partial class MapRenderer : Node2D
{
    const string MapUrl = "http://127.0.0.1:5001/map";
    const string StateUrl = "http://127.0.0.1:5001/state";
    const string RunTestUrl = "http://127.0.0.1:5001/run_test";
    // /state carries METRES (same unit as /map and as this node's own
    // world coordinates), so there is no pixels-per-meter factor here.

    // Phase 7: communication method, selected at startup (user args after
    // "--"). "http" (default) = the existing double pipeline to an external
    // server on :5001; "embedded" = the sim runs IN-PROCESS (SimHost node),
    // state is read directly per frame, and the REST API is embedded in this
    // process for external test injection. Both methods coexist.
    private string _source = "http";
    private string _embeddedMapName;   // null = OSM (console-host parity)
    private int _embeddedPort = 5001;
    private SimHost _simHost;          // non-null in embedded mode

    static Vector2 W(float x, float y) => new(x, -y);

    /// <summary>One line of marking work (dashed or solid).</summary>
    class MarkingLine
    {
        public List<Vector2> Pts;
        public float DashM, GapM;   // 0/0 for solid
        public Color Color;
        public float WidthM;        // physical width of the paint
        public bool Breitstrich;    // lane-boundary paint drawn twice as wide
        public bool Solid;
        public bool Elevated;       // drawn above bridge decks (z 21)
        public bool FarCull;        // hidden below MarkingMinZoom (see _farMarkings)
    }

    private HttpRequest _http;
    // Test-runner UI (bottom left): number field + "Run Test" button.
    // The sim's POST /run_test launches the EXTERNAL test runner process
    // (tests live outside the sim - they drive it via this API).
    private HttpRequest _cmdHttp;
    private string _pendingCmd = "";      // "post" | "poll"
    private bool _pollActive;
    private int _pollGen;                 // bump per polling chain - stale CreateTimer callbacks die
    private bool _cmdHttpBusy;            // _cmdHttp handles ONE request at a time
    private int _clickPendingNum = 0;     // >0: a Run Test click waits for the in-flight response to land
    private LineEdit _testInput;
    private Button _runTestBtn;
    private Label _runStatus;
    private Camera2D _cam;
    private Node2D _world;                 // container for all map nodes
    private Node2D _markLayer;             // world-space markings, built once
    private Rect2 _bounds;                  // world bounds in metres
    private readonly HashSet<Key> _keys = new();
    private string _screenshotPath;
    private float _shotDelay = 0.5f;
    private Vector2I _resizeTo;        // --resize <w> <h>: grow the window after start
    private double _testSeek = -1.0;   // --test-seek <frac>: click the scrub bar
    private Vector2 _testMmPan;        // --test-minimap-pan <fx> <fy>: drive the view box
    private double _testScrubDrag = -1.0;  // --test-scrub-drag <frac>: drag in the bar
    // Dev hook: --motionlog <file> writes "ms x y" per car per frame so
    // rendering smoothness can be measured (delta variance = stutter).
    private string _motionLogPath;
    private int _autoRunTest = -1;   // --run-test <N>: auto-click Run Test
    private StreamWriter _motionLog;
    private bool _testPan;
    private bool _testJog;           // --test-jog: hold '.' and watch the jog, then let go
    private bool _testSlotButtons;   // --test-slot-buttons: click a slot's two corner buttons
    private bool _testWindowOut;     // --test-window-out: park a window illegally, let the guard answer
    // --quit-on-test-done: exit IN-APP (GetTree().Quit) once a polled test
    // run finishes. Used by scripts/run_e2e.sh so the window never has to
    // be killed from outside - any kill signal (SIGTERM -> .NET abort,
    // SIGKILL -> macOS crash-report window) pops an annoying dialog.
    private bool _quitOnTestDone;
    private Vector2? _overrideCenter;
    private float? _overrideZoom;

    private readonly List<MarkingLine> _markings = new();
    // The small white disc in the middle of a real crossing, metres:
    // (centre, radius). The sim decides where they are (RoadNetwork.
    // GetCrossingMarkers); this only paints them.
    private readonly List<(Vector2 C, float R)> _crossingDots = new();
    // The meshes holding zoom-culled paint (centre + halt lines): kept so
    // _Process can switch them off together below MarkingMinZoom.
    private readonly List<MeshInstance2D> _farMarkings = new();

    // Dash patterns from the sim (set in BuildMap; RebuildMarkings needs them).
    private float _cDash = 3f, _cGap = 3f, _lDash = 2f, _lGap = 4f,
                  _pDash = 1f, _pGap = 1f;

    // Markings are drawn at their PHYSICAL widths (a centreline is 0.15 m
    // of paint, not a screen pixel) and at full opacity at every zoom: the
    // renderer's job is the paint, the camera's job is the view. MSAA 4x
    // (project settings) anti-aliases the sub-pixel edges instead of hiding
    // them behind a fade.
    private MinimapNode _minimap;
    private Window _mmWin;         // the minimap's embedded window
    private int _mmDbg;            // the first camera moves the inset asked for, logged
    private int _clampDbg;         // the first times a hand hit the pan limit, logged
    // How far off the network the view may be parked (binding user decision
    // 2026-10-22: "limit panning to the area where tracks exist, plus maybe
    // 100 m left right up down") - metres.
    private const float PanBeyondMapM = 100f;
    // Every window this client owns keeps these (binding user decision
    // 2026-10-22: "windows cannot be dragged out of the main window, not even
    // partially; always keep a margin of 5 pixels from each other"). The
    // chrome numbers are Godot's own, measured on a 1600 px run: the map
    // window's client area sat at node position (1240,64) while its title
    // bar painted from y=32 and its frame reached x=1232 and 5 px below the
    // client's bottom - so a window is only inside once its CHROME is.
    private const int WinGapPx = 5;          // the belt every window keeps
    private const int WinTitleBarPx = 32;    // chrome above the client area
    private const int WinFrameSidePx = 8;    // chrome left and right of it
    private const int WinFrameBottomPx = 5;  // chrome below it
    private readonly List<Window> _windows = new();   // every window, guarded
    private CanvasLayer _mmHud;   // main-window readouts stacked UNDER the map window
    private Vector2 _mmPinVp;      // window size the map window was pinned for
    private Vector2 _lastCamPos = new(float.NaN, float.NaN);
    private float _lastCamZoom = float.NaN;
    // Minimap road network: raw segments in sim coords (x east, y north)
    // with physical width — the minimap draws each as a line with a 1 px
    // floor so every road stays visible at any map scale.
    private readonly List<(Vector2 A, Vector2 B, float W)> _mmSegs = new();

    // Paved-edge outline: kept so the kerb paint can be hidden as a whole
    // below a legibility zoom instead of turning into sub-pixel noise.
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
    /// <summary>Crash camera: the first car that reports a contact this run
    /// pins the view on it. A 50-car crossing run is unreadable at the map's
    /// own scale, and the crash is rarely at the crossing centre the --center
    /// arg aims at (measured: contacts at 1102/1862 m, 1388/1855 m - two
    /// figure-8 arms away from the junction). The sim freezes on the crash,
    /// so the view stays on the impact for inspection.</summary>
    private string _crashShotPath;   // --crash-shot <path>: one PNG at the crash (diagnostic)
    private long _crashFocusUid = 0L;
    private bool _crashZoomed = false;

    private const float CrashZoom = 34.0f;   // px/m: ~47 x 26 m over the impact
    private Dictionary<long, int> _crashCounts = new();
    /// <summary>--focus <uid>: watch THIS car (the client binds to it and
    /// zooms in when it appears). The e2e runner passes the uid of the last
    /// crashed car, so a failed crossing run can be re-watched on the impact
    /// instead of the map-wide default view.</summary>
    private long _focusUid = 0L;
    // A pan the operator applied INSIDE the review: the pin is the default
    // view, the pan is what they add to it, and both survive a scrub because
    // the pin is recomputed from the tape every substep.
    private Vector2 _reviewPan;
    /// <summary>The spot the hand pinned the view on through the minimap's
    /// inset (Godot world, metres). The review camera re-derives its pin from
    /// the tape EVERY substep, so writing such a hand move straight to the
    /// camera loses it on the next frame (measured: MINIMAP-PIN put the view
    /// at (1399.7,-837.8) and the following substep pulled it back to the
    /// centroid (775.9,-101.5)). The ask is kept as an absolute map point
    /// instead, and the pin defers to it - the hand named a place on the map,
    /// not an offset against a car that drives on.</summary>
    private Vector2? _mmAsk;
    private Vector2 _dragStartPan;
    // The COURTESY flash (docs/RIGHT_OF_WAY_NEGOTIATION.md §6, the user's
    // word bitteschön): drawn by the car that CONCEDES a right-of-way
    // resource, aimed at the car whose passage it grants. A cone in the
    // WORLD layer, not a child of the car - the car node rotates with the
    // heading and the flash must point at the OTHER car, not at the nose.
    private readonly Dictionary<long, Polygon2D> _courtesy = new();
    // --focus also names a ROLE, resolved every frame from the live state:
    // "parked" = the car parked in the lane (an obstruction never moves, so
    // pinning on it is what the pass under test is about), "slow" = the
    // slowest car still rolling (the crawler the pass must squeeze past).
    // The staged uid is not knowable before the run, so a number cannot be
    // used for a scenario that stages its cast itself.
    private string _focusRole = "";
    // A NAME is stronger than a predicate: the scenario authored the cast,
    // so "parked_car" IS that car - no relative-speed rule, no latch
    // ambiguity. Resolved from the per-car hud_label the spawn carries.
    private string _focusName = "";
    private bool _focusAnnounced;
    private long _focusAnnouncedUid;
    private int _focusFrames;
    private long? _selectedUid;    // click-selected car (info window under the minimap)
    // Decision log for the selected car (info window): polled from
    // GET /car/{uid}/decisions (sim side: Car.Decisions - one event per
    // CHANGE of braking reason, newest shown first).
    private HttpRequest _decHttp;
    private bool _decBusy;
    private long _decUidFor = -1;    // uid the displayed lines belong to
    private ulong _decLastMs = 0;    // wall time of the last fetch attempt
    private List<string> _decLines = new();
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
    private double _timeWarp = 1.0;      // announced by /state (--warp/--fast)
    private float _renderTarget = float.NegativeInfinity;
    private bool _rtClamped;             // render target waiting for data
    private Label _speedLabel;
    private string _lastSpeedText = "";
    private Label _testLabel;          // "5/21" from POST /label (via /state)
    private Label _flowLabel;          // fleet flow: m/s + absolute metres
    private double _flowRate = -1.0;   // m/s over the whole run (-1 = n/a)
    private double _flowTotal = -1.0;  // metres driven since sim start
    private int _standstillN = 0;      // R31: cars stuck < 1 m in the window
    private Panel _infoPanel;          // click-to-inspect car info window
    private Label _infoSpeed;          // live speed row (updated per frame)
    private TextEdit _infoText;        // selectable/scrollable/copyable log
    private string _lastInfoDecKey = "";    // uid|content: full-reset key

    // Clean-shutdown generation: scripts/run_e2e.sh NEVER signals Godot
    // (a SIGTERM kills .NET's exit path into an abort and pops a macOS
    // crash-report window). It bumps this file instead; when the value
    // changes under us, this window is stale and quits itself.
    const string GodotGenFile = "/tmp/driving_game_godot_gen";
    private string _godotGen = "";
    private ulong _godotGenLastMs;
    private Button _infoCopyBtn;   // "copy": whole log -> clipboard
    private ulong _copyFlashUntil; // "copied!" flash revert timestamp

    static string ReadGodotGen()
    {
        try { return File.ReadAllText(GodotGenFile).Trim(); }
        catch { return ""; }
    }
    private string _lastTestText = null;

    // The view is a CLIENT feature: a Godot Camera2D follows the node of the
    // bound car (engine position smoothing gives the follow lag), and manual
    // pan/zoom takes over until the input stops. The sim has no camera state.
    private ulong _lastManualMs = 0;
    private Vector2 _lastFollowTarget;  // previous follow target (snap rule)

    // Per-car display state from /state (multi-car, docs/MULTI_CAR_PLAN.md):
    // color name, blinker/hazard lights, the car's OWN test flags + label.
    private class CarMeta
    {
        public string Color = "blue";
        public string Driver = "BICYCLE";   // driver class (BICYCLE/FREE), from /state
        public bool BlinkL, BlinkR, Hazard, Braking, HeadlightsOn, TaillightsOn;
        public long CourtesyFor = 0L;
        public string CourtesyKind = "";
        public (float X, float Y, float Hdg)? FlagGreen, FlagRed;
        public string HudLabel;
        // Collision footprint (the box used for crash detection), in metres,
        // derived from /state body_corners. 0 = not known yet.
        public float CollLenM, CollWidM;
        public bool InContact;   // crashed (pinned against another car)
    }
    private readonly Dictionary<long, CarMeta> _carMeta = new();
    // Small per-car HUD label in world space above the car's nose.
    private readonly Dictionary<long, Label> _carLabels = new();
    // Corner light nodes per car (Variant can't hold object arrays, so no
    // SetMeta - plain dictionaries).
    private readonly Dictionary<long, Polygon2D[]> _blinkL = new();
    private readonly Dictionary<long, Polygon2D[]> _blinkR = new();
    // Rear-corner tail/brake lights: dark red while TaillightsOn, bright
    // red while IsBraking (regardless of TaillightsOn - a real brake light
    // works even with the lights off), invisible otherwise. Colour is set
    // dynamically per frame, unlike the fixed-colour blinkers.
    private readonly Dictionary<long, Polygon2D[]> _brakeLights = new();
    // Front-corner headlights: white, visible while HeadlightsOn.
    private readonly Dictionary<long, Polygon2D[]> _headlights = new();

    // Car sprites: seven vehicles extracted from assets/source/vehicles_green.png
    // by tools/make_car_sprites.py (nose = texture top, transparent bg).
    // The "color" field names a vehicle model, not just a tint.
    static readonly Dictionary<string, Texture2D> _carTextures = new();
    static Texture2D CarTexture(string color)
    {
        if (_carTextures.Count == 0)
        {
            _carTextures["blue"]    = GD.Load<Texture2D>("res://assets/car_blue.png");
            _carTextures["silver"]  = GD.Load<Texture2D>("res://assets/car_silver.png");
            _carTextures["police"]  = GD.Load<Texture2D>("res://assets/car_police.png");
            _carTextures["tan"]     = GD.Load<Texture2D>("res://assets/car_tan.png");
            _carTextures["tractor"] = GD.Load<Texture2D>("res://assets/car_tractor.png");
            _carTextures["pickup"]  = GD.Load<Texture2D>("res://assets/car_pickup.png");
            _carTextures["mixer"]   = GD.Load<Texture2D>("res://assets/car_mixer.png");
        }
        return color != null && _carTextures.TryGetValue(color, out var t)
            ? t : _carTextures["blue"];
    }

    // Sign artwork: the SVGs provided in assets/signs/ (Godot imports them
    // natively). "yield" = Vorfahrt gewähren, "priority" = Vorfahrtstraße.
    static readonly Dictionary<string, Texture2D> _signTextures = new();
    static Texture2D SignTexture(string type)
    {
        if (_signTextures.Count == 0)
        {
            _signTextures["yield"]    = GD.Load<Texture2D>("res://assets/signs/vorfahrt_gewaehren.svg");
            _signTextures["priority"] = GD.Load<Texture2D>("res://assets/signs/vorfahrtsstrasse.svg");
        }
        return type != null && _signTextures.TryGetValue(type, out var t) && t != null
            ? t : _signTextures["yield"];
    }

    // Physical size (W x L in metres) per sprite. The photo proportions are
    // a STARTING point only (tools/make_car_sprites.py prints them): the two
    // trucks were drawn too short in the source photo and are set to real
    // dimensions instead (Zugmaschine ~7.0 m, Betonmischer ~8.4 m,
    // standard truck width 2.5-2.55 m). The renderer stretches each texture
    // to these sizes; the sim still uses the sedan box for all vehicles -
    // see "Raceline rework — per-vehicle dimensions" in
    // docs/C_SHARP_PORT_SPEC.md (TODO, not implemented).
    // Per-sprite physical footprint (width × length, metres) - the shared
    // table in Config.VEHICLE_SIZES is the single source of truth (the
    // simulation's collision math uses the same numbers).
    static (float W, float L) CarSizeOf(string color)
    {
        var (w, l) = DrivingGame.Sim.Config.SizeForColor(color);
        return ((float)w, (float)l);
    }

    // Minimap dot colors: hand-picked so the eight vehicles stay
    // distinguishable at minimap scale (median body samples were too close).
    static readonly Dictionary<string, Color> CarColors = new()
    {
        ["blue"]    = new(0.25f, 0.41f, 0.86f),
        ["silver"]  = new(0.80f, 0.82f, 0.85f),
        ["police"]  = new(0.55f, 0.68f, 0.95f),
        ["tan"]     = new(0.72f, 0.63f, 0.48f),
        ["tractor"] = new(0.48f, 0.50f, 0.36f),
        ["pickup"]  = new(0.42f, 0.44f, 0.47f),
        ["mixer"]   = new(0.85f, 0.83f, 0.78f),
    };

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
        _godotGen = ReadGodotGen();   // baseline for the clean-quit check
        // User args after "--": parsed FIRST - the source selection decides
        // how map + state are fetched. --screenshot <path> saves the viewport
        // a moment after the map is built (parity check vs pygame) and then
        // QUITS — screenshot runs are self-terminating, so nothing has to be
        // killed from outside (a SIGTERM kills .NET's exit path into an
        // abort; see 2026-08-31 crash reports). --center <x> <y> and
        // --zoom <z> override the initial fit view.
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--screenshot" && i + 1 < args.Length)
                _screenshotPath = args[i + 1];
            else if (args[i] == "--delay" && i + 1 < args.Length)
                _shotDelay = float.Parse(args[i + 1]);
            else if (args[i] == "--center" && i + 2 < args.Length)
            {
                _overrideCenter = W(float.Parse(args[i + 1]), float.Parse(args[i + 2]));
                // A fixed camera target: never auto-follow a spawned car, so
                // the view stays on this point (e.g. the crossing) instead of
                // snapping to the last car. Manual pan/zoom still works.
                _autoFollow = false;
                i += 2;
            }
            else if (args[i] == "--crash-shot" && i + 1 < args.Length)
            {
                _crashShotPath = args[i + 1];
            }
            else if (args[i] == "--focus" && i + 1 < args.Length)
            {
                var fa = args[i + 1];
                if (fa == "parked" || fa == "slow") _focusRole = fa;
                else if (long.TryParse(fa, out var uidv)) _focusUid = uidv;
                else _focusName = fa;   // a NAME the scenario gave the car
                // The focus car also decides the scale: the crash, close up.
                _overrideZoom = CrashZoom;
            }
            else if (args[i] == "--zoom" && i + 1 < args.Length)
                _overrideZoom = float.Parse(args[i + 1]);
            else if (args[i] == "--test-pan")
                _testPan = true;
            else if (args[i] == "--test-seek" && i + 1 < args.Length)
            {
                // Dev hook (same family as --test-pan): push a real mouse
                // click into the scrub bar's seek band at a fraction of the
                // tape, so "can a click move the cursor?" is a question a
                // scripted run can answer instead of a hand on the mouse.
                _testSeek = float.Parse(args[i + 1]);
                i += 1;
            }
            else if (args[i] == "--test-scrub-drag" && i + 1 < args.Length)
            {
                // Dev hook (same family as --test-seek): push a real DRAG into
                // the scrub bar's seek band - press, move, release - so the
                // question "does a bar drag also pan the map?" is answered by
                // the input path itself, not by reading the handler.
                _testScrubDrag = float.Parse(args[i + 1]);
                i += 1;
            }
            else if (args[i] == "--test-bar-point" && i + 2 < args.Length)
            {
                // Dev hook (same family as --test-seek): pin the pointer for
                // the scrub bar's idle decision at a fixed main-window point,
                // so "does the bar fade away when the hand is elsewhere" is a
                // scripted question and not one that depends on where the OS
                // pointer happens to rest while the run is recorded.
                _testBarPoint = new Vector2(float.Parse(args[i + 1]), float.Parse(args[i + 2]));
                i += 2;
            }
            else if (args[i] == "--test-slot-buttons")
                _testSlotButtons = true;
            else if (args[i] == "--test-jog")
                _testJog = true;
            else if (args[i] == "--test-window-out")
                _testWindowOut = true;
            else if (args[i] == "--test-minimap-pan" && i + 2 < args.Length)
            {
                // Dev hook (same family as --test-seek and --test-scrub-drag):
                // push a REAL press -> move -> release into the minimap's
                // inset at fractions of its rect, then a wheel over it, so
                // "does the inset move and size the view box?" is answered by
                // the input path itself and not by a hand on the mouse.
                _testMmPan = new Vector2(float.Parse(args[i + 1]), float.Parse(args[i + 2]));
                i += 2;
            }
            else if (args[i] == "--resize" && i + 2 < args.Length)
            {
                // Change the window size a moment after start. A HUD that
                // measures itself against the viewport can only be trusted if
                // it survives a resize - and a resize is otherwise something
                // only a hand on the window border can do, i.e. something no
                // scripted screenshot run can test.
                _resizeTo = new Vector2I(int.Parse(args[i + 1]), int.Parse(args[i + 2]));
                i += 2;
            }
            else if (args[i] == "--quit-on-test-done")
                _quitOnTestDone = true;
            else if (args[i] == "--motionlog" && i + 1 < args.Length)
                _motionLogPath = args[i + 1];
            else if (args[i] == "--run-test" && i + 1 < args.Length)
                _autoRunTest = int.Parse(args[i + 1]);
            else if (args[i] == "--source" && i + 1 < args.Length)
                _source = args[i + 1];
            else if (args[i] == "--map" && i + 1 < args.Length)
                _embeddedMapName = args[i + 1];
            else if (args[i] == "--port" && i + 1 < args.Length)
                _embeddedPort = int.Parse(args[i + 1]);
            else if (args[i] == "--review-name" && i + 1 < args.Length)
            {
                _reviewName = args[i + 1];
                i += 1;
            }
            else if (args[i] == "--review-at" && i + 1 < args.Length)
            {
                _reviewAt = int.Parse(args[i + 1]);
                i += 1;
            }
            else if (args[i] == "--review")
            {
                // The Simulationsreviewer (docs/SIMULATIONS_REVIEWER.md): a
                // SECOND WINDOW of this client that draws a TAPE frame instead
                // of the live sim. Optional path - without one the newest tape
                // in tests/ is used.
                _review = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    _reviewPath = args[i + 1];
                    i += 1;
                }
                if (args[i] == "--review-at" && i + 1 < args.Length)
                {
                    // Start the cursor somewhere inside the run - a review
                    // screenshot of a known moment needs it.
                    _reviewAt = int.Parse(args[i + 1]);
                    i += 1;
                }
            }
        }

        // Phase 7: pick the communication method. Embedded mode creates the
        // SimHost FIRST (its _Ready runs synchronously on AddChild: network,
        // sim thread and embedded REST API are up before we fetch anything).
        bool embedded = string.Equals(_source, "embedded",
                                      StringComparison.OrdinalIgnoreCase);
        if (embedded)
        {
            _simHost = new SimHost(_embeddedMapName, _embeddedPort);
            AddChild(_simHost);
            GD.Print($"Source: EMBEDDED (in-process sim, REST API on :{_embeddedPort})");
        }
        else
            GD.Print("Source: HTTP (external server on :5001)");

        // Review mode: load the tape BEFORE anything is fetched, and build its
        // HUD (the two-line readout + the scrub bar with the fluidity curve).
        if (_review)
        {
            string tape = _reviewPath;
            if (tape.Length == 0)
                tape = NewestTape();
            if (tape.Length > 0 && LoadTape(tape))
            {
                // Index the tape ONCE - every per-frame scan of the whole
                // run is what put the review window at ~2 fps.
                BuildTapeIndex();
                GD.Print($"REVIEW of {TapeNameOf(tape)}: {_tape.Count} substeps, " +
                         $"dt {_tapeDt:F6} s, map {_tapeMap} #{_tapeHash}");
            }
            else
            {
                _review = false;
                GD.Print("!! review: no tape readable - falling back to the live view");
            }
            BuildReviewHud();
            BuildReviewSlots();
            BuildRunPicker();
            // A review window draws the TAPE, so a live sim beside it is
            // irrelevant to the picture - and the live surface (speed label,
            // flow, test runner) is hidden per frame. ⏹ stop is what freezes
            // the host, and only when the user presses it (docs/
            // SIMULATIONS_REVIEWER.md).
        }

        // The window title carries the REST port: two windows of this
        // project differ ONLY by the server they talk to - the e2e host and
        // a scratch run look identical otherwise, which made "which map is
        // this?" unanswerable from the screen.
        var port = embedded ? _embeddedPort : PortOfUrl(MapUrl);
        if (port > 0)
        {
            var title = GetTree().Root.Title;
            if (title.Length == 0) title = "DrivingGame";
            GetTree().Root.Title = $"{title} :{port}";
        }

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
        _world.AddChild(_markLayer);
        _http.RequestCompleted += OnMapResponse;
        if (embedded)
        {
            // Map in-process: the SAME payload the /map endpoint serves
            // (shared MapPayload builder), no HTTP round-trip.
            var payload = DrivingGame.Api.MapPayload.Build(_simHost.Engine.Network);
            using var doc = JsonDocument.Parse(
                JsonSerializer.SerializeToUtf8Bytes(payload));
            BuildMap(doc.RootElement);
        }
        else
            FetchMap();

        // /state polling (pipelined, see below):
        // UseThreads: without it, Godot's HTTPRequest does ONE socket read
        // per rendered frame (godotengine/godot#120425) - every request then
        // costs several frames (~85-210 ms measured on this machine for a
        // localhost reply that takes 0.4 ms from Python). Threaded mode
        // drains the socket at OS speed; callbacks still fire on main.
        //
        if (!embedded)
        {
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
        }

        // Test-runner UI + its own HTTP node (the map/state nodes are busy
        // with their pipelines).
        // The decision fetch belongs to the CLIENT, not to the REST-source
        // branch: embedded mode has a REST API too (in-process, on
        // --port), and clicking a car used to dereference a null _decHttp
        // there. One node, the port the client actually talks to.
        _decHttp = new HttpRequest { UseThreads = true, Timeout = 1.0 };
        AddChild(_decHttp);
        _decHttp.RequestCompleted += (id, code, hdr, body) =>
            OnDecisionsResponse(code, body);

        _cmdHttp = new HttpRequest { UseThreads = true, Timeout = 5.0 };
        AddChild(_cmdHttp);
        _cmdHttp.RequestCompleted += (id, code, hdr, body) =>
            OnCmdResponse(code, body);
        if (!_review) BuildRunTestUi();   // the reviewer has its own surface

        // --run-test <N>: fill the field and fire the same code path as a
        // button click, a moment after the UI is built.
        if (_autoRunTest > 0)
            GetTree().CreateTimer(1.0).Timeout += () =>
            {
                _testInput.Text = _autoRunTest.ToString();
                RunTestFromUi();
            };

        // --quit-on-test-done: watch /run_test from the start (the run may
        // be launched by scripts/run_e2e.sh, not by this window) and quit
        // in-app when it finishes.
        if (_quitOnTestDone)
            StartPolling();

        if (_motionLogPath != null)
            _motionLog = new StreamWriter(_motionLogPath, append: false);

        // Frame pacing is the display's: Godot's default adaptive vsync caps
        // the loop, and everything here is delta-driven, so no manual fps cap.

        if (_testPan && _screenshotPath != null)
        {
            // Dev hook: hold Key.D for 1.5 s after load, then shoot —
            // verifies the key -> camera pan path end to end.
            GetTree().CreateTimer(2.0).Timeout += () => _keys.Add(Key.D);
            GetTree().CreateTimer(3.5).Timeout += () => _keys.Remove(Key.D);
            GetTree().CreateTimer(4.0).Timeout += () => SaveShot(_screenshotPath);
        }

        if (_testJog)
        {
            // Dev hook (same family as --test-seek): push a REAL held '.' -
            // press, hold, release - through the viewport and read the cursor
            // over time, so "a held '.' or ',' moves slow forward/backward and
            // stops when the finger comes up" is measured rather than assumed.
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                GD.Print($"TEST-JOG start cursor={_cursor} dt={_tapeDt:F6} s -> {JogStepsPerSecond:F0} substeps/s is {JogStepsPerSecond * _tapeDt:F2} s of tape per wall second");
                // The HELD state, not a pushed key: this build's bindings
                // deliver mouse events through PushInput but a pushed key
                // never reaches _Input (measured: the tap step did not move
                // the cursor), so the hook sets exactly what a held '.' would
                // have set - and the release below is what a finger coming
                // up would set.
                _jogDir = 1.0;      // what holding '.' sets: forward
                GetTree().CreateTimer(0.6).Timeout += () => GD.Print($"TEST-JOG +0.6s cursor={_cursor} dir={_jogDir} acc={_jogAcc:F2} playing={_playing} tape={_tape.Count} dt={_tapeDt:F6}");
                GetTree().CreateTimer(1.2).Timeout += () => GD.Print($"TEST-JOG +1.2s cursor={_cursor}");
                GetTree().CreateTimer(1.8).Timeout += () => GD.Print($"TEST-JOG +1.8s cursor={_cursor}");
                GetTree().CreateTimer(2.4).Timeout += () =>
                {
                    _jogDir = 0.0;              // the finger came up
                    GD.Print($"TEST-JOG release set at cursor={_cursor}");
                };
                GetTree().CreateTimer(3.2).Timeout += () => GD.Print($"TEST-JOG +1.2s after release cursor={_cursor}");
                GetTree().CreateTimer(4.0).Timeout += () =>
                {
                    GD.Print($"TEST-JOG +2.0s after release cursor={_cursor}");
                    if (_screenshotPath != null) SaveShot(_screenshotPath);
                };
            };
        }

        if (_testSlotButtons)
        {
            // Dev hook (same family as --test-seek / --test-window-out): drive
            // the two corner buttons of slot 1 through the real input path -
            // pick a car, toggle follow, centre, toggle back - and read the
            // camera and the toggles back after each, so the decision is
            // measured, not assumed.
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                var vpS = GetTree().Root.GetVisibleRect().Size;
                // Two picks, a hand's width apart, so two slot windows own a
                // car and the "one car followed" rule has something to enforce.
                foreach (var at in new[] { vpS / 2f, vpS / 2f + new Vector2(120f, 0f) })
                {
                    GetViewport().PushInput(new InputEventMouseButton
                    {
                        Position = at, GlobalPosition = at,
                        ButtonIndex = MouseButton.Left, Pressed = true,
                    });
                    GetViewport().PushInput(new InputEventMouseButton
                    {
                        Position = at, GlobalPosition = at,
                        ButtonIndex = MouseButton.Left, Pressed = false,
                    });
                }
                GetTree().CreateTimer(0.6).Timeout += () =>
                {
                    if (_slots.Count == 0) return;
                    var s = _slots[0];
                    GD.Print($"SLOT-TEST after pick: uid={s.UidShown} visible={s.Win.Visible} " +
                             $"cam=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2})");
                    GetTree().CreateTimer(0.4).Timeout += () =>
                    {
                        // Click slot 1's FOLLOW button, in the window's lower-right
                        // corner: the window's client origin plus the control's
                        // own rect, which is window-local.
                        var fr = s.FollowBtn.GetGlobalRect();
                        var c = new Vector2(s.Win.Position.X + fr.Position.X + fr.Size.X / 2f,
                                            s.Win.Position.Y + fr.Position.Y + fr.Size.Y / 2f);
                        GetViewport().PushInput(new InputEventMouseButton
                        {
                            Position = c, GlobalPosition = c,
                            ButtonIndex = MouseButton.Left, Pressed = true,
                        });
                        GetViewport().PushInput(new InputEventMouseButton
                        {
                            Position = c, GlobalPosition = c,
                            ButtonIndex = MouseButton.Left, Pressed = false,
                        });
                        GetTree().CreateTimer(0.5).Timeout += () =>
                        {
                            GD.Print($"SLOT-TEST follow click: slot0 following={s.Following} " +
                                     $"followed uid={SlotFollowUid()} button='{s.FollowBtn.Text}' " +
                                     $"cam=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2})");
                            // Then the CENTRE button - momentary.
                            var cr = s.CenterBtn.GetGlobalRect();
                            var c2 = new Vector2(s.Win.Position.X + cr.Position.X + cr.Size.X / 2f,
                                                 s.Win.Position.Y + cr.Position.Y + cr.Size.Y / 2f);
                            GetViewport().PushInput(new InputEventMouseButton
                            {
                                Position = c2, GlobalPosition = c2,
                                ButtonIndex = MouseButton.Left, Pressed = true,
                            });
                            GetViewport().PushInput(new InputEventMouseButton
                            {
                                Position = c2, GlobalPosition = c2,
                                ButtonIndex = MouseButton.Left, Pressed = false,
                            });
                            GetTree().CreateTimer(0.5).Timeout += () =>
                            {
                                GD.Print($"SLOT-TEST centre click: cam=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2}) " +
                                         $"mmAsk={_mmAsk != null} following={s.Following}");
                                // And follow again - the toggle must switch OFF.
                                GetViewport().PushInput(new InputEventMouseButton
                                {
                                    Position = c, GlobalPosition = c,
                                    ButtonIndex = MouseButton.Left, Pressed = true,
                                });
                                GetViewport().PushInput(new InputEventMouseButton
                                {
                                    Position = c, GlobalPosition = c,
                                    ButtonIndex = MouseButton.Left, Pressed = false,
                                });
                                GetTree().CreateTimer(0.5).Timeout += () =>
                                {
                                    GD.Print($"SLOT-TEST follow twice: slot0 following={s.Following} " +
                                             $"followed uid={SlotFollowUid()} button='{s.FollowBtn.Text}'");
                                    // Now the SECOND slot's follow - it must take
                                    // the camera and switch the first back off.
                                    if (_slots.Count < 2 || _slots[1].UidShown <= 0L)
                                    {
                                        if (_screenshotPath != null) SaveShot(_screenshotPath);
                                        return;
                                    }
                                    var s2 = _slots[1];
                                    var fr2 = s2.FollowBtn.GetGlobalRect();
                                    var c3 = new Vector2(s2.Win.Position.X + fr2.Position.X + fr2.Size.X / 2f,
                                                         s2.Win.Position.Y + fr2.Position.Y + fr2.Size.Y / 2f);
                                    GetViewport().PushInput(new InputEventMouseButton
                                    {
                                        Position = c3, GlobalPosition = c3,
                                        ButtonIndex = MouseButton.Left, Pressed = true,
                                    });
                                    GetViewport().PushInput(new InputEventMouseButton
                                    {
                                        Position = c3, GlobalPosition = c3,
                                        ButtonIndex = MouseButton.Left, Pressed = false,
                                    });
                                    GetTree().CreateTimer(0.5).Timeout += () =>
                                    {
                                        GD.Print($"SLOT-TEST second follow: slot1 following={_slots[1].Following} " +
                                                 $"slot0 following={s.Following} followed uid={SlotFollowUid()}");
                                        // Turn the FIRST one on again while the
                                        // second holds the camera - the rule
                                        // "switching one on switches the others
                                        // off" has to be measured, not assumed.
                                        GetViewport().PushInput(new InputEventMouseButton
                                        {
                                            Position = c, GlobalPosition = c,
                                            ButtonIndex = MouseButton.Left, Pressed = true,
                                        });
                                        GetViewport().PushInput(new InputEventMouseButton
                                        {
                                            Position = c, GlobalPosition = c,
                                            ButtonIndex = MouseButton.Left, Pressed = false,
                                        });
                                        GetTree().CreateTimer(0.5).Timeout += () =>
                                        {
                                            GD.Print($"SLOT-TEST third follow: slot0 following={s.Following} " +
                                                     $"slot1 following={_slots[1].Following} followed uid={SlotFollowUid()}");
                                            if (_screenshotPath != null) SaveShot(_screenshotPath);
                                        };
                                    };
                                };
                            };
                        };
                    };
                };
            };
        }

        if (_testWindowOut)
        {
            // Dev hook (same family as --test-pan): put windows somewhere a
            // hand is not allowed to leave them - partly out of the main
            // window, and on top of each other - and read back where the
            // guard settled them, so "windows cannot be dragged out, and they
            // keep 5 px from each other" is a measured answer rather than a
            // claim.
            // After --resize when one is asked for: the point of the test is
            // the size the windows have to share, so the resize comes first.
            var whenWin = _resizeTo.X > 0 ? Mathf.Max(2.0, _shotDelay - 0.8) : 1.5;
            GetTree().CreateTimer(whenWin).Timeout += () =>
            {
                var asked = "";
                foreach (var w in _windows) asked += $" {w.Title}@({w.Position.X},{w.Position.Y})";
                GD.Print($"TEST-WINDOW ask:{asked}");
                for (var i = 0; i < _windows.Count; i++)
                    // All outside the same corner, all overlapping each other.
                    _windows[i].Position = new Vector2I(-200 + 30 * i, -200 + 20 * i);
                GetTree().CreateTimer(0.3).Timeout += () =>
                {
                    var got = "";
                    foreach (var w in _windows) got += $" {w.Title}@({w.Position.X},{w.Position.Y}) {w.Size.X}x{w.Size.Y}";
                    GD.Print($"TEST-WINDOW enforced:{got}");
                    // The gap that is actually left between the outer rects of
                    // neighbours, in px - the number the decision asks for is
                    // 5.
                    for (var i = 0; i < _windows.Count; i++)
                    {
                        for (var j = i + 1; j < _windows.Count; j++)
                        {
                            var a = OuterRect(_windows[i]);
                            var b = OuterRect(_windows[j]);
                            var gx = Mathf.Max(b.Position.X - a.End.X, a.Position.X - b.End.X);
                            var gy = Mathf.Max(b.Position.Y - a.End.Y, a.Position.Y - b.End.Y);
                            GD.Print($"TEST-WINDOW gap {_windows[i].Title} vs {_windows[j].Title} = " +
                                     $"{Mathf.Max(gx, gy):F0} px clear (want {WinGapPx})");
                        }
                    }
                };
            };
        }

        if (_testMmPan.X != 0f || _testMmPan.Y != 0f)
        {
            GetTree().CreateTimer(1.5).Timeout += () =>
            {
                if (_minimap == null || _mmWin == null) return;
                var r = new Rect2(_mmWin.Position, _minimap.Size);
                float x = r.Position.X + r.Size.X * _testMmPan.X;
                float y = r.Position.Y + r.Size.Y * _testMmPan.Y;
                var vpT = GetViewport().GetVisibleRect().Size;
                GD.Print($"TEST-MM-PAN inset={r} press=({x:F0},{y:F0}) " +
                         $"cam before=({_cam.GlobalPosition.X:F1},{_cam.GlobalPosition.Y:F1})");
                GetViewport().PushInput(new InputEventMouseButton
                {
                    Position = new Vector2(x, y), GlobalPosition = new Vector2(x, y),
                    ButtonIndex = MouseButton.Left, Pressed = true,
                });
                GetTree().CreateTimer(0.2).Timeout += () =>
                {
                    // The hand walks inside the inset - the box has to follow
                    // with the CURSOR centred in it.
                    GetViewport().PushInput(new InputEventMouseMotion
                    {
                        Position = new Vector2(x + 60f, y + 20f),
                        GlobalPosition = new Vector2(x + 60f, y + 20f),
                        Relative = new Vector2(60f, 20f),
                    });
                    GetTree().CreateTimer(0.2).Timeout += () =>
                    {
                        GetViewport().PushInput(new InputEventMouseButton
                        {
                            Position = new Vector2(x + 60f, y + 20f),
                            GlobalPosition = new Vector2(x + 60f, y + 20f),
                            ButtonIndex = MouseButton.Left, Pressed = false,
                        });
                        GetTree().CreateTimer(0.2).Timeout += () =>
                        {
                            float z = _cam.Zoom.X;
                            GD.Print($"TEST-MM-PAN after drag cam=({_cam.GlobalPosition.X:F1},{_cam.GlobalPosition.Y:F1}) " +
                                     $"hand moved (+60,+20) mm px -> " +
                                     $"world {60f / (r.Size.X / _bounds.Size.X):F1} m expected · zoom {z:F3} px/m " +
                                     $"view {vpT.X / z:F0} m box {(vpT.X / z) * (r.Size.X / _bounds.Size.X):F0} px");
                            for (int notch = 0; notch < 3; notch++)
                                GetViewport().PushInput(new InputEventMouseButton
                                {
                                    Position = new Vector2(x + 60f, y + 20f),
                                    GlobalPosition = new Vector2(x + 60f, y + 20f),
                                    ButtonIndex = MouseButton.WheelUp, Pressed = true,
                                });
                            GetTree().CreateTimer(0.2).Timeout += () =>
                            {
                                // And zoom OUT: the box must stop at the
                                // inset's own size = the map's extent.
                                for (int notch = 0; notch < 30; notch++)
                                    GetViewport().PushInput(new InputEventMouseButton
                                    {
                                        Position = new Vector2(x + 60f, y + 20f),
                                        GlobalPosition = new Vector2(x + 60f, y + 20f),
                                        ButtonIndex = MouseButton.WheelDown, Pressed = true,
                                    });
                                GetTree().CreateTimer(0.25).Timeout += () =>
                                {
                                    var zOut = _cam.Zoom.X;
                                    var vpO = GetViewport().GetVisibleRect().Size;
                                    GD.Print($"TEST-MM-WHEEL out 30 notches -> zoom {zOut:F3} px/m " +
                                             $"view {vpO.X / zOut:F0} m · box {(vpO.X / zOut) * (_minimap.Size.X / _bounds.Size.X):F0} px " +
                                             $"of the inset's {_minimap.Size.X:F0} px · cap asked " +
                                             $"{Mathf.Max(ZoomMin, MinZoomOverMap()):F3} px/m = {_bounds.Size.X:F0} m wide");
                                    float cx = r.Position.X + r.Size.X * 0.99f;
                                    float cy = r.Position.Y + r.Size.Y * 0.01f;
                                    GetViewport().PushInput(new InputEventMouseButton
                                    {
                                        Position = new Vector2(cx, cy),
                                        GlobalPosition = new Vector2(cx, cy),
                                        ButtonIndex = MouseButton.Left, Pressed = true,
                                    });
                                    GetTree().CreateTimer(0.2).Timeout += () =>
                                        GD.Print($"TEST-MM-CLAMP corner press -> cam=({_cam.GlobalPosition.X:F1},{_cam.GlobalPosition.Y:F1}) " +
                                                 $"allowed x<= {_bounds.End.X + PanBeyondMapM:F0} " +
                                                 $"godot y>= {-( _bounds.End.Y + PanBeyondMapM):F0}");
                                };
                            };
                        };
                    };
                };
            };
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
        // Free the static geometry, but keep the marking layer alive (its
        // meshes are rebuilt by RebuildMarkings, not re-created here).
        foreach (var child in _world.GetChildren())
            if (child != _markLayer)
                child.QueueFree();
        _markings.Clear();
        _crossingDots.Clear();
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

        // --- Road paint (z 0): one filled ribbon PER ROAD. Nothing is
        // punched out behind the paint, so an island (a roundabout, a
        // figure-8 lobe) is simply the area no ribbon covers and a road that
        // crosses itself paints its crossing twice in the same colour — what
        // a crossing actually looks like. (The old single unioned polygon
        // needed a background punch for its holes; as a global layer that
        // painted grass over anything else at z 0 inside a ring, which is
        // what hid the junction fillets at the plain figure-8 crossing.)
        if (root.TryGetProperty("road_strips", out var stripsEl))
            AddRoadStrips(stripsEl, roadColor);
        if (root.TryGetProperty("road_polygons", out var fillsEl))
            foreach (var r in fillsEl.EnumerateArray())
                AddPolygon(Pts(r.GetProperty("exterior")), roadColor, 0);

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
        // Halt-/wart line width (Ze. 294): the sim's, 0.5 m of paint.
        float stopW = 0.5f;
        if (root.TryGetProperty("marking_style", out var ms2))
            stopW = ms2.TryGetProperty("stop_line_width_m", out var sw)
                ? sw.GetSingle() : 0.5f;

        var white = new Color(1f, 1f, 1f);
        var cream = new Color(251 / 255f, 251 / 255f, 245 / 255f);
        var rail = new Color(183 / 255f, 189 / 255f, 186 / 255f);

        // Centerlines: white paint, 0.15 m wide. The network decides the
        // style per piece - solid in front of a junction mouth, dashed
        // beyond it - so the renderer just paints what it is told.
        if (root.TryGetProperty("centerlines", out var cl))
            foreach (var line in cl.EnumerateArray())
            {
                var pts = Pts(line.GetProperty("pts"));
                if (line.GetProperty("style").GetString() == "solid")
                    _markings.Add(new MarkingLine { Pts = pts, Solid = true,
                                                     Color = white, WidthM = 0.15f,
                                                     FarCull = true });
                else
                    _markings.Add(new MarkingLine { Pts = pts, DashM = _cDash, GapM = _cGap,
                                                     Color = white, WidthM = 0.15f,
                                                     FarCull = true });
            }

        // Elevated centrelines: the ground-level ones under a bridge deck
        // are covered by it (z 2 < 20), so the sim exports the deck's own
        // centreline to be drawn ABOVE the deck.
        if (root.TryGetProperty("elevated_centerlines", out var ecl))
            foreach (var line in ecl.EnumerateArray())
                _markings.Add(new MarkingLine { Pts = Pts(line), DashM = _cDash, GapM = _cGap,
                                                 Color = white, WidthM = 0.15f,
                                                 Elevated = true });

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
                                                         Color = white, WidthM = 0.15f });
                        break;
                    case "p_dash":
                        _markings.Add(new MarkingLine { Pts = pts, DashM = _pDash, GapM = _pGap,
                                                         Color = white, WidthM = 0.15f });
                        break;
                    case "solid":
                        _markings.Add(new MarkingLine { Pts = pts, Solid = true,
                                                        Color = cream, WidthM = widthM,
                                                        Breitstrich = widthM >= 0.25f });
                        break;
                    case "guardrail":
                        _markings.Add(new MarkingLine { Pts = pts, Solid = true,
                                                        Color = rail, WidthM = 0.15f });
                        break;
                }
            }

        // --- Halt-/wart lines (Ze. 294): the broad white band across the
        // mouth of every yielding approach. The sim places it where the
        // corner roundings touch the approach (RoadNetwork.GetStopLines /
        // GetCrossingMouths) - German practice paints exactly there: at the
        // mouth, never inside the crossing. In Germany the yield position is
        // a solid broad line, not shark's teeth (those are a Danish marking;
        // here the rule is carried by the triangular 205 sign), so the paint
        // is one continuous band at the physical halt-line width.
        if (root.TryGetProperty("stop_lines", out var sl))
            foreach (var l in sl.EnumerateArray())
            {
                var a = new Vector2(l[0].GetSingle(), -l[1].GetSingle());
                var b = new Vector2(l[2].GetSingle(), -l[3].GetSingle());
                _markings.Add(new MarkingLine { Pts = new List<Vector2> { a, b },
                                                 Solid = true, Color = white,
                                                 WidthM = stopW, FarCull = true });
            }

        // --- The crossing's own middle marker: a small white disc at its
        // centre. Not a German rule - the user asked for it, and it keeps a
        // crossing readable where no other paint exists.
        if (root.TryGetProperty("crossing_markers", out var cm))
            foreach (var d in cm.EnumerateArray())
                _crossingDots.Add((W(d[0].GetSingle(), d[1].GetSingle()),
                                   d[2].GetSingle()));

        // --- Arrows + painted P marks (z 2): plain polygons.
        foreach (var key in new[] { "oneway_arrows", "parking_marks" })
            if (root.TryGetProperty(key, out var arr))
                foreach (var poly in arr.EnumerateArray())
                    AddPolygon(Pts(poly), white, 2);

        // --- Kerb outline (z 3, above the road fill): the TRUE paved
        // perimeter, one closed Line2D per ring of the unioned paved area,
        // inset by EDGE_LINE_INSET_M so a tarmac shoulder stays outside the
        // paint. Physical width - at low zoom it thins out naturally.
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
                AddDeckFill(r, sidewalk, 20, bg);
        if (root.TryGetProperty("elevated_roadways", out var erw))
            foreach (var r in erw.EnumerateArray())
                AddDeckFill(r, roadColor, 20, bg);
        if (root.TryGetProperty("elevated_edge_lines", out var eer))
            foreach (var line in eer.EnumerateArray())
            {
                var edge = new Line2D
                {
                    Points = Pts(line).ToArray(),
                    Closed = false,
                    DefaultColor = white,
                    Width = EdgeLineWidthM,
                    ZIndex = 22,
                };
                _world.AddChild(edge);
                _bridgeEdges.Add(edge);
            }

        // --- Road signs (z 3, above markings, below cars): the real sign
        // artwork from assets/signs/ (Vorfahrt gewähren / Vorfahrtstraße).
        // The payload places them OUTSIDE the curb on the approach side;
        // here they are rotated to the approach heading and scaled to a
        // deliberately OVERSIZED width - map readability over realism.
        if (root.TryGetProperty("signs", out var sgn))
            foreach (var s in sgn.EnumerateArray())
            {
                Vector2 C = W(s.GetProperty("x").GetSingle(), s.GetProperty("y").GetSingle());
                string stype = s.GetProperty("type").GetString() ?? "";
                float headingRad = Mathf.DegToRad(s.GetProperty("heading_deg").GetSingle());
                var tex = SignTexture(stype);
                const float SignWidthM = 2.5f;   // bigger than a real sign on purpose
                _world.AddChild(new Sprite2D
                {
                    Texture = tex,
                    Position = C,
                    Rotation = headingRad,       // aligned with the road (car convention)
                    Scale = new Vector2(SignWidthM / tex.GetSize().X,
                                        SignWidthM / tex.GetSize().Y),
                    ZIndex = 3,
                });
            }

        // Camera: fit the whole map into the viewport (or use overrides).
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float zoom = Mathf.Min(vp.X / _bounds.Size.X, vp.Y / _bounds.Size.Y) * 0.95f;
        var center = W(_bounds.Position.X + _bounds.Size.X / 2f,
                       _bounds.Position.Y + _bounds.Size.Y / 2f);
        if (_overrideZoom.HasValue) zoom = _overrideZoom.Value;
        if (_overrideCenter.HasValue) center = _overrideCenter.Value;
        _cam.Zoom = new Vector2(zoom, zoom);
        _cam.GlobalPosition = center;
        RebuildMarkings();

        // Screen-space overlays: minimap (top right) + zoom readout.
        // The inset's shape comes FROM THE MAP (see MmBoxFor), so the drawing
        // fills the window it lives in instead of leaving empty frame around a
        // fixed square.
        var mmBox = MmBoxFor(_bounds);
        if (_minimap == null)
        {
            // The minimap lives in an EMBEDDED window (Godot's Window node,
            // default gui_embed_subwindows = true): it gets a title bar and a
            // close button, so the map inset can be told apart from - and
            // closed off - the rest of the screen. MinimapNode is a Control
            // that draws into its OWN rect, so the window can be sized to the
            // inset and nothing is left over.
            _mmWin = new Window {
                Title = "map",
                Transient = true,
                Unresizable = true,
                // Below the readout's box - at the very top edge the title
                // bar hid behind the review label and the window read as an
                // anonymous frame.
                Position = new Vector2I(
                    (int)(GetViewport().GetVisibleRect().Size.X
                          - mmBox.X - 2 * MinimapNode.Margin), 64),
                // Frame = inset + the caption row under it, nothing more: a
                // window taller than its content shows an empty band.
                Size = MmWindowSize(mmBox),
            };
            AddChild(_mmWin);
            _mmWin.CloseRequested += () => _mmWin.Hide();
            var layer = new CanvasLayer { Layer = 10 };
            _mmWin.AddChild(layer);
            _minimap = new MinimapNode();
            _minimap.Position = new Vector2(MinimapNode.Margin, MinimapNode.Margin);
            layer.AddChild(_minimap);
            // The window is the inset and nothing else (user decision 2026-10-22:
            // "make the minimap content fit in the window"), so the readouts do
            // not live in it - they hang under it, in the MAIN window's frame,
            // where main-window coordinates are the right ones.
            _mmHud = new CanvasLayer { Layer = 10 };
            AddChild(_mmHud);
            RegisterWindow(_mmWin);

            var sb = new StyleBoxFlat
            {
                BgColor = new Color(20 / 255f, 20 / 255f, 20 / 255f, 180f / 255f),
                ContentMarginLeft = 7, ContentMarginRight = 7,
                ContentMarginTop = 6, ContentMarginBottom = 6,
            };
            // Fleet flow (the quality metric of 2026-10-19): the metres the
            // WHOLE fleet drives per second of sim time, plus the absolute
            // total. High = fluid traffic, low = the fleet is stuck - see
            // docs/DRIVING_MANEUVERS.md §8 R28.
            _flowLabel = new Label();
            _flowLabel.AddThemeStyleboxOverride("normal", sb);
            _flowLabel.AddThemeColorOverride("font_color",
                new Color(180 / 255f, 220 / 255f, 180 / 255f));
            _flowLabel.AddThemeFontSizeOverride("font_size", 20);
            _flowLabel.Text = "";
            _flowLabel.Visible = false;   // until the first /state frame
            _mmHud.AddChild(_flowLabel);

            // Speed readout under the minimap: bound car (or first car).
            _speedLabel = new Label();
            _speedLabel.AddThemeStyleboxOverride("normal", sb);
            _speedLabel.AddThemeColorOverride("font_color",
                new Color(220 / 255f, 220 / 255f, 220 / 255f));
            _speedLabel.AddThemeFontSizeOverride("font_size", 20);
            _speedLabel.Visible = false;
            _mmHud.AddChild(_speedLabel);

            // Test number label: yellow text on a dark panel under the
            // minimap, set by the e2e suite via POST /label.
            _testLabel = new Label();
            _testLabel.AddThemeStyleboxOverride("normal", sb);
            _testLabel.AddThemeColorOverride("font_color",
                new Color(1f, 1f, 0f));
            _testLabel.AddThemeFontSizeOverride("font_size", 24);
            _testLabel.Visible = false;
            _mmHud.AddChild(_testLabel);

            // Car info window: click a car in the map to select it. Static
            // position under the minimap, same background as the minimap.
            var sbInfo = new StyleBoxFlat
            {
                BgColor = MinimapNode.Bg,
                BorderColor = MinimapNode.Border,
                BorderWidthLeft = 2, BorderWidthRight = 2,
                BorderWidthTop = 2, BorderWidthBottom = 2,
                ContentMarginLeft = 10, ContentMarginRight = 10,
                ContentMarginTop = 8, ContentMarginBottom = 8,
            };
            _infoPanel = new Panel { Visible = false };
            _infoPanel.AddThemeStyleboxOverride("panel", sbInfo);
            // Live speed row on top - kept OUT of the text view so its
            // per-frame churn never resets the user's scroll position.
            _infoSpeed = new Label { Text = "" };
            _infoSpeed.AddThemeColorOverride("font_color",
                new Color(220 / 255f, 220 / 255f, 220 / 255f));
            _infoSpeed.AddThemeFontSizeOverride("font_size", 16);
            _infoSpeed.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            _infoSpeed.OffsetLeft = 8;  _infoSpeed.OffsetRight = -8;
            _infoSpeed.OffsetTop = 6;   _infoSpeed.OffsetBottom = 30;
            _infoPanel.AddChild(_infoSpeed);
            // Real text view instead of a Label: selectable, scrollable and
            // copyable (Editable=false still allows select + copy), so the
            // full decision log - up to 100 lines - can be read and copied
            // out for analysis, not just the ~10 a fixed label could show.
            _infoText = new TextEdit
            {
                Editable = false,   // read-only: nothing can be typed in ...
                SelectingEnabled = true,   // ...but select + copy still work
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                AutowrapMode = TextServer.AutowrapMode.Word,
            };
            _infoText.AddThemeFontSizeOverride("font_size", 16);
            _infoText.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _infoText.OffsetLeft = 8;   _infoText.OffsetTop = 34;
            _infoText.OffsetRight = -8; _infoText.OffsetBottom = -8;
            _infoPanel.AddChild(_infoText);
            // "copy" button: copies the ENTIRE log to the clipboard in one
            // click - manual select-all over 100 lines is the pain this
            // removes.
            _infoCopyBtn = new Button { Text = "copy" };
            _infoCopyBtn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            _infoCopyBtn.OffsetLeft = -110; _infoCopyBtn.OffsetTop = 2;
            _infoCopyBtn.OffsetRight = -8;   _infoCopyBtn.OffsetBottom = 26;
            _infoCopyBtn.Pressed += () =>
            {
                DisplayServer.ClipboardSet(_infoText.Text);
                _infoCopyBtn.Text = "copied!";
                _copyFlashUntil = Time.GetTicksMsec() + 1200;
            };
            _infoPanel.AddChild(_infoCopyBtn);
            _mmHud.AddChild(_infoPanel);
        }
        _minimap.Setup(_mmSegs, _bounds, roadColor, _cam);
        // The inset doubles as the VIEW HANDLE (binding user decision 2026-10-22):
        // press inside it, and the yellow box that names the main window's view
        // SPRINGS to the hand with the cursor dead centre in it; holding and
        // dragging walks it; the wheel over it sizes it. Godot routes events
        // inside an embedded window to that window's own nodes - a press pushed
        // at (1384,206), inside the inset's rect (1240,64)+(360,284.2), never
        // reached the main window's _Input at all - so the gesture is read by
        // MinimapNode in the inset's pixels and arrives here as the WORLD point
        // the hand named. The camera stays the host's: the box and the view are
        // joined through one scale, so they cannot drift apart.
        _minimap.PinView = world =>
        {
            var want = ClampViewCentre(world, "minimap handle");
            if (_review)
                // The review pin re-derives itself every substep, so the
                // hand's absolute point is remembered and applied by
                // ReviewCamera - writing it to the camera here loses it one
                // frame later (measured: (1399.7,-837.8) -> (775.9,-101.5)).
                _mmAsk = want;
            else
                _cam.GlobalPosition = want;
            _lastManualMs = Time.GetTicksMsec();    // the hand owns the view
            _minimap.QueueRedraw();
            if (_mmDbg < 6)
            {
                _mmDbg++;
                var vp = GetViewport().GetVisibleRect().Size;
                GD.Print($"MINIMAP-PIN cam=({_cam.GlobalPosition.X:F1},{_cam.GlobalPosition.Y:F1}) " +
                         $"hand named=({world.X:F1},{world.Y:F1}) m · view {vp.X / _cam.Zoom.X:F0} m " +
                         $"box {(vp.X / _cam.Zoom.X) * (_minimap.Size.X / _bounds.Size.X):F0} px " +
                         $"· clamp [{_bounds.Position.X - PanBeyondMapM:F0},{_bounds.End.X + PanBeyondMapM:F0}] m");
            }
        };
        _minimap.ZoomView = (world, dir) =>
        {
            ZoomBy(dir > 0 ? ZoomStep : 1f / ZoomStep, ScreenOfWorld(world));
            if (_mmDbg < 12)
            {
                _mmDbg++;
                var vp = GetViewport().GetVisibleRect().Size;
                GD.Print($"MINIMAP-ZOOM dir {(dir > 0 ? "in" : "out")} · zoom {_cam.Zoom.X:F3} px/m " +
                         $"· view {vp.X / _cam.Zoom.X:F0} m · box {(vp.X / _cam.Zoom.X) * (_minimap.Size.X / _bounds.Size.X):F0} " +
                         $"px of the inset's {_minimap.Size.X:F0} px · map {_bounds.Size.X:F0} m");
            }
        };
        // A second map with a different aspect re-fits the window (the nodes
        // are built once, the shape is not).
        _minimap.Size = mmBox;
        _mmWin.Size = MmWindowSize(mmBox);
        _lastCamPos = _cam.GlobalPosition;
        _lastCamZoom = zoom;
        _minimap.QueueRedraw();
        EnforceWindows();

        GD.Print($"Map ready: bounds {_bounds}, zoom {zoom:F3}");

        if (_screenshotPath != null && !_testPan)
            GetTree().CreateTimer(_shotDelay).Timeout += () => SaveShot(_screenshotPath);
        if (_resizeTo.X > 0 && _resizeTo.Y > 0)
        {
            // 1.5 s BEFORE the shot (and never at t=0 - the point of the hook
            // is a size change that happens AFTER the HUD exists), so a run
            // with both flags pictures the resized window.
            float when = Mathf.Max(0.6f, _shotDelay - 1.5f);
            GetTree().CreateTimer(when).Timeout += () =>
            {
                GetWindow().Size = _resizeTo;   // position stays where it is
                GD.Print($"window resized to {_resizeTo} (--resize)");
            };
        }
    }

    /// <summary>The inset's box for a map: BoxSize on the LONG side, the
    /// world's aspect on the other. A square box forced two different scales
    /// on x and y (a 2470 x 1950 m network drawn 1.27x too tall) and, being
    /// smaller than its own window, left the map floating inside empty frame.</summary>
    static Vector2 MmBoxFor(Rect2 bounds)
    {
        float ar = bounds.Size.Y > 0f ? bounds.Size.X / bounds.Size.Y : 1f;
        return ar >= 1f
            ? new Vector2(MinimapNode.BoxSize, MinimapNode.BoxSize / Math.Max(0.05f, ar))
            : new Vector2(MinimapNode.BoxSize * Math.Max(0.05f, ar), MinimapNode.BoxSize);
    }

    /// <summary>The map window's size for an inset: THE INSET. The content is
    /// the window - no band for a caption line any more (user decision
    /// 2026-10-22: "make the minimap content fit in the window", and the
    /// position/zoom line under the inset is what had to go).</summary>
    static Vector2I MmWindowSize(Vector2 box) => new((int)box.X, (int)box.Y);

    // ------------------------------------------- static marking geometry

    /// <summary>Markings are paint: every line keeps its PHYSICAL width (a
    /// centreline is 0.15 m of white, a Breitstrich twice that) at full
    /// opacity, so the meshes are world-space and built ONCE per map -
    /// nothing depends on the zoom, so nothing is rebuilt when the camera
    /// moves. The only zoom response is switching whole batches off (the
    /// far-cull ones: centre and halt lines). Sub-pixel edges belong to
    /// the anti-aliasing pass (MSAA 4x in project settings), not to a
    /// fade.</summary>
    private void RebuildMarkings()
    {
        foreach (var child in _markLayer.GetChildren())
            child.QueueFree();
        _farMarkings.Clear();

        var verts = new Dictionary<(float w, Color c, bool elev, bool cull), List<Vector3>>();
        // The class the CURRENT line belongs to; Quad reads it, so lines
        // that switch off when zooming out get their own mesh.
        bool cull = false;
        void Quad(Color c, float w, Vector2 a, Vector2 b, bool elev)
        {
            if (!verts.TryGetValue((w, c, elev, cull), out var list))
            {
                list = new List<Vector3>();
                verts[(w, c, elev, cull)] = list;
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
            cull = line.FarCull;
            if (line.Solid)
            {
                // Lane boundaries get the Breitstrich (double width); a
                // halt line is painted at its own physical width.
                float w = line.Breitstrich ? 2f * line.WidthM : line.WidthM;
                for (int i = 0; i < line.Pts.Count - 1; i++)
                    Quad(line.Color, w, line.Pts[i], line.Pts[i + 1], line.Elevated);
            }
            else if (line.DashM > 0f)
                AddDashes(line.Elevated
                    ? (Color c, float w, Vector2 a, Vector2 b) =>
                        Quad(c, w, a, b, true)
                    : (Color c, float w, Vector2 a, Vector2 b) =>
                        Quad(c, w, a, b, false),
                    line.Color, line.WidthM, line.Pts, line.DashM, line.GapM);
        }

        // A disc is a triangle fan - batched with the rest of the paint.
        cull = false;
        const int segs = 16;
        var white = new Color(1f, 1f, 1f);
        foreach (var (c, r) in _crossingDots)
        {
            if (!verts.TryGetValue((2f * r, white, false, false), out var list))
            {
                list = new List<Vector3>();
                verts[(2f * r, white, false, false)] = list;
            }
            for (int i = 0; i < segs; i++)
            {
                float a0 = Mathf.Tau * i / segs, a1 = Mathf.Tau * (i + 1) / segs;
                list.Add(new Vector3(c.X, c.Y, 0));
                list.Add(new Vector3(c.X + r * Mathf.Cos(a0), c.Y + r * Mathf.Sin(a0), 0));
                list.Add(new Vector3(c.X + r * Mathf.Cos(a1), c.Y + r * Mathf.Sin(a1), 0));
            }
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
            if (kv.Key.cull) _farMarkings.Add(mi);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The port a REST URL names ("http://127.0.0.1:5001/map" ->
    /// 5001); 0 when it does not name one.</summary>
    /// <summary>The ONE name matcher both clients use: the live path feeds
    /// the cast it reads from /state, the reviewer feeds the tape header's
    /// names= map. Two sources, one rule - "the parked car" means the same
    /// car in the window and in the review of the same run.</summary>
    private static long MatchName(string name, Dictionary<long, string> cast)
    {
        foreach (var kv in cast)
            if (kv.Value == name) return kv.Key;
        return 0L;
    }

    /// <summary>One warm cone from the conceder toward the car it grants
    /// passage to. Length scales with the gap (capped), so the gesture reads
    /// as "after you" in the direction of the OTHER car rather than as a
    /// headlight beam.</summary>
    private void DrawCourtesy(long uid, Node2D node, CarMeta meta)
    {
        if (!_courtesy.TryGetValue(uid, out var cone))
        {
            cone = new Polygon2D();
            _world.AddChild(cone);
            _courtesy[uid] = cone;
        }
        if (meta.CourtesyFor == 0L || !_carNodes.TryGetValue(meta.CourtesyFor, out var target))
        {
            cone.Visible = false;
            return;
        }
        var to = target.GlobalPosition - node.GlobalPosition;
        var dM = to.Length() / _cam.Zoom.X;
        if (dM < 0.2) { cone.Visible = false; return; }
        var dir = to.Normalized();
        var len = Mathf.Clamp((float)dM * 0.45f, 0.6f, 2.6f);
        var side = len * 0.22f;
        cone.Polygon = new Vector2[]
        {
            Vector2.Zero,
            dir * len + new Vector2(-dir.Y, dir.X) * side,
            dir * len - new Vector2(-dir.Y, dir.X) * side,
        };
        cone.GlobalPosition = node.GlobalPosition + dir * 0.35f * len;
        cone.Color = new Color(1f, 0.85f, 0.45f, 0.38f);
        cone.Visible = true;
        cone.ZIndex = 40;
    }

    private static int PortOfUrl(string url)
    {
        var at = url.LastIndexOf(':');
        if (at < 0) return 0;
        var n = 0;
        for (int i = at + 1; i < url.Length && url[i] >= '0' && url[i] <= '9'; i++)
            n = n * 10 + (url[i] - '0');
        return n;
    }

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

    /// <summary>Fills every road ribbon as triangles in ONE mesh. A strip is
    /// vertex-paired, so quad i is (left[i], left[i+1], right[i+1], right[i])
    /// — no triangulation of a possibly self-crossing outline is needed, a
    /// figure-8 crossing is simply two quads of the same colour on top of
    /// each other, which is what it looks like from above.</summary>
    private void AddRoadStrips(JsonElement stripsEl, Color color)
    {
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        mesh.SurfaceSetColor(color);
        int quads = 0;
        foreach (var s in stripsEl.EnumerateArray())
        {
            var left = Pts(s.GetProperty("left"));
            var right = Pts(s.GetProperty("right"));
            if (left.Count != right.Count || left.Count < 2) continue;
            for (int i = 0; i + 1 < left.Count; i++)
            {
                Vector2 a = left[i], b = left[i + 1], c = right[i + 1], d = right[i];
                mesh.SurfaceAddVertex(new(a.X, a.Y, 0));
                mesh.SurfaceAddVertex(new(b.X, b.Y, 0));
                mesh.SurfaceAddVertex(new(c.X, c.Y, 0));
                mesh.SurfaceAddVertex(new(a.X, a.Y, 0));
                mesh.SurfaceAddVertex(new(c.X, c.Y, 0));
                mesh.SurfaceAddVertex(new(d.X, d.Y, 0));
                quads += 1;
            }
        }
        mesh.SurfaceEnd();
        if (quads == 0) return;
        _world.AddChild(new MeshInstance2D { Mesh = mesh, ZIndex = 0 });
    }

    /// <summary>Bridge decks (z 20) sit above everything else in the world,
    /// so painting their islands with the background cannot cover anything it
    /// shouldn't — unlike the ground roads, which are painted as ribbons and
    /// need no paint-out at all.</summary>
    private void AddDeckFill(JsonElement r, Color color, int z, Color bg)
    {
        var pts = Pts(r.GetProperty("exterior"));
        if (pts.Count < 3) return;
        AddPolygon(pts, color, z);
        if (r.TryGetProperty("holes", out var holes))
            foreach (var h in holes.EnumerateArray())
            {
                var hp = Pts(h);
                if (hp.Count >= 3) AddPolygon(hp, bg, z + 1);
            }
    }

    delegate void QuadFn(Color c, float w, Vector2 a, Vector2 b);

    /// <summary>Walk a polyline at constant arc length emitting dash quads —
    /// the dash/gap pattern comes from the sim (marking_style), so the paint
    /// matches what the road network says.</summary>
    static void AddDashes(QuadFn quad, Color c, float widthM,
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
                    quad(c, widthM, a + u * traveled, a + u * (traveled + step));
                traveled += step;
                intoPeriod += step;
            }
        }
    }

    // ---------------------------------------------------------------- camera

    // Camera zoom limits in px/m (the client's own view, not the sim's):
    // 0.24 px/m shows ~5 km across a 1280 px window, 64 px/m shows ~20 m.
    const float ZoomMin = 0.24f;
    const float ZoomMax = 64f;
    // Paint widths in METRES: a kerb line, a centreline and a tyre track are
    // all ~15 cm of paint, so they scale with the view instead of being
    // floored to screen pixels.
    const float EdgeLineWidthM = 0.15f;
    const float TrailWidthM = 0.15f;
    // Edge lines are hidden entirely below this zoom (6 px/m) instead of
    // turning into sub-pixel noise (user decision).
    const float EdgeLineMinZoom = 6.0f;
    /// <summary>Centre and halt lines stay longer than the kerb lines: the
    /// user asked for them to survive further out, so they switch off at a
    /// LOWER zoom than the road's outer lines (6 px/m) - only when the road
    /// itself is already tiny. Below this they would be sub-pixel noise.</summary>
    const float MarkingMinZoom = 2.0f;
    const float ZoomStep = 1.15f;   // wheel/pinch step per notch

    bool _dragging;
    bool _stripDrag;   // a left-button drag that BEGAN on the review strip
    Vector2 _dragStartMouse, _dragStartCam;

    public override void _Input(InputEvent e)
    {
        if (_review) ReviewInput(e);
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
                // Pan keys pause the follow temporarily - it resumes once
                // the input stops. F releases the car for real.
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
                // The instrument strip is the strip's OWN business: a press
                // in it is a seek or a button, never a grab of the map.
                // ReviewScrubZone calls AcceptEvent(), which only shields the
                // nodes BEHIND it - the old-style _Input() here still receives
                // the same event, so without this decline one bar gesture
                // meant two things at once: scrub AND pan (measured, review
                // mode: press at y 771 inside the seek band 686..856 plus a
                // 60 px move put the review pin at (-136.8, 0) and slid the
                // map from cam 848.25 m to 648.92 m while the cursor moved
                // from frame 8 to 10). The guard is ReviewSelect's y test for
                // the car pick, so "over the bar" means one thing to both the
                // pick and the grab - and the drag keeps its full length even
                // when it started at the strip's edge.
                if (_review && (mb.Position.Y > _barTop - 6.0 || _stripDrag))
                {
                    _stripDrag = mb.Pressed;      // the strip owns it, alone
                    _dragging = false;            // no grab starts
                }
                else
                {
                    _stripDrag = false;
                    // Drag the map with the left button (same as pygame).
                    _dragging = mb.Pressed;
                    if (mb.Pressed) _lastManualMs = Time.GetTicksMsec();
                    _dragStartMouse = mb.Position;
                    _dragStartCam = ClampViewCentre(_cam.GlobalPosition);
                    _dragStartPan = _reviewPan;
                    // A short click (release without moving the map) selects
                    // the car under the cursor; empty space clears the
                    // selection. INSIDE the map branch: the strip's own click
                    // is picked by ReviewSelect, which knows the tape's cars -
                    // this path only sees the live _carNodes.
                    if (!mb.Pressed &&
                        (mb.Position - _dragStartMouse).Length() < 6f)
                        SelectCarAt(mb.Position);
                }
            }
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            // Grab behavior: the world point under the cursor stays put.
            // (screen and Godot world both have +y down, so minus on both axes)
            var d = mm.Position - _dragStartMouse;
            // In review mode the camera is PINNED on the tape frame every
            // substep, so writing the drag to the camera is undone
            // immediately - panning did nothing at all. The drag belongs to
            // the pin instead: an offset the pin adds to.
            if (_review)
                _reviewPan = _dragStartPan -
                    new Vector2(d.X / _cam.Zoom.X, d.Y / _cam.Zoom.Y);
            else
                // Panning is bounded by the network plus the belt around it
                // (binding user decision 2026-10-22) - the same limit the
                // minimap's handle obeys, so a hand on the map and a hand on
                // the inset cannot end up with two different countries in view.
                _cam.GlobalPosition = ClampViewCentre(new Vector2(
                    _dragStartCam.X - d.X / _cam.Zoom.X,
                    _dragStartCam.Y - d.Y / _cam.Zoom.Y), "map drag");
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
                _cam.GlobalPosition = ClampViewCentre(_cam.GlobalPosition + px / _cam.Zoom.X, "trackpad");
            }
        }
    }

    /// <summary>The world point an inset pixel names, projected to the pixel
    /// of the MAIN window that shows it now - the anchor a wheel has to
    /// multiply by, because the camera lives here and the inset only knows
    /// its own pixels.</summary>
    private Vector2 ScreenOfWorld(Vector2 w)
    {
        var vp = GetViewport().GetVisibleRect().Size;
        return vp / 2f + (w - _cam.GlobalPosition) * _cam.Zoom.X;
    }

    /// <summary>Where the view may be parked: the roads plus PanBeyondMapM
    /// on every side (binding user decision 2026-10-22). Every hand - the map
    /// drag, the pan keys, the trackpad, the minimap's own handle - goes
    /// through here, so they cannot disagree about the legal country. Asked
    /// and got are logged when a hand tries to leave (the only moment the
    /// limit is visible at all).</summary>
    private Vector2 ClampViewCentre(Vector2 want, string why = "")
    {
        var got = new Vector2(
            Mathf.Clamp(want.X, _bounds.Position.X - PanBeyondMapM, _bounds.End.X + PanBeyondMapM),
            Mathf.Clamp(want.Y, -(_bounds.End.Y + PanBeyondMapM), -(_bounds.Position.Y - PanBeyondMapM)));
        if (why != "" && _clampDbg < 6 && got != want)
        {
            _clampDbg++;
            GD.Print($"VIEW-CLAMP {why}: asked=({want.X:F1},{want.Y:F1}) -> ({got.X:F1},{got.Y:F1}) m " +
                     $"· legal [{_bounds.Position.X - PanBeyondMapM:F0},{_bounds.End.X + PanBeyondMapM:F0}] x " +
                     $"[{-_bounds.End.Y - PanBeyondMapM:F0},{-_bounds.Position.Y + PanBeyondMapM:F0}] (godot y)");
        }
        return got;
    }

    /// <summary>The smallest zoom whose view still fits the inset - the map's
    /// whole extent, i.e. the box at the inset's own size.</summary>
    private float MinZoomOverMap()
    {
        if (_bounds.Size.X <= 0f || _bounds.Size.Y <= 0f) return ZoomMin;
        var vp = GetTree().Root.GetVisibleRect().Size;   // the MAIN window, not the inset
        return Math.Max(vp.X / _bounds.Size.X, vp.Y / _bounds.Size.Y);
    }

    /// <summary>Zoom by f, keeping the world point under `at` fixed.</summary>
    void ZoomBy(float f, Vector2? at = null)
    {
        float z0 = _cam.Zoom.X;
        // Zooming out stops where the yellow box would fill the minimap
        // (binding user decision 2026-10-22): the box IS the view, drawn at
        // the inset's scale, so a view wider than the map's own extent would
        // need a box wider than the inset.
        float z1 = Mathf.Clamp(z0 * f, Mathf.Max(ZoomMin, MinZoomOverMap()), ZoomMax);
        if (Mathf.IsEqualApprox(z1, z0)) return;
        var vp = GetViewport().GetVisibleRect().Size;
        Vector2 s = at ?? vp / 2f;
        _cam.GlobalPosition += (s - vp / 2f) * (1f / z0 - 1f / z1);
        _cam.Zoom = new Vector2(z1, z1);
        // While following, a wheel-zoom counts as manual control: the follow
        // resumes once the input stops (the zoom itself is never overridden).
        if (_followUid.HasValue) _lastManualMs = Time.GetTicksMsec();
    }

    public override void _Process(double delta)
    {
        // Embedded source (Phase 7): read the in-process snapshot every frame
        // and feed it through the SAME ingest path as the HTTP pipeline
        // (serialize + parse keeps state application single-sourced; the cost
        // is negligible vs an HTTP round trip - profiled at G5 via the EMBED
        // motionlog line).
        if (_review)
        {
            ReviewTick(delta);
        }
        else if (_simHost != null)
        {
            long t0 = (long)Time.GetTicksUsec();
            IngestState(JsonSerializer.SerializeToUtf8Bytes(
                _simHost.Engine.StateSnapshot()), "embedded /state");
            if (_motionLog != null)
                _motionLog.WriteLine($"EMBED {(long)Time.GetTicksUsec() - t0}us");
        }

        // Fleet flow readout: updated in _Process (NOT in UpdateCars) so it
        // shows even before a car exists - the numbers are a property of the
        // sim, not of the cars on screen. Hidden in review mode: those numbers
        // describe the LIVE sim, and the review window's own readout already
        // carries the tape's fluidity.
        // Fleet flow readout. The numbers only mean anything for a MULTI-car
        // run (a single car's flow is just its own speed), so the line is
        // hidden otherwise - and what is shown is PER CAR: the fleet total
        // divided by the number of cars involved.
        if (_flowLabel != null && !_review)
        {
            int nCars = _carBuf.Count;
            bool show = _flowRate >= 0.0 && nCars > 1;
            _flowLabel.Visible = show;
            if (show)
                _flowLabel.Text = $"flow/car {_flowRate / nCars:F1} m/s  " +
                                  $"{_flowTotal / nCars:F0} m per car " +
                                  $"({nCars} cars)" +
                                  (_standstillN > 0
                                      ? $"  !! stuck: {_standstillN}" : "");
        }

        var dir = Vector2.Zero;
        if (_keys.Contains(Key.A) || _keys.Contains(Key.Left)) dir.X -= 1;
        if (_keys.Contains(Key.D) || _keys.Contains(Key.Right)) dir.X += 1;
        // "up" is north in the sim's world = -y in Godot.
        if (_keys.Contains(Key.W) || _keys.Contains(Key.Up)) dir.Y -= 1;
        if (_keys.Contains(Key.S) || _keys.Contains(Key.Down)) dir.Y += 1;
        if (dir != Vector2.Zero)
        {
            _cam.GlobalPosition = ClampViewCentre(
                _cam.GlobalPosition + dir.Normalized() * 600f / _cam.Zoom.X * (float)delta,
                "pan keys");
            _lastManualMs = Time.GetTicksMsec();
        }

        // The map window belongs to a corner - a resize has to carry it there
        // again (its position is absolute, so after --resize it sat in the
        // middle of the top edge, on top of the readout it must not cover).
        PinMapWindow();
        // And no window, including this one, may sit partly out of the main
        // window or touch a neighbour: the guard puts them back (a hand can
        // drag a title bar anywhere - Godot does not stop it).
        EnforceWindows();

        // M2: interpolate cars from the /state buffer + camera follow.
        UpdateCars();

        if (_motionLog != null)
            foreach (var kv in _carNodes)
                _motionLog.WriteLine($"{Time.GetTicksMsec()} {kv.Key} " +
                    $"{kv.Value.GlobalPosition.X:F2} {kv.Value.GlobalPosition.Y:F2} " +
                    // camera world pos: screen-space car motion = (car-cam)*zoom
                    $"{_cam.GlobalPosition.X:F2} {_cam.GlobalPosition.Y:F2} " +
                    $"{_simNow:F3} {(_rtClamped ? 1 : 0)} {_renderTarget:F4} " +
                        $"{_cam.Zoom.X:F1}");

        // Breadcrumb trails + test flags (rebuild on data/zoom change).
        UpdateOverlays();

        // Kerb lines are 0.15 m of paint: below ~1.5x they are sub-pixel and
        // would only shimmer, so they switch off instead of fading in and out.
        float s = _cam.Zoom.X;
        bool edgeVisible = s >= EdgeLineMinZoom;
        foreach (var l in _edgeLines)
            l.Visible = edgeVisible;
        foreach (var l in _bridgeEdges)
            l.Visible = edgeVisible;
        // Centre and halt lines keep going where the kerb lines have gone:
        // they switch off only below MarkingMinZoom, further out than the
        // road's outer lines (user decision).
        bool paintVisible = s >= MarkingMinZoom;
        foreach (var m in _farMarkings)
            m.Visible = paintVisible;

        // Minimap only changes when the camera moves.
        if (_minimap != null &&
            (_cam.GlobalPosition != _lastCamPos || _cam.Zoom.X != _lastCamZoom))
        {
            _lastCamPos = _cam.GlobalPosition;
            _lastCamZoom = s;
            _minimap.QueueRedraw();
        }
    }

    // ------------------------------------------------- M2: car tracking

    /// <summary>Decision-log response for the SELECTED car. Stale responses
    /// (selection changed in flight) are dropped; the poll re-fires on the
    /// next frame. Failures just keep the last lines (server may be down).
    /// </summary>
    private void OnDecisionsResponse(long responseCode, byte[] body)
    {
        _decBusy = false;
        if (responseCode != 200 || _selectedUid is not long sel) return;
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (!root.TryGetProperty("uid", out var uEl) || uEl.GetInt64() != sel)
                return;
            _decUidFor = sel;
            var lines = new List<string>();
            if (root.TryGetProperty("decisions", out var arr))
                foreach (var d in arr.EnumerateArray())
                    lines.Add($"{d.GetProperty("t").GetDouble():F1}s  " +
                              d.GetProperty("msg").GetString());
            if (lines.Count == 0)
            {
                _decLines = new List<string> { "(no decisions yet)" };
                return;
            }
            lines = lines.TakeLast(5).ToList();
            lines.Reverse();   // newest first
            _decLines = lines;
        }
        catch
        {
            _decLines = new List<string> { "(unparsable response)" };
        }
    }

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
        IngestState(body, "GET /state");
    }

    /// <summary>Parse + apply one state frame. BOTH sources feed this single
    /// code path: the HTTP pipeline ("http" source) and the per-frame
    /// in-process snapshot read ("embedded" source, Phase 7) - no divergence.
    /// </summary>
    private void IngestState(byte[] body, string origin)
    {
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

            // The host's pacing switch: how much sim time runs per wall
            // second (--warp N; --fast announces 0 = "measure it"). The
            // render clock below scales its bounds by it, so a warped run
            // still interpolates instead of starving to hold-and-jump.
            if (root.TryGetProperty("time_warp", out var twEl))
                _timeWarp = Math.Max(0.0, twEl.GetDouble());

            // Snap only on the very first arrival or a big discontinuity
            // (restart/teleport).
            bool newFrame = t > prevMax;
            if (newFrame &&
                (_simNow == double.NegativeInfinity || Math.Abs(t - _simNow) > 1.0))
            {
                _simNow = t;
                _rateRefSim = t;
                _rateRefWall = Time.GetTicksMsec() / 1000.0;
                _simRate = _timeWarp >= 1.0 ? _timeWarp : 1.0;
            }

            var seen = new HashSet<long>();
            void Add(long uid, float xM, float yM, float hdg,
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
                    Time = t, X = xM, Y = yM,
                    HeadingDeg = hdg, SpeedKmh = kmh, Level = level,
                });
                RecordTrail(uid, t, xM, yM, hdg);
                if (_motionLog != null)
                    _motionLog.WriteLine($"SAMP {t:F3} {xM:F2} {yM:F2}");
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
                long roleParked = 0, roleSlowest = 0, roleNamed = 0;
                var liveCast = new Dictionary<long, string>();
                double slowestKmh = 1e9, fastestKmh = 0.0;
                foreach (var c in cars.EnumerateArray())
                {
                    long uid = c.GetProperty("car_uid").GetInt64();
                    if (!_carBuf.ContainsKey(uid)) anyNewCar = true;
                    if (uid > newestUid) newestUid = uid;
                    double kmh = c.GetProperty("speed_kmh").GetSingle();
                    Add(uid,
                        c.GetProperty("x").GetSingle(), c.GetProperty("y").GetSingle(),
                        c.GetProperty("heading").GetSingle(), (float)kmh,
                        c.GetProperty("level").GetInt32());
                    // Per-car display state: color, lights, OWN flags/label.
                    if (!_carMeta.TryGetValue(uid, out var m))
                        _carMeta[uid] = m = new CarMeta();
                    m.Color = c.TryGetProperty("color", out var cc)
                               && cc.ValueKind == JsonValueKind.String
                           ? cc.GetString() : "blue";
                    m.Driver = c.TryGetProperty("driver", out var drv)
                                && drv.ValueKind == JsonValueKind.String
                            ? drv.GetString() : "BICYCLE";
                    m.BlinkL = c.TryGetProperty("blinker_left", out var bl) && bl.GetBoolean();
                    m.BlinkR = c.TryGetProperty("blinker_right", out var br) && br.GetBoolean();
                    m.Hazard = c.TryGetProperty("hazard", out var hz) && hz.GetBoolean();
                    m.Braking = c.TryGetProperty("braking", out var bk2) && bk2.GetBoolean();
                    if (_focusRole.Length > 0 || _focusName.Length > 0)
                    {
                        if (c.TryGetProperty("parking", out var pkv) &&
                            pkv.ValueKind == JsonValueKind.Object &&
                            pkv.TryGetProperty("parked", out var pkb) &&
                            pkb.ValueKind == JsonValueKind.True)
                            roleParked = uid;
                        // "slow" must not pick a stopped car - the crawler is
                        // rolling; a standstill is a different story.
                        if (_focusName.Length > 0 &&
                            c.TryGetProperty("hud_label", out var hdl) &&
                            hdl.ValueKind == JsonValueKind.String)
                            liveCast[uid] = hdl.GetString();
                        if (kmh > 1.8)
                        {
                            if (kmh < slowestKmh)
                            {
                                slowestKmh = kmh;
                                roleSlowest = uid;
                            }
                            if (kmh > fastestKmh) fastestKmh = kmh;
                        }
                    }
                    m.HeadlightsOn = c.TryGetProperty("headlights_on", out var lo1) && lo1.GetBoolean();
                    m.CourtesyFor = c.TryGetProperty("courtesy_for", out var cfu) &&
                                    cfu.ValueKind == JsonValueKind.Number
                        ? cfu.GetInt64() : 0L;
                    m.CourtesyKind = c.TryGetProperty("courtesy_kind", out var cfk) &&
                                     cfk.ValueKind == JsonValueKind.String
                        ? cfk.GetString() : "";
                    m.TaillightsOn = c.TryGetProperty("taillights_on", out var lo2) && lo2.GetBoolean();
                    m.FlagGreen = ParseFlag(c, "green");
                    m.FlagRed = ParseFlag(c, "red");
                    m.HudLabel = c.TryGetProperty("hud_label", out var hlc)
                                  && hlc.ValueKind == JsonValueKind.String
                           ? hlc.GetString() : null;
                    m.InContact = c.TryGetProperty("in_contact", out var ic)
                                  && ic.GetBoolean();
                    // Crash camera (see the field): crash_episodes is
                    // monotonic, in_contact is transient and can clear
                    // between two client frames - either marks the impact,
                    // the first car to do so wins.
                    int crashEps = c.TryGetProperty("crash_episodes", out var ce)
                                   && ce.ValueKind == JsonValueKind.Number
                        ? ce.GetInt32() : 0;
                    int seenEps = _crashCounts.ContainsKey(uid) ? _crashCounts[uid] : 0;
                    if (_crashFocusUid == 0L &&
                        (crashEps > seenEps || m.InContact))
                        _crashFocusUid = uid;
                    _crashCounts[uid] = Math.Max(seenEps, crashEps);
                    // Collision footprint from the four body_corners (METRES,
                    // like every other /state coordinate): length = front->rear
                    // edge, width = left->right.
                    if (c.TryGetProperty("body_corners", out var bc)
                        && bc.ValueKind == JsonValueKind.Array && bc.GetArrayLength() == 4)
                    {
                        var fr = bc[0]; var fl = bc[1]; var rl = bc[2];
                        float frx = fr[0].GetSingle(), fry = fr[1].GetSingle();
                        float flx = fl[0].GetSingle(), fly = fl[1].GetSingle();
                        float rlx = rl[0].GetSingle(), rly = rl[1].GetSingle();
                        m.CollWidM = Mathf.Sqrt((frx - flx) * (frx - flx)
                                                + (fry - fly) * (fry - fly));
                        m.CollLenM = Mathf.Sqrt((flx - rlx) * (flx - rlx)
                                                + (fly - rly) * (fly - rly));
                    }
                }
                // Multi-car auto-follow: a freshly spawned car takes over
                // the camera binding - mirrors the sim, which follows the
                // LAST teleported car. Skipped after F (follow released) or
                // while no new car arrived (TAB stays in effect), and once
                // the crash camera has latched - the impact outranks a spawn.
                // A role focus re-resolves every frame: the staged uid is
                // unknown before the run, and the car that answers "the
                // parked one" or "the slow one" is defined by the state, not
                // by a number the scenario happens to hand out.
                // A role names ONE car for the whole run: once the window has
                // latched onto it, it stays - re-picking the per-frame minimum
                // made the camera flap between the crawler and the primary
                // every time the crawler stopped for a pass.
                if (_focusName.Length > 0) roleNamed = MatchName(_focusName, liveCast);
                long rolePick = _focusName.Length > 0 ? roleNamed
                               : _focusRole == "parked" ? roleParked
                               : _focusRole == "slow" ? roleSlowest : 0;
                // "the slow car" must be slow RELATIVE to the traffic, not
                // merely the minimum: at staging time only the primary rolls,
                // and calling it the slow car latched the camera onto the
                // wrong car for the whole run.
                if (_focusName.Length == 0 && _focusRole == "slow" &&
                    !(fastestKmh > 0.0 && slowestKmh <= 0.6 * fastestKmh))
                    rolePick = 0;   // a NAME needs no relative-speed guard
                if (rolePick != 0 && _focusUid == 0)
                {
                    _focusUid = rolePick;
                    // The role re-resolves every frame, so the window follows
                    // the car that ANSWERS the role - in slow_pass_overtake
                    // that is the crawler, which only exists after staging, so
                    // the binding has to move once it does.
                    if (_focusUid != _focusAnnouncedUid)
                    {
                        _focusAnnouncedUid = _focusUid;
                        GD.Print($"CAM-FOCUS {(_focusName.Length > 0 ? "name=" + _focusName
                                                           : "role=" + _focusRole)} -> uid {_focusUid}");
                    }
                }
                else if ((_focusRole.Length > 0 || _focusName.Length > 0) &&
                          _focusUid == 0 && !_focusAnnounced && ++_focusFrames > 900)
                {
                    _focusAnnounced = true;
                    GD.Print($"CAM-FOCUS {(_focusName.Length > 0 ? _focusName : _focusRole)} " +
                             $"UNRESOLVED after {_focusFrames} frames");
                }

                if (_autoFollow && anyNewCar && newestUid > 0 && _crashFocusUid == 0L)
                    _followUid = newestUid;
            }
            else if (root.TryGetProperty("has_car", out var hc) && hc.GetBoolean())
                Add(root.GetProperty("car_uid").GetInt64(),
                    root.GetProperty("x").GetSingle(), root.GetProperty("y").GetSingle(),
                    root.GetProperty("heading").GetSingle(),
                    root.GetProperty("speed_kmh").GetSingle(),
                    root.GetProperty("level").GetInt32());

            // The view is the client's own Camera2D - /state carries no
            // camera state (see FollowCamera()).

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
                m.Braking = root.TryGetProperty("braking", out var b3) && b3.GetBoolean();
                m.HeadlightsOn = root.TryGetProperty("headlights_on", out var b4) && b4.GetBoolean();
                m.TaillightsOn = root.TryGetProperty("taillights_on", out var b5) && b5.GetBoolean();
            }

            // HUD test label ("5/21") - the PRIMARY car's label, string or null.
            if (root.TryGetProperty("hud_label", out var hl))
                _lastTestText = hl.ValueKind == JsonValueKind.String
                    ? hl.GetString() : null;

            // Fleet flow (the quality metric of 2026-10-19): metres driven by
            // the whole fleet per second of sim time, and the absolute total
            // since the sim started (see docs §8 R28 / SimEngine _flowMeters).
            if (root.TryGetProperty("flow_m_per_s", out var fr28) &&
                fr28.ValueKind == JsonValueKind.Number)
                _flowRate = fr28.GetDouble();
            if (root.TryGetProperty("flow_m_total", out var ft28) &&
                ft28.ValueKind == JsonValueKind.Number)
                _flowTotal = ft28.GetDouble();
            // R31 standstill errors: cars that moved < 1 m within the window.
            if (root.TryGetProperty("standstill_errors", out var se28) &&
                se28.ValueKind == JsonValueKind.Number)
                _standstillN = se28.GetInt32();

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
            GD.PrintErr($"{origin}: bad payload: {e.Message}");
        }
    }

    static (float X, float Y, float Hdg)? ParseFlag(JsonElement root, string key)
    {
        if (!root.TryGetProperty("flags", out var fl) ||
            fl.ValueKind != JsonValueKind.Object || !fl.TryGetProperty(key, out var f))
            return null;
        if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() < 3) return null;
        var a = f.EnumerateArray().ToArray();
        return (a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
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
        _brakeLights.Remove(uid);
        _headlights.Remove(uid);
        if (_carNodes.TryGetValue(uid, out var n))
        { n.QueueFree(); _carNodes.Remove(uid); }
        if (_followUid == uid) _followUid = null;
        if (_selectedUid == uid) _selectedUid = null;
    }

    void ClearCars()
    {
        foreach (var uid in _carBuf.Keys.ToArray()) RemoveCar(uid);
        _lastFlagFp = "";
        _lastMaxTime = double.NegativeInfinity;
        _simNow = double.NegativeInfinity;
        _rateRefSim = -1;
        _simRate = 1.0;
        _renderTarget = float.NegativeInfinity;
    }

    Node2D MakeCarNode(long uid)
    {
        // All sprites have the nose pointing UP (north), so
        // rotation_degrees = heading directly. The node sits on the REAR
        // AXLE (/state x/y); the sprite is centred on the BODY centre,
        // RearAxleOffsetM ahead of it.
        var root = new Node2D { Name = $"car_{uid}" };
        // Multi-car: each car gets its own vehicle sprite (see CarTexture).
        // Meta may not exist yet on the very first sample - red is the
        // safe default.
        string color = _carMeta.TryGetValue(uid, out var meta) ? meta.Color : "blue";
        var tex = CarTexture(color);
        var size = CarSizeOf(color);
        // The texture is stretched to the sprite's physical size: the blue
        // sedan matches the game's 1.8 x 4.4 m car box, the trucks keep
        // their photo proportions (CarSizes). Nose = texture top; with
        // rotation_degrees = heading the nose points along travel.
        root.AddChild(new Sprite2D
        {
            Texture = tex,
            Scale = new Vector2(size.W / tex.GetSize().X, size.L / tex.GetSize().Y),
            Position = new Vector2(0, -RearAxleOffsetM),
        });
        // Blinker corner lights: one at each body corner,
        // +/-0.85*L/2 fore-aft and +/-0.75*W/2 lateral around the body
        // centre; orange, 0.5 s blink period.
        // Corners follow the SPRITE's physical size, not the sedan box,
        // so lights sit at the body corners of trucks too.
        float fore = size.L / 2f * 0.85f;
        float lat = size.W / 2f * 0.75f;
        var blinkerL = new Polygon2D[2];
        var blinkerR = new Polygon2D[2];
        var blinkerColor = new Color(1f, 0.706f, 0f);   // (255,180,0)
        for (int i = 0; i < 2; i++)
        {
            float y = -fore * (i == 0 ? 1f : -1f) - RearAxleOffsetM;
            blinkerL[i] = MakeLight(new Vector2(-lat, y), blinkerColor);
            blinkerR[i] = MakeLight(new Vector2(lat, y), blinkerColor);
        }
        root.AddChild(blinkerL[0]); root.AddChild(blinkerL[1]);
        root.AddChild(blinkerR[0]); root.AddChild(blinkerR[1]);
        _blinkL[uid] = blinkerL;
        _blinkR[uid] = blinkerR;

        // Front-corner headlights: white, same fore-aft position as the
        // front blinkers, placed further outboard (0.95*W/2) so both are
        // visible as distinct dots.
        float frontY = -fore - RearAxleOffsetM;
        float latOutboard = size.W / 2f * 0.95f;
        var headlightColor = new Color(1f, 1f, 0.9f);
        var headlights = new[]
        {
            MakeLight(new Vector2(-latOutboard, frontY), headlightColor),
            MakeLight(new Vector2(latOutboard, frontY), headlightColor),
        };
        root.AddChild(headlights[0]); root.AddChild(headlights[1]);
        _headlights[uid] = headlights;

        // Rear-corner tail/brake lights: same outboard lateral position as
        // the headlights, at the rear. Colour (dark red = tail-light-on,
        // bright red = braking) is set dynamically per frame, not here.
        float rearY = fore - RearAxleOffsetM;
        var brakeLights = new[]
        {
            MakeLight(new Vector2(-latOutboard, rearY), new Color(0f, 0f, 0f)),
            MakeLight(new Vector2(latOutboard, rearY), new Color(0f, 0f, 0f)),
        };
        root.AddChild(brakeLights[0]); root.AddChild(brakeLights[1]);
        _brakeLights[uid] = brakeLights;

        _world.AddChild(root);
        return root;
    }

    static Polygon2D MakeLight(Vector2 pos, Color color)
    {
        // Unit-radius 8-gon CENTRED AT THE NODE'S OWN ORIGIN (not baked into
        // the polygon's vertices): `pos` is the node's Position instead.
        // Per-frame zoom compensation sets this node's Scale (see
        // UpdateCars) to keep the dot's SIZE constant on screen - Godot
        // scales a node's rendering relative to its OWN Position, never its
        // parent's origin. Baking `pos` into the vertices (the original
        // bug) meant that same Scale ALSO shrank the offset toward the
        // car's rear axle: at the ~0.1 zoom-compensation factor typically
        // in play, a light meant to sit ~3 m from the body centre rendered
        // ~0.3 m out - every corner light (blinkers included) has clustered
        // near the middle of the car since this code was first written.
        var pts = new Vector2[8];
        for (int i = 0; i < 8; i++)
        {
            float a = Mathf.Tau * i / 8f;
            pts[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }
        return new Polygon2D
        {
            Position = pos,
            Color = color,
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
        double frameDt = Math.Max(0, nowWall - _lastFrameWall);
        if (_simNow > double.NegativeInfinity)
        {
            // Rate bounds scale with the announced warp; an unbounded run
            // (--fast) is measured like any other stream.
            double warpCap = Math.Max(1.0, _timeWarp);
            if (nowWall - _rateRefWall >= 1.0 && _rateRefSim > 0)
            {
                double r = (_lastMaxTime - _rateRefSim) / (nowWall - _rateRefWall);
                if (r > 0.05 && r < 2.0 * warpCap) _simRate = r;   // else keep old
                _rateRefSim = _lastMaxTime;
                _rateRefWall = nowWall;
            }
            _simNow += frameDt * Mathf.Clamp(_simRate, 0.0, 1.5 * warpCap);
            // Re-sync the clock to the data (measured 2026-09-06): a
            // one-off offset - e.g. the sim stutters while spawning 150
            // cars and the free-running clock races ~400 ms ahead - used
            // to stick FOREVER, because both sides then run at the same
            // rate. Stuck forward, rt sits past the newest sample: cars
            // degrade to hold-and-jump and FollowCamera falls into its
            // raw-packet branch (no interpolation) -> the whole track
            // snaps on every /state response (visible jitter).
            // Bounded exponential pull (time constant ~0.3 s, max 3x wall
            // per frame): converges any stuck offset invisibly; during a
            // data stall it settles ~0.3 s ahead (cars hold, no snap)
            // and re-converges when the stream resumes.
            if (_lastMaxTime > double.NegativeInfinity)
            {
                double desired = _lastMaxTime + RenderDelaySec;
                float err = (float)(desired - _simNow);
                float maxStep = (float)(frameDt * 3.0 * warpCap);
                _simNow += Mathf.Clamp(err * Math.Min(1.0f, frameDt * 3.0),
                                       -maxStep, maxStep);
            }
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

            // bitteschön: the conceder signals TOWARD the car it lets through.
            if (_carMeta.TryGetValue(kv.Key, out var metaC))
                DrawCourtesy(kv.Key, node, metaC);

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
                // Headlights: white, visible while HeadlightsOn.
                if (_headlights.TryGetValue(kv.Key, out var hl))
                    foreach (var b in hl)
                        { b.Visible = meta.HeadlightsOn; b.Scale = sc; }
                // Tail/brake lights: bright red while braking (regardless
                // of TaillightsOn - a real brake light works with the
                // lights off), else dark red while TaillightsOn, else
                // invisible.
                if (_brakeLights.TryGetValue(kv.Key, out var bk))
                    foreach (var b in bk)
                    {
                        b.Visible = meta.Braking || meta.TaillightsOn;
                        b.Color = meta.Braking ? new Color(1f, 0.15f, 0.15f)
                                                : new Color(0.4f, 0.05f, 0.05f);
                        b.Scale = sc;
                    }

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
                    float noseLenM = CarSizeOf(meta.Color).L;
                    lab.Position = pos + new Vector2(
                        -lab.Size.X * 0.1f * 0.5f,
                        -(RearAxleOffsetM + noseLenM / 2f + 0.6f));
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
                    ? m.Color : "blue";
                _minimap.Cars.Add((new Vector2(kv.Value.GlobalPosition.X,
                                               -kv.Value.GlobalPosition.Y),
                                   kv.Key == _followUid,
                                   CarColors[col]));
            }
            _minimap.QueueRedraw();
        }

        // Test number under the speed readout.
        if (_testLabel != null && _lastTestText != null)
        {
            bool show = _lastTestText.Length > 0;
            _testLabel.Visible = show;
            if (show && _lastTestText != _testLabel.Text)
                _testLabel.Text = _lastTestText;
        }

        // Speed readout under the minimap: bound car (or first car). Hidden in
        // review mode - it describes the live sim, not the tape under the cursor.
        if (_speedLabel != null && !_review)
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
                }
            }
        }

        // Car info window (click selection): id, type, live speed, driver
        // class - static under the minimap, same background as the minimap.
        if (_infoPanel != null)
        {
            bool show = _selectedUid.HasValue &&
                        _carNodes.ContainsKey(_selectedUid.Value);
            if (show)
            {
                long uid = _selectedUid.Value;
                // Fetch on selection change, then refresh once a second:
                // a stuck car's log is static, a busy one keeps appending.
                ulong nowMs = Time.GetTicksMsec();
                if (!_decBusy &&
                    (uid != _decUidFor || nowMs - _decLastMs >= 1000))
                {
                    _decBusy = true;
                    _decLastMs = nowMs;
                    if (uid != _decUidFor) _decLines = new() { "(loading...)" };
                    _decHttp.Request(
                        $"http://127.0.0.1:{_embeddedPort}/car/{uid}/decisions");
                }
                var mSel = _carMeta.GetValueOrDefault(uid);
                string color = mSel?.Color ?? "blue";
                string cls = DrivingGame.Sim.Config.VEHICLE_CLASS
                    .GetValueOrDefault(color, "car");
                float kmh = 0f;
                if (_carBuf.TryGetValue(uid, out var bufSel) && bufSel.Count > 0)
                    kmh = bufSel[^1].SpeedKmh;
                // The speed row lives in its own Label (per-frame churn is
                // fine there). The TextEdit is only reset on a STRUCTURAL
                // change (new decision line, new car) - a full reset drops
                // the scroll position, so it must not happen more often.
                _infoSpeed.Text = $"speed   {kmh:F1} km/h";
                var sb = new System.Text.StringBuilder();
                sb.Append($"#{uid}   {color} ({cls})\n");
                sb.Append($"driver  {mSel?.Driver ?? "?"}\n");
                sb.Append("decisions (newest first):\n");
                foreach (var line in _decLines)
                    sb.Append(line).Append('\n');
                string decKey = $"{uid}|{sb}";
                if (decKey != _lastInfoDecKey)
                {
                    _lastInfoDecKey = decKey;
                    _infoText.Text = sb.ToString();
                }
            }
            _infoPanel.Visible = show;
        }
        LayoutUnderMinimap();
        LayoutInfoFooter();
    }

    // Height of the car info FOOTER (full-width bar at the bottom of the
    // window): 4 header lines + up to 5 decision lines. Full width means
    // the decision lines never wrap, so this fits them all.
    const int FooterHeight = 240;

    /// <summary>Click selection: hit-test every car's body box (in the car's
    /// local frame, node origin = rear axle so the body centre sits
    /// RearAxleOffsetM ahead) against the world point under the cursor; the
    /// nearest hit wins. A click on empty space clears the selection.</summary>
    void SelectCarAt(Vector2 screenPos)
    {
        var vp = GetViewport().GetVisibleRect().Size;
        Vector2 world = _cam.GetGlobalPosition() + (screenPos - vp / 2f) / _cam.Zoom;
        long? best = null;
        double bestD = double.PositiveInfinity;
        foreach (var kv in _carNodes)
        {
            var node = kv.Value;
            _carMeta.TryGetValue(kv.Key, out var m);
            // Hit area == the sim's crash-detection footprint
            // (CollLenM/CollWidM from body_corners), falling back to the
            // sprite size before the first sample.
            var size = CarSizeOf(m?.Color ?? "blue");
            float halfW = (m != null && m.CollWidM > 0 ? m.CollWidM : size.W) / 2f;
            float halfL = (m != null && m.CollLenM > 0 ? m.CollLenM : size.L) / 2f;
            Vector2 bodyCentre = node.GlobalPosition +
                                 new Vector2(0, -RearAxleOffsetM);
            // Rotate the world->body vector into the car's local frame
            // (x = lateral, y = fore-aft along travel).
            float c = Mathf.Cos(-node.Rotation), s = Mathf.Sin(-node.Rotation);
            Vector2 dv = world - bodyCentre;
            Vector2 rel = new Vector2(dv.X * c - dv.Y * s, dv.X * s + dv.Y * c);
            // Hit area == the red outline (no extra tolerance).
            if (Math.Abs(rel.X) > halfW || Math.Abs(rel.Y) > halfL)
                continue;
            double d = rel.LengthSquared();
            if (d < bestD) { bestD = d; best = kv.Key; }
        }
        _selectedUid = best;
    }

    /// <summary>Stack the overlays under the minimap, top to bottom: speed
    /// readout, test label. (The car info panel is a full-width FOOTER at
    /// the window bottom - see LayoutInfoFooter - not part of this stack.)
    /// </summary>
    /// <summary>Keep the map window in the corner it was built for. Only on a
    /// size change: a window the operator dragged sideways keeps its spot for
    /// as long as the window is the same size.</summary>
    /// <summary>Register an owned window so the guard below knows it exists;
    /// the reviewer's slot windows call this too (same class, other file).</summary>
    public void RegisterWindow(Window w)
    {
        if (!_windows.Contains(w)) _windows.Add(w);
    }

    /// <summary>A window that is torn down (a HUD rebuild re-lays the readout
    /// out) leaves the registry, or the guard would push a freed node around.
    /// </summary>
    public void UnregisterWindow(Window w) => _windows.Remove(w);

    /// <summary>Where a window's CLIENT area may sit: the main window minus
    /// the belt and Godot's own chrome, so the whole window is inside, never
    /// even partly out (binding user decision 2026-10-22).</summary>
    private Vector2I ClampWindowPos(Window w, Vector2 want)
    {
        var main = GetTree().Root.GetVisibleRect().Size;
        var loX = (float)(WinGapPx + WinFrameSidePx);
        var hiX = main.X - WinGapPx - WinFrameSidePx - w.Size.X;
        var loY = (float)(WinGapPx + WinTitleBarPx);
        var hiY = main.Y - WinGapPx - WinFrameBottomPx - w.Size.Y;
        return new Vector2I(
            (int)Mathf.Clamp(want.X, loX, Mathf.Max(loX, hiX)),
            (int)Mathf.Clamp(want.Y, loY, Mathf.Max(loY, hiY)));
    }

    /// <summary>Godot lets a hand drag an embedded window anywhere. Every
    /// window this client owns is put back where it belongs: inside the main
    /// window with its chrome, and WinGapPx from each other (binding user
    /// decision 2026-10-22). A handful of windows, few passes, and the
    /// assignment only happens when something actually moved - a window the
    /// hand left alone never jitters.</summary>
    private void EnforceWindows()
    {
        var settled = false;
        for (var pass = 0; pass < 60 && !settled; pass++)
        {
            var moved = false;
            foreach (var w in _windows)
            {
                if (!w.IsInsideTree()) continue;   // torn down, not ours any more
                var cl = ClampWindowPos(w, new Vector2(w.Position.X, w.Position.Y));
                if (cl.X != w.Position.X || cl.Y != w.Position.Y)
                {
                    w.Position = cl;
                    moved = true;
                }
            }
            for (var i = 0; i < _windows.Count; i++)
                for (var j = i + 1; j < _windows.Count; j++)
                    if (_windows[i].IsInsideTree() && _windows[j].IsInsideTree() &&
                        SeparateWindows(_windows[i], _windows[j])) moved = true;
            settled = !moved;
        }
        if (settled) return;
        // Not settled in 60 passes: the windows asked for something the main
        // window cannot hold side by side. Inside-ness is the hard half of
        // the decision, so it gets the last word - a final clamp, no further
        // separation - and the shortfall is named rather than hidden.
        foreach (var w in _windows)
        {
            var cl = ClampWindowPos(w, new Vector2(w.Position.X, w.Position.Y));
            if (cl.X != w.Position.X || cl.Y != w.Position.Y) w.Position = cl;
        }
        if (_clampDbg < 2)
        {
            _clampDbg++;
            var need = 0.0;
            foreach (var w in _windows) need += w.Size.Y + WinTitleBarPx + WinFrameBottomPx + WinGapPx;
            GD.Print($"WINDOWS-TIGHT {need:F0} px of windows in {GetTree().Root.GetVisibleRect().Size.Y:F0} px " +
                     $"of main window · every window is inside, the {WinGapPx} px belt cannot hold " +
                     $"{_windows.Count} of them at once");
        }
    }

    /// <summary>Push B out of A's belt along the axis it crossed least, so a
    /// drag never lets two windows - or their title bars - overlap. The next
    /// pass clamps the result back inside, which is what makes a window
    /// pinned to an edge give way instead of hiding under the main window's
    /// frame.</summary>
    /// <summary>A window's CLIENT rect plus Godot's own chrome - what has to
    /// stay inside the main window and 5 px clear of every other window.</summary>
    private Rect2 OuterRect(Window w) => new Rect2(
        new Vector2(w.Position.X, w.Position.Y) - new Vector2(WinFrameSidePx, WinTitleBarPx),
        new Vector2(w.Size.X + 2f * WinFrameSidePx,
                    w.Size.Y + WinTitleBarPx + WinFrameBottomPx));

    private bool SeparateWindows(Window a, Window b)
    {
        var aR = OuterRect(a);
        var bR = OuterRect(b);
        var px = (Mathf.Min(aR.End.X, bR.End.X) - Mathf.Max(aR.Position.X, bR.Position.X)) + WinGapPx;
        var py = (Mathf.Min(aR.End.Y, bR.End.Y) - Mathf.Max(aR.Position.Y, bR.Position.Y)) + WinGapPx;
        if (px <= 0f || py <= 0f) return false;
        var p = new Vector2(b.Position.X, b.Position.Y);
        if (px < py)
            p.X += bR.Position.X < aR.Position.X ? -px : px;
        else
            p.Y += bR.Position.Y < aR.Position.Y ? -py : py;
        b.Position = new Vector2I((int)p.X, (int)p.Y);
        return true;
    }

    void PinMapWindow()
    {
        if (_mmWin == null || _minimap == null) return;
        var sz = GetViewport().GetVisibleRect().Size;
        if (sz == _mmPinVp) return;
        _mmPinVp = sz;
        _mmWin.Position = ClampWindowPos(_mmWin,
                                         new Vector2(sz.X - _minimap.Size.X, _mmWin.Position.Y));
    }

    /// <summary>Stack the readouts UNDER the map window, in the main window:
    /// the window is the inset and nothing else (user decision 2026-10-22 -
    /// the position/zoom line is gone and the content has to fit the window),
    /// so the labels are no longer children of it and their coordinates are
    /// main-window pixels again.</summary>
    void LayoutUnderMinimap()
    {
        if (_minimap == null) return;
        var box = _minimap.Size;
        var x = (float)(_mmWin != null ? _mmWin.Position.X : 0);
        var y = (float)(_mmWin != null ? _mmWin.Position.Y : 0) + box.Y + WinGapPx;
        foreach (var lab in new[] { _flowLabel, _speedLabel, _testLabel })
        {
            if (lab == null || !lab.Visible) continue;
            lab.Position = new Vector2(x, y);
            y += lab.Size.Y + 6f;
        }
    }

    /// <summary>Position the car info panel as a full-width FOOTER at the
    /// bottom of the window (only visible while a car is selected). Wider
    /// than the old minimap-side box: the decision log reads on one line
    /// each instead of wrapping in a 360 px column.
    /// </summary>
    void LayoutInfoFooter()
    {
        if (_infoPanel == null || !_infoPanel.Visible) return;
        var vp = GetViewport().GetVisibleRect();
        var m = (float)WinGapPx;
        _infoPanel.Position = new Vector2(m, vp.Size.Y - FooterHeight - m);
        _infoPanel.Size = new Vector2(vp.Size.X - 2 * m, FooterHeight);
    }

    /// <summary>Follow mode: the Camera2D tracks the node of the bound car.
    /// The engine's position smoothing gives the follow lag; a rebind (Tab)
    /// or a teleported car resets that smoothing instead of gliding across
    /// the map. F releases the bound car for good - the view then stays
    /// wherever the user panned it.</summary>
    void FollowCamera()
    {
        // Review mode owns its camera: the view belongs to the TAPE frame under
        // the cursor (or to an explicit --center), never to whichever car the
        // live sim happens to have bound.
        if (_review) return;
        // --focus <uid>: the view belongs to the requested car (the runner
        // passes the last crashed uid), so it outranks the auto-crash latch.
        if (_focusUid != 0L && _carNodes.ContainsKey(_focusUid))
            _followUid = _focusUid;
        // Crash camera: once a car has crashed, the view belongs to the
        // impact, not to the run's --center or the newest spawn.
        if ((_crashFocusUid != 0L && _carNodes.ContainsKey(_crashFocusUid)) ||
            (_focusUid != 0L && _carNodes.ContainsKey(_focusUid)))
        {
            long bind = _focusUid != 0L && _carNodes.ContainsKey(_focusUid)
                ? _focusUid : _crashFocusUid;
            _followUid = bind;
            if (!_crashZoomed)
            {
                _crashZoomed = true;
                _cam.Zoom = new Vector2(CrashZoom, CrashZoom);
                GD.Print($"Crash camera: pinned on car {bind} @ {CrashZoom:F0} px/m");
                if (_crashShotPath != null && _crashShotPath != "")
                {
                    GetTree().CreateTimer(0.5).Timeout += () => SaveShot(_crashShotPath);
                    GD.Print($"crash shot scheduled -> {_crashShotPath}");
                }
            }
        }
        if (_followUid == null && _autoFollow && _carNodes.Count > 0)
            _followUid = _carNodes.Keys.Min();
        if (_followUid == null) return;   // unfollowed: manual view only
        if (!_carNodes.TryGetValue(_followUid.Value, out var node))
        { _followUid = null; return; }   // car gone
        // Manual control (drag, pan keys, wheel) wins while it is held.
        if (Time.GetTicksMsec() - _lastManualMs < 500) return;
        // The car node already sits at its interpolated render position, so
        // the camera just points at it.
        var target = node.GlobalPosition;
        // A jump bigger than a real car could drive in one frame (a rebind,
        // a teleport): no glide across the map - reset the smoothing.
        if ((target - _lastFollowTarget).Length() > 10f)
            _cam.ResetSmoothing();
        _cam.GlobalPosition = target;
        _lastFollowTarget = target;

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

    // --- Breadcrumb trails + test flags ---

    void UpdateOverlays()
    {
        // Revert the "copied!" flash on the copy button.
        if (_infoCopyBtn != null && _infoCopyBtn.Text == "copied!" &&
            Time.GetTicksMsec() >= _copyFlashUntil)
            _infoCopyBtn.Text = "copy";
        // Stale-window self-quit: the runner bumped the generation file
        // (this window belongs to a finished/previous run) -> quit clean.
        ulong nowMsGen = Time.GetTicksMsec();
        if (nowMsGen - _godotGenLastMs >= 1000)
        {
            _godotGenLastMs = nowMsGen;
            string gen = ReadGodotGen();
            if (gen != _godotGen)
            {
                Console.WriteLine("[godot] generation changed - quitting (clean close)");
                GetTree().Quit();
                return;
            }
        }
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
            line.Width = TrailWidthM;   // a tyre track is paint, not a screen px
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
            Width = TrailWidthM,
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
        // A click always means "interrupt whatever is running and start
        // this test": the server kills the old test process (interrupt)
        // before it spawns the new one. Stop the old polling chain so its
        // responses cannot clobber the new run.
        _pollActive = false;
        _runTestBtn.Disabled = true;
        SetRunStatus($"Starte Test #{n} (laufender Test wird unterbrochen) …",
            ok: true);
        if (_cmdHttpBusy)
        {
            // A request (poll or older start) is in flight - _cmdHttp is
            // single-flight. Send the new start as soon as that response
            // lands (OnCmdResponse checks _clickPendingNum first).
            _clickPendingNum = n;
            return;
        }
        SendRunTest(n);
    }

    void SendRunTest(int n)
    {
        _cmdHttpBusy = true;
        _pendingCmd = "post";
        var body = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"number\": {n}, \"interrupt\": true}}");
        // RequestRaw: raw byte body + custom headers (Godot 4.7).
        _cmdHttp.RequestRaw(RunTestUrl,
            new[] { "Content-Type: application/json" },
            HttpClient.Method.Post, body);
    }

    void OnCmdResponse(long code, byte[] body)
    {
        _cmdHttpBusy = false;
        // A Run Test click that arrived while a request was in flight goes
        // out now. The response it supersedes is dropped on purpose: a poll
        // is periodic, an older start is replaced by the newer click.
        if (_clickPendingNum > 0)
        {
            int clickNum = _clickPendingNum;
            _clickPendingNum = 0;
            SendRunTest(clickNum);
            return;
        }
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
            ScheduleNextPoll();
        }
        else if (!haveNumber)
        {
            if (_quitOnTestDone)
            {
                // Run not started yet - keep watching.
                ScheduleNextPoll();
            }
            else
            {
                // No run has ever been recorded - nothing to track.
                _pollActive = false;
            }
        }
        else if (!running && haveNumber)
        {
            _pollActive = false;
            SetRunStatus(rc == 0
                ? $"Test #{n} fertig ✅ (Log: {logFile})"
                : $"Test #{n} beendet, rc={rc} ❌ (Log: {logFile})",
                ok: rc == 0);
            if (_quitOnTestDone)
            {
                // Let the status label render for a moment, then quit
                // cleanly - no external kill needed (see field comment).
                GetTree().CreateTimer(2.0).Timeout += () => GetTree().Quit();
            }
        }
    }

    void StartPolling()
    {
        if (_pollActive) return;
        _pollActive = true;
        _pollGen++;
        PollRunStatus();
    }

    // Gen-captured: a CreateTimer that outlived its polling chain (a Run
    // Test click in between) must not resurrect a second chain.
    void ScheduleNextPoll()
    {
        int gen = _pollGen;
        GetTree().CreateTimer(3.0).Timeout += () =>
        {
            if (gen == _pollGen && _pollActive) PollRunStatus();
        };
    }

    void PollRunStatus()
    {
        if (!_pollActive || _cmdHttpBusy) return;
        _cmdHttpBusy = true;
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
/// Top-right overview map: a 180 px box
/// box, dark-green background, gray road network, red car dot (M2), yellow
/// viewport rectangle. Lives on a CanvasLayer so it ignores the camera.
/// All input is in SIM coords (x east, y north); the y-flip happens here.
/// </summary>
/// <summary>The map inset. A CONTROL, not a free-floating Node2D: it draws
/// into its OWN rect, so whatever hosts it - the map window of either client -
/// gets an inset that fills the frame. As a Node2D it placed a fixed 360 px
/// box at (vp.X - BoxSize - Margin, Margin) of the viewport it rendered in,
/// which inside a window of its own left empty frame around the map.</summary>
public partial class MinimapNode : Control
{
    public const int BoxSize = 360;  // the LONG side of the inset
    // No band between the inset and the window: the inset fills the window's
    // client area, so the only thing inside the frame that is not map is the
    // caption row under it (binding user decision 2026-10-22: "the minimap
    // shall fill the minimap window to the edges" - measured before the
    // change: a 1600 px window, inset 358 x 282 of MINIMAP_BG with 17 px of
    // see-through band on every side inside the window's own frame).
    public const int Margin = 0;
    public static readonly Color Bg = new(20 / 255f, 60 / 255f, 20 / 255f);       // MINIMAP_BG
    public static readonly Color Border = new(80 / 255f, 80 / 255f, 80 / 255f);  // MINIMAP_BORDER
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
        // The inset is now its OWN surface: the hand works on it (it pins and
        // sizes the main view's box), so the events must not fall through to
        // whatever is behind the window.
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>The host's camera hooks (the host owns the camera; this node
    /// owns the inset's geometry and the hand): the world point the hand
    /// named, and the wheel's direction (+1 = the box grows bigger view… no:
    /// +1 = wheel up = zoom IN = a SMALLER view).
    /// Wired by MapRenderer right after Setup().</summary>
    public System.Action<Vector2> PinView;
    public System.Action<Vector2, int> ZoomView;

    private bool _handDown;    // the left button held inside the inset
    private int _evDbg;         // the first events that arrived here, logged

    /// <summary>Godot gives events inside an embedded window to the window's
    /// OWN nodes, in the window's local pixels - which is exactly what this
    /// node needs, since the inset IS its rect (Margin 0).</summary>
    public override void _GuiInput(InputEvent e)
    {
        if (_evDbg < 8)
        {
            _evDbg++;
            string kind = "other";
            var lp = Vector2.Zero;
            if (e is InputEventMouseButton kmb) { kind = kmb.ButtonIndex.ToString(); lp = kmb.Position; }
            else if (e is InputEventMouseMotion kmm) { kind = "motion"; lp = kmm.Position; }
            GD.Print($"MINIMAP-EV {kind} local=({lp.X:F0},{lp.Y:F0}) " +
                     $"inset={Size.X:F0}x{Size.Y:F0} held={_handDown}");
        }
        if (e is InputEventMouseButton m && m.ButtonIndex == MouseButton.Left)
        {
            if (m.Pressed)
            {
                _handDown = true;
                PinView?.Invoke(GdWorld(m.Position));
            }
            else
                _handDown = false;
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (!_handDown) return;
            if (mm.Position.X < 0f || mm.Position.Y < 0f ||
                mm.Position.X > Size.X || mm.Position.Y > Size.Y)
            {
                // The hand walked off the inset: the gesture ENDS, it must
                // not keep dragging the view from outside (measured before:
                // motion at local (182,334) - 50 px below the inset's 284 -
                // still moved the view, so a drag that left the inset kept
                // steering the map).
                _handDown = false;
                return;
            }
            PinView?.Invoke(GdWorld(mm.Position));
        }
        else if (e is InputEventMouseButton w && w.Pressed &&
                 (w.ButtonIndex == MouseButton.WheelUp || w.ButtonIndex == MouseButton.WheelDown))
            ZoomView?.Invoke(GdWorld(w.Position),
                             w.ButtonIndex == MouseButton.WheelUp ? 1 : -1);
    }

    /// <summary>The world point (Godot world, y DOWN) one of the inset's own
    /// pixels names: this node's Mm() run backwards at the inset's scale - the
    /// same scale the yellow view box is drawn at, so the hand and the box
    /// agree by construction.</summary>
    private Vector2 GdWorld(Vector2 p)
    {
        var sx = Size.X / _bounds.Size.X;
        var sy = Size.Y / _bounds.Size.Y;
        var wx = _bounds.Position.X + p.X / sx;                      // sim metres
        var wy = _bounds.Position.Y + (Size.Y - p.Y) / sy;            // y north
        return new Vector2(wx, -wy);
    }

    public override void _Draw()
    {
        if (_segs == null || _cam == null) return;
        // The control's rect IS the inset - the host decides the shape (it
        // comes from the map's aspect, see MmBoxFor), so the network fills
        // the frame it was given.
        var box = new Rect2(Vector2.Zero, Size);
        if (box.Size.X < 4f || box.Size.Y < 4f) return;
        DrawRect(box, Bg);

        float sx = box.Size.X / _bounds.Size.X;
        float sy = box.Size.Y / _bounds.Size.Y;
        Vector2 Mm(Vector2 p) => new(
            (p.X - _bounds.Position.X) * sx,
            box.Size.Y - (p.Y - _bounds.Position.Y) * sy);

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
        //
        // The view it describes is the MAIN window's, not this inset's own
        // viewport: inside an embedded subwindow GetViewport() answers with
        // the subwindow (~390 px instead of ~2900), so the box came out ~7x
        // too small and always fell through to the crosshair below.
        var vp = GetTree().Root.GetVisibleRect().Size;
        float z = _cam.Zoom.X;
        var c = _cam.GlobalPosition;                 // Godot world (y down)
        Vector2 camSim = new(c.X, -c.Y);             // sim coords
        float wpx = vp.X / z * sx, hpx = vp.Y / z * sy;
        float left = (camSim.X - vp.X / 2f / z - _bounds.Position.X) * sx;
        float top = box.Size.Y - (camSim.Y + vp.Y / 2f / z - _bounds.Position.Y) * sy;
        var vpr = new Rect2(left, top, wpx, hpx).Intersection(box);
        // A view much smaller than the map (the reviewer at zoom 14 sees
        // 162 m of 2470 m) made the box a ~10 px mark at the inset's edge -
        // it read as a clipped ring. Below a legible size the box is dropped
        // and a crosshair names the spot instead: an indicator, not a shape
        // that pretends to be the view.
        if (wpx < 18.0f || hpx < 18.0f)
        {
            var mid = new Vector2(left + wpx / 2.0f, top + hpx / 2.0f);
            DrawLine(new Vector2(mid.X - 7.0f, mid.Y), new Vector2(mid.X + 7.0f, mid.Y),
                new Color(1f, 1f, 0f), 1.5f);
            DrawLine(new Vector2(mid.X, mid.Y - 7.0f), new Vector2(mid.X, mid.Y + 7.0f),
                new Color(1f, 1f, 0f), 1.5f);
        }
        else if (vpr.Size != Vector2.Zero)
            DrawRect(vpr, new Color(1f, 1f, 0f), false, 2f);

        // Flush to the window: a 2 px outline ON the inset's edge would have
        // half of itself outside the client area, so the line is drawn just
        // inside it and keeps all 2 px on screen.
        DrawRect(new Rect2(box.Position + new Vector2(1f, 1f),
                           box.Size - new Vector2(2f, 2f)), Border, false, 2f);
    }

}
