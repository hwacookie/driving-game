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
    private int _apiPort = 5001;         // resolved in _Ready: embedded port or the URL's own
    private SimHost _simHost;          // non-null in embedded mode
    // --source / --map as GIVEN on the command line. A review window fills in
    // both for you from the tape's own header, but an explicit flag outranks
    // the tape (issue #2).
    private bool _sourceGiven, _mapGiven;
    // The ground could not be built at all: the window says NO GROUND and
    // paints no cars - a tape drawn on nothing is a lie about where those cars
    // drove (docs/SIMULATIONS_REVIEWER.md, issue #2).
    private bool _noGround;

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
    private OptionButton _testPick;          // the scenario list, number first
    private readonly List<int> _testPickNums = new();
    private HttpRequest _testsHttp;          // GET /tests, on its own socket:
    private int _testsTries;                 // _cmdHttp is single-flight
    private int _lastRunNum;                 // the run the status line talks about
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
    // Where the window OPENS (docs/TESTING.md "Window placement", the Godot
    // recipe "Remembering window position and size across sessions"): the
    // geometry it had at the last close, kept in user://window.ini. Applied in
    // _Ready BEFORE the first frame is drawn, so there is no visible
    // open-small-then-grow-then-move step any more (that dance was the user's
    // complaint of 2026-10-25: "kein Resize, sondern direkt").
    private const string WinCfgPath = "user://window.ini";
    private Rect2I _windowAsk;             // --window <x> <y> <w> <h>: explicit, wins over the memory
    private bool _windowNoRestore;         // --window-default: ignore the stored geometry once
    private double _winSaveAcc;            // the "save regularly" timer of the docs' note
    private Rect2I _winSavedRect;           // the rect already in window.ini (write only on a change)
    private int _winSaveDbg;
    private string _winPlacedBy = "default";
    private double _testSeek = -1.0;   // --test-seek <frac>: click the scrub bar
    private Vector2 _testMmPan;        // --test-minimap-pan <fx> <fy>: drive the view box
    private double _testScrubDrag = -1.0;  // --test-scrub-drag <frac>: drag in the bar
    // Dev hook: --motionlog <file> writes "ms x y" per car per frame so
    // rendering smoothness can be measured (delta variance = stutter).
    private string _motionLogPath;
    private int _autoRunTest = -1;   // --run-test <N>: auto-click Run Test
    // Dev hooks of the same family as --test-pan / --test-bar-ui: the two
    // car-detail paths a hand would take, fired from the script, so "does a
    // click on a car open the menu in the LIVE viewer, and does the live frame
    // feed an open slot?" is answerable without a finger on the mouse.
    private int _testCarHook;           // 1 = the menu, 2 = open a slot
    private StreamWriter _motionLog;
    private bool _testPan;
    private bool _testJog;           // --test-jog: hold '.' and watch the jog, then let go
    private bool _testPlay;          // --test-play: press ▶, let the motion log time it
    /// <summary>A/B switch for the 2026-10-25 stutter measurement:
    /// --no-play-lerp paints the raw recorded substep while still advancing
    /// the clock, i.e. exactly what ▶ play did before the continuous cursor.
    /// tests/pose_stutter_check.py --ab traces both on the SAME tape, so the
    /// comparison is one switch and not two runs of two different worlds.</summary>
    private bool _noPlayLerp;
    private bool _testSlotButtons;   // --test-slot-buttons: drive a car detail window
    private bool _testPanProbe;      // --test-pan-probe: log a scripted pan, twice
    private bool _testBarUi;         // --test-bar-ui: wheel-zoom + marker tooltip/click
    private bool _testFocusProbe;    // --test-focus-probe: review auto-focus + follow trace
    private bool _dumpAllControls;   // probe-internal: list every Control once
    private bool _testInfoWindowOut;     // --test-window-out: park a window illegally, let the guard answer
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
    /// <summary>The stamp of the ground THIS window actually drew - the same
    /// `RoadNetwork.MapFingerprint` the tape header carries (`GameApi.cs:475`
    /// stamps a run, `MapPayload.cs:204` answers it beside the geometry).
    /// A tape NAMES its map rather than carrying road polygons (the user's
    /// decision of 2026-10-25: the ground is static per run, so a reference
    /// beats a few hundred KB per file) - and a name is only honest if it is
    /// CHECKED. Paint the tape's cars on a foreign ground otherwise and the
    /// review is a lie about where they drove, so a v2-vs-v2 mismatch
    /// REFUSES the picture (`ReviewFeed` returns, the HUD says why).
    /// A stamp without the `v2:` prefix predates the wide fingerprint - the
    /// old hash covered segment endpoints/width/oneway and a sign COUNT only,
    /// so `level`, a sign's type and a sign's POSITION were invisible to it,
    /// AND its spaces were truncated by the header's own parse. Those tapes
    /// load with a tag, not a refusal: unknown is not wrong.</summary>
    private string _drawnMapHash = "";
    private bool _mapMismatch;
    private bool _ignoreMapHash;    // --ignore-map-hash: show it anyway
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
    /// <summary>Who owns the render clock for this frame. The live stream has
    /// to be ESTIMATED (samples land at network rate, so a free-running clock
    /// plus a bounded pull is what makes it smooth); a source that STATES a
    /// time — the tape cursor — IS that time, and running an estimate on top
    /// of it puts two clocks in one frame, disagreeing by a frame's worth of
    /// drift. Measured as frames painted a substep ahead and then back:
    /// tests/pose_stutter_check.py.</summary>
    private bool _sourceOwnsClock;
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

    // --- The scenario's declared camera focus (docs/TESTING.md "Camera focus") --
    // A test says what it is ABOUT; the window re-aims when that changes.
    // kind "car"   → the primary car, view fitted (CarFocusViewM across)
    // kind "named" → that named car of the cast, at the declared zoom
    // kind "loc"   → a PLACE: no car is followed at all (crossing / pass tests,
    //                where following one car out of 50 loses the subject)
    // A focus the OPERATOR gave the client by hand (--center, --zoom, F, a drag)
    // beats the run's: `_focusManual` latches on the first such input, so an
    // announced focus cannot undo a deliberate choice mid-run.
    private string _tFocusLabel = "", _tFocusKind = "car", _tFocusName = "", _tFocusDesc = "";
    /// <summary>The test-info WINDOW (docs/TESTING.md §5, the user's decision of
    /// 2026-10-25): a watching human must know WHICH test runs, WHY, and whether
    /// its goal was reached. `_testVerdict` holds the filed verdict of the
    /// CURRENT test (matched on the focus LABEL inside `test_results`, so a
    /// verdict of another scenario can never be shown as this one's state);
    /// empty means still running.</summary>
    private string _testVerdict = "";
    private int _testDbg, _testDbg2;
    private Window _testInfoWin;
    private ColorRect _testBg, _testChip;
    private Label _testPurpose, _testWatch, _testState, _testNums, _testChipIcon;
    // The car the scenario STAGED under that role name (focus "name_uid"). The
    // name alone is not a key: roles are reused from test to test.
    private long _tFocusNameUid;
    private double _tFocusX, _tFocusY, _tFocusZoom;
    private long _tFocusUid;
    private bool _tFocusManual;

    // The view is a CLIENT feature: a Godot Camera2D follows the node of the
    // bound car (engine position smoothing gives the follow lag), and manual
    // pan/zoom takes over until the input stops. The sim has no camera state.
    private ulong _lastManualMs = 0;
    private Vector2 _lastFollowTarget;  // previous follow target (snap rule)

    // Per-car display state from /state (multi-car, docs/REST_API.md):
    // color name, blinker/hazard lights, the car's OWN test flags + label.
    private class CarMeta
    {
        public string Model = "blue";
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
        // The two facts the CAR DETAIL window shows that a sprite does not
        // need: who holds this car, and which rule caps it. Parsed from the same
        // `cars[]` entries as the blinkers, so a LIVE detail slot can show the
        // same chain the reviewer reads off the tape.
        public long HeldBy;
        public string CapKey = "";
        // The car's PAINT as `#rrggbb` ("" = the photographed colour of its
        // vehicle). Visual only, and only ever from /state or the tape: the
        // vehicle id decides dynamics and size, this decides colour (issue #10).
        public string Color = "";
        public string ColorApplied = " ";    // the material now on the sprite
        public Sprite2D Sprite;               // where the material goes
        // The declared per-car view options from /state (issue #9). null = the
        // test said nothing, which keeps the standing behaviour.
        public bool? OptTracks;
        public bool? OptDetail;
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
    // Keyed by PHOTO, because that is what an asset is: a picture of one vehicle.
    // A car names a MODEL (#18) and the model says which photo shows it, so
    // "what do I draw" is answered by the table rather than by string equality.
    static readonly Dictionary<string, Texture2D> _carTextures = new();
    static Texture2D CarTexture(string vehicle)
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
        // Model name → its photo. An old caller that still hands over a photo
        // word gets it back unchanged (ModelFor resolves both vocabularies).
        string photo = DrivingGame.Sim.Config.ModelFor(vehicle).Photo;
        return _carTextures.TryGetValue(photo, out var t) ? t : _carTextures["blue"];
    }

    // --- Livery (issue #10): paint the bodywork, leave the glass alone ------
    static readonly Dictionary<string, Texture2D> _maskTextures = new();
    static readonly Dictionary<string, ShaderMaterial> _liveryMats = new();
    static Shader _liveryShader;

    /// <summary>The material that paints one vehicle's bodywork in one livery, or
    /// null when the car wears none (then the photographed sprite shows, exactly
    /// as before). Cached per (vehicle, livery): a 51-car run in three liveries
    /// is THREE materials, not fifty-one, so a livery change is an assignment.
    /// A missing mask returns null on purpose — with no mask the shader would
    /// paint the whole silhouette, which is the very failure the mask exists to
    /// prevent (assets/car_&lt;vehicle&gt;_mask.png, authored in
    /// tools/make_car_sprites.py).</summary>
    static ShaderMaterial PaintMaterial(string vehicle, string livery)
    {
        if (string.IsNullOrEmpty(livery) || string.IsNullOrEmpty(vehicle)) return null;
        // The MASK belongs to the photo (the picture whose bodywork is being
        // recoloured), so a model resolves to its photo first: the cache then
        // holds one material per (photo, paint) and a 51-car run in three paints
        // is still three materials.
        string photo = DrivingGame.Sim.Config.ModelFor(vehicle).Photo;
        string key = photo + "|" + livery;
        if (_liveryMats.TryGetValue(key, out var cached)) return cached;
        _liveryShader ??= GD.Load<Shader>("res://assets/livery.gdshader");
        if (_liveryShader == null)
        {
            GD.PrintErr("color: res://assets/livery.gdshader missing - cars keep their photographed colour");
            return null;
        }
        if (!_maskTextures.TryGetValue(photo, out var mask))
            _maskTextures[photo] = mask =
                GD.Load<Texture2D>($"res://assets/car_{photo}_mask.png");
        if (mask == null)
        {
            GD.PrintErr($"color: no body mask for photo '{photo}' (model '{vehicle}') - not painting it");
            return null;
        }
        var mat = new ShaderMaterial { Shader = _liveryShader };
        mat.SetShaderParameter("mask_tex", mask);
        mat.SetShaderParameter("livery", new Color(livery));
        _liveryMats[key] = mat;
        return mat;
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
    // to these sizes; the sim collides with the SAME table (Car.Size ->
    // Config.VEHICLE_SIZES), so the drawn footprint and the hit box cannot
    // drift apart. What is still open is per-vehicle TRACK geometry
    // (wheelbase/steering limits stay sedan-like) - see SPEC.md
    // "Per-vehicle raceline & kinematics geometry".
    // Per-sprite physical footprint (width × length, metres) - the shared
    // table in Config.VEHICLE_SIZES is the single source of truth (the
    // simulation's collision math uses the same numbers).
    /// <summary>Which PHOTO shows a model. The client's picture-keyed tables
    /// (the sprite atlas, the body mask, the minimap dots) are keyed by the photo
    /// they were cut from, while a car now names a MODEL (#18) — so every crossing
    /// from one vocabulary to the other goes through here. Skipping it is not a
    /// silent wrong colour but a per-frame exception: `CarColors["mixer_truck"]`
    /// threw 946 times before this helper existed.</summary>
    static string PhotoOf(string model) => DrivingGame.Sim.Config.ModelFor(model).Photo;

    static (float W, float L) CarSizeOf(string vehicle)
    {
        var (w, l) = DrivingGame.Sim.Config.SizeForVehicle(vehicle);
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
        // a moment after the map is built (the visual check of AGENTS.md
        // "Screenshots") and then
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
                _tFocusManual = true;   // an operator-named view beats the run's
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
                // …and the operator, not the scenario, chose it.
                _tFocusManual = true;
            }
            else if (args[i] == "--zoom" && i + 1 < args.Length)
            {
                _overrideZoom = float.Parse(args[i + 1]);
                _tFocusManual = true;
            }
            else if (args[i] == "--road-materials")
            {
                // Paint roads with the material table instead of the flat fill:
                // the judgement of a generated texture needs it ON a road, at a
                // real scale, with cars on it - not as a PNG on a screen.
                _roadTextured = true;
            }
            else if (args[i] == "--road-all" && i + 1 < args.Length)
            {
                // One material for EVERY road type - the test maps and the
                // OSM city all get the same surface, so a run can be watched
                // on a realistic street instead of flat grey. <file> is the
                // asset name without extension (road_main, road_cracked, ...).
                _roadAllMaterial = args[i + 1];
                _roadTextured = true;
                i += 1;
            }
            else if (args[i] == "--road-flat" && i + 1 < args.Length)
            {
                // Types that stay FLAT: a material can only be judged worth
                // keeping against the picture it replaces, so the comparison
                // belongs in the same frame - half the roads textured, half
                // plain, one camera move.
                _roadFlat = args[i + 1];
                i += 1;
            }
            else if (args[i] == "--tile-k" && i + 1 < args.Length)
            {
                // One tile covers K road widths. The judgement "is this
                // gravel the right size for a street" is relative to the
                // street, so the knob is a ratio, not metres.
                _roadTileK = Math.Max(0.5, double.Parse(args[i + 1]));
                i += 1;
            }
            else if (args[i] == "--road-tile" && i + 1 < args.Length)
            {
                // Metres of road one generated tile covers, for EVERY type - the
                // one scale knob, so a material can be judged at more than one
                // size without editing the table.
                _roadTileOverride = double.Parse(args[i + 1]);
                i += 1;
            }
            else if (args[i] == "--road-wear" && i + 1 < args.Length)
            {
                // Mark density: 1 = one crack per ~55 m, one patch per ~260 m
                // of road. 0 turns the wear off (a clean map for a screenshot
                // you want to judge on colour alone).
                _wearDensity = Math.Clamp(float.Parse(args[i + 1]), 0f, 4f);
                i += 1;
            }
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
            else if (args[i] == "--test-slot-buttons")   // retired name, see --test-detail
                _testSlotButtons = true;
            else if (args[i] == "--test-pan-probe")
                _testPanProbe = true;
            else if (args[i] == "--test-focus-probe")
            {
                // Dev hook: the review camera's own focus question, scripted.
                // Three claims to answer without a hand on the mouse:
                //   1. does a declaration drive the view at all,
                //   2. does it FOLLOW the car it names (a frame later the view
                //      must still sit on that car, not on where it started),
                //   3. does crossing into the next scenario switch both the
                //      focus and the followed item.
                _testFocusProbe = true;
            }
            else if (args[i] == "--test-bar-ui")
                _testBarUi = true;
            else if (args[i] == "--test-jog")
                _testJog = true;
            else if (args[i] == "--ignore-map-hash")
                _ignoreMapHash = true;
            else if (args[i] == "--tape-live")
                // The live window's PICTURE from the tip of a tape that is still
                // being written - the unification of viewer and reviewer (docs/
                // SIMULATIONS_REVIEWER.md → "One client, one data source", the
                // user's decision of 2026-10-25): the scrub bar, the cursor and
                // the rate dropdown exist here too. This is a SOURCE, not a mode:
                // it does NOT set `_review`, because the live surface (speed,
                // flow, the test runner) describes the same run the picture shows
                // and must stay on screen.
                _tapeLive = true;
            else if (args[i] == "--test-play")
                _testPlay = true;
            else if (args[i] == "--no-play-lerp")
                _noPlayLerp = true;
            else if (args[i] == "--test-window-out")
                _testInfoWindowOut = true;
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
            else if (args[i] == "--window" && i + 3 < args.Length)
            {
                // Open HERE and at this size, once, before the first frame:
                // `--window <x> <y> <w> <h>` (desktop coordinates, the same
                // space Window.Position/Size use). Overrides the remembered
                // geometry for this run - and, like a hand on the border, it
                // becomes the NEW remembered geometry at the next close.
                _windowAsk = new Rect2I(int.Parse(args[i + 1]), int.Parse(args[i + 2]),
                                        int.Parse(args[i + 3]), int.Parse(args[i + 4]));
                i += 4;
            }
            else if (args[i] == "--window-default")
                _windowNoRestore = true;    // ignore user://window.ini for this run
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
            else if (args[i] == "--test-car-menu")
                _testCarHook = 1;
            else if (args[i] == "--test-car-slot")
                _testCarHook = 2;
            else if (args[i] == "--quit-on-test-done")
                _quitOnTestDone = true;
            else if (args[i] == "--motionlog" && i + 1 < args.Length)
                _motionLogPath = args[i + 1];
            else if (args[i] == "--perf-probe")
                _perfProbe = true;
            else if (args[i] == "--run-test" && i + 1 < args.Length)
                _autoRunTest = int.Parse(args[i + 1]);
            else if (args[i] == "--source" && i + 1 < args.Length)
            {
                _source = args[i + 1];
                _sourceGiven = true;
            }
            else if (args[i] == "--map" && i + 1 < args.Length)
            {
                _embeddedMapName = args[i + 1];
                _mapGiven = true;
            }
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

        // The window's opening geometry, stated ONCE, before the first frame is
        // drawn (and before the HUD measures itself against the viewport, and
        // before the map fits itself to the window): the rect it was closed with
        // last time, or the `--window` of this run, or the default. A later
        // resize would repaint the whole HUD and show the jump the user asked to
        // be rid of - and it is this point in `_Ready` that makes the difference,
        // since nothing has reached the screen yet.
        RestoreWindowPlacement();

        // Phase 7: pick the communication method. Embedded mode creates the
        // SimHost FIRST (its _Ready runs synchronously on AddChild: network,
        // sim thread and embedded REST API are up before we fetch anything).
        // A review window defaults to an IN-PROCESS ground (issue #2): the
        // geometry it draws is the SAME MapPayload.Build the /map endpoint
        // serves, so the tape-vs-ground fingerprint check means the same thing
        // either way - while a host standing beside a reviewer is a foreign
        // clock and a foreign car set, and produced two defects already (#1 a
        // ground arriving after the tape began painting, #4 that host's empty
        // /state deleting the tape's cars). `--source http` still means
        // "attach to that host" and stays the way to review a tape whose
        // ground is not on this machine.
        if (_review && !_sourceGiven)
        {
            _source = "embedded";
            _embeddedPort = 0;      // a map source: no REST API, no sim thread
            if (_reviewPath.Length == 0) _reviewPath = NewestTape();
            if (!_mapGiven)
            {
                // `map=` EMPTY is the console host's label for the OSM ground
                // (GameApi.cs:494 stamps `mapName ?? ""`), and an empty
                // _embeddedMapName means the same thing on this side.
                string tapeMap = TapeMapNameOf(_reviewPath);
                _embeddedMapName = tapeMap.Length == 0 || tapeMap == "osm"
                    ? null : tapeMap;
            }
            GD.Print($"REVIEW-SOURCE: in-process ground " +
                     $"'{_embeddedMapName ?? "osm (cache)"}' from the tape header " +
                     $"(--map overrides, --source http attaches to a host)");
        }

        bool embedded = string.Equals(_source, "embedded",
                                      StringComparison.OrdinalIgnoreCase);
        if (embedded)
        {
            _simHost = new SimHost(_embeddedMapName, _embeddedPort);
            AddChild(_simHost);
            if (_simHost.LoadError != null)
            {
                string name = _embeddedMapName ?? "osm (cache)";
                string err = _simHost.LoadError;
                _simHost.QueueFree();
                _simHost = null;
                _noGround = true;
                GD.PrintErr($"NO GROUND: cannot build '{name}' here ({err}) — " +
                            "NO cars will be painted. Put the map's data where this " +
                            "client finds it, or review against the host that has it: " +
                            "--source http http://<host>:<port>");
                // No `Map ready` will ever come to arm the one-shot - do it here.
                ArmShotIfAsked();
            }
            else
                GD.Print($"Source: EMBEDDED ({(_embeddedPort > 0
                    ? $"in-process sim, REST API on :{_embeddedPort}"
                    : "map source only - no REST API, no sim thread")})");
        }
        else
            GD.Print("Source: HTTP (external server on :5001)");

        // Review mode: load the tape BEFORE anything is fetched, and build its
        // HUD (the two-line readout + the scrub bar with the fluidity curve).
        // Two no-ground cases, deliberately told apart: an ATTACHED reviewer
        // (`--source http`) whose host is down right now still draws the tape's
        // cars, because the host may answer the next second and the readout
        // says the map is unverifiable - that is the honest half of "the
        // reviewer survives the host dying". A reviewer whose OWN machine
        // cannot build the named map (`_noGround`) paints nothing at all: no
        // retry will ever help, and cars without streets are a lie about where
        // they drove (issue #2).
        if (_review || _tapeLive)
        {
            // The reviewer and the live window are ONE instrument; what differs
            // is the SOURCE. A file (`--review [path]`) is loaded whole; the
            // live window's source is the host's GROWING tape (`--tape-live`),
            // which arrives by hops instead of in one read - and there is nothing
            // to fall back to when a hop has not landed yet, because the tape
            // starts with the sim (decision (a) of 2026-10-25: every run is
            // recorded, so the live view is never without a picture).
            if (_tapeLive)
            {
                _tapeFirstStep = -1; _tapeHead = -1; _deliveredStep = -1;
                _playing = true;          // the cursor chases the head at the dialled rate
                _cursor = 0; _playPos = 0.0;
                GD.Print("REVIEW-SOURCE live tape tip (no file loaded): " +
                         $"recording={DrivingGame.Sim.RunTape.Recording}");
            }
            else
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
                else if (!_tapeLive)
                {
                    _review = false;
                    GD.Print("!! review: no tape readable - falling back to the live view");
                }
            }
            BuildReviewHud();
            if (!_tapeLive)
                BuildRunPicker();   // a live session has ONE run: the current one
            if (_tapeLive && _simHost == null)
            {
                // The http half of the live tape source: ONE node (see `RequestTapeTip`
                // in the reviewer part - a hop carries up to 24 substeps, so one
                // request per rendered frame is already 2.4 substeps/frame against
                // the sim's 60/s). Kick it once here; the response rearms it.
                _tipHttp = new HttpRequest { UseThreads = true, Timeout = 2.0 };
                AddChild(_tipHttp);
                _tipHttp.RequestCompleted += (id, code, hdr, body) =>
                    OnTapeTipResponse(id, code, hdr, body);
                RequestTapeTip();
            }
            // A review window draws the TAPE, so a live sim beside it is
            // irrelevant to the picture - and the live surface (speed label,
            // flow, test runner) is hidden per frame. ⏹ stop is what freezes
            // the host, and only when the user presses it (docs/
            // SIMULATIONS_REVIEWER.md). `--tape-live` keeps the live surface:
            // its picture and the live sim are the same run.
        }

        // The window title carries the REST port: two windows of this
        // project differ ONLY by the server they talk to - the e2e host and
        // a scratch run look identical otherwise, which made "which map is
        // this?" unanswerable from the screen.
        var port = embedded ? _embeddedPort : PortOfUrl(MapUrl);
        _apiPort = port;      // the host THIS window talks to - see RunTestUrlOf()
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
        if (embedded && _simHost != null)
        {
            // Map in-process: the SAME payload the /map endpoint serves
            // (shared MapPayload builder), no HTTP round-trip.
            var payload = DrivingGame.Api.MapPayload.Build(_simHost.Engine.Network);
            using var doc = JsonDocument.Parse(
                JsonSerializer.SerializeToUtf8Bytes(payload));
            BuildMap(doc.RootElement);
        }
        else if (!embedded)
            FetchMap();
        // embedded with NO ground built (LoadError): nothing to draw and no
        // host to ask. Asking anyway is what today does silently - a review
        // window retrying GET /map forever while its cars drive on nothing.

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
            if (_tapeLive)
            {
                // The live tape hop: ONE node (see `RequestTapeTip` in the
                // reviewer part - a hop carries 24 substeps, so a single
                // request per frame is already ~2.4 substeps/frame against
                // the sim's 60/s; the state pair needs two because a /state
                // answer carries exactly one).
                _tipHttp = new HttpRequest { UseThreads = true, Timeout = 2.0 };
                AddChild(_tipHttp);
                _tipHttp.RequestCompleted += (id, code, hdr, body) =>
                    OnTapeTipResponse(id, code, hdr, body);
                RequestTapeTip();
            }
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
        if (!_review)
        {
            BuildTestInfo();    // ONE test surface: info rows + run controls
        }

        // --run-test <N>: fill the field and fire the same code path as a
        // button click, a moment after the UI is built.
        if (_autoRunTest > 0)
            GetTree().CreateTimer(1.0).Timeout += () => StartTestNumber(_autoRunTest);

        // The car hooks fire once the scenario has had time to put cars on the
        // map (`--run-test` starts at 1.0 s, the suite teleports within the
        // second after) - 6 s in, polled by the timer rather than a blind sleep
        // on the sim, because the check itself reports "no car yet" honestly.
        if (_testCarHook != 0)
            GetTree().CreateTimer(6.0).Timeout += DoTestCarHook;

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

        if (_testPlay)
        {
            // Dev hook (family of --test-jog): press ▶ a moment after the tape
            // loaded and LEAVE it running, so "does the picture advance
            // smoothly at 1x" is answered by the per-frame pose line of the
            // motion log rather than by an eye on a 144 Hz screen —
            // tests/pose_stutter_check.py drives exactly this.
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                StartPlay();
                GD.Print($"TEST-PLAY playing={_playing} cursor={_cursor} " +
                         $"rate={_playRate:F1}x dt={_tapeDt:F6} s tape={_tape.Count} substeps");
                // And what the clock does from there: a play that STARTS is
                // not yet a play that RUNS, and the only honest witness is the
                // state sampled over time. Both addresses go in the line — the
                // CURSOR index and the STEP it points at — because the painted
                // time is derived from the step, and a window can be smooth on
                // one clock and wrong on the other.
                for (int s = 1; s <= 4; s++)
                {
                    int at = s;      // the loop variable must not be captured
                    GetTree().CreateTimer(at).Timeout += () => GD.Print(
                        $"TEST-PLAY +{at}s playing={_playing} cursor={_cursor} " +
                        $"step={(_cursor >= 0 && _cursor < _tape.Count ? _tape[_cursor].Step : -1)} " +
                        $"playPos={_playPos:F3} tape={_tape.Count} " +
                        $"simNow={_simNow:F4} renderTarget={_renderTarget:F4} " +
                        $"dt={_lastTickDelta * 1000:F2}ms");
                }
            };
        }

        if (_testSlotButtons)
        {
            // --test-slot-buttons: the car detail window's own probe
            // (docs/CAR_DETAIL_WINDOW.md). The name predates the window — it
            // was the three slots' button test — and it keeps the flag while
            // measuring the new instrument: open, follow, resize, seek, open a
            // second. Each step prints the state it claims to have caused.
            GetTree().CreateTimer(2.0).Timeout += () => DetailProbe();
        }
        if (_testPanProbe)
        {
            // DIAGNOSTIC ONLY — it changes no state a normal run sees. The
            // report (2026-10-24): "panning in the main window does not work
            // reliable". A hand does not say when it broke, so this pushes the
            // SAME scripted map drag three times through the real input path
            // and prints the whole view state around each one, with the two UI
            // actions that plausibly sit between the working and the dead
            // attempt in between:
            //   #1 clean            → does the drag move the camera at all?
            //   centre on a car     → that is CenterViewOnSlot -> _mmAsk
            //   #2 same drag        → the reported failure, if the cam is stuck
            //   motion, no button   → a _dragging that never saw its release
            // It prints WHICH pin the camera obeys, because in review mode the
            // drag writes an OFFSET the pin adds to — so a pin that wins alone
            // makes the drag invisible while _reviewPan keeps moving. That is
            // the difference between "panning is broken" and "panning is
            // shadowed", and only the log can tell them apart.
            void Snap(string tag)
            {
                var vpS = GetViewport().GetVisibleRect().Size;
                var mmAsk = _mmAsk == null ? "-" :
                    $"({_mmAsk.Value.X:F1},{_mmAsk.Value.Y:F1})";
                GD.Print($"PAN-PROBE {tag}: vp={vpS.X:F0}x{vpS.Y:F0} " +
                         $"cam=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2}) " +
                         $"zoom={_cam.Zoom.X:F2} pan=({_reviewPan.X:F2},{_reviewPan.Y:F2}) " +
                         $"mmAsk={mmAsk} follow={DetailFollowUid()} " +
                         $"override={(_overrideCenter == null ? "-" : "set")} " +
                         $"dragging={_dragging} stripDrag={_stripDrag} " +
                         $"barTop={_barTop:F0} band={_zoneTop:F0}..{_zoneBot:F0} " +
                         $"obeys={(_review ? (DetailFollowUid() != 0L ? "slot-follow" : _mmAsk != null ? "HAND-PIN(pan ignored)" : "centroid+pan") : "camera direct")}");
            }
            // One scripted grab at a FRACTION of the window height, so the
            // answer is "from which y down does a drag stop being a grab".
            void DragAt(string tag, double yFrac, double atS)
            {
                GetTree().CreateTimer(atS).Timeout += () =>
                {
                    var vp = GetViewport().GetVisibleRect().Size;
                    var p = new Vector2(vp.X * 0.42f, vp.Y * (float)yFrac);
                    var q = p + new Vector2(120f, 0f);
                    Snap($"{tag} y={yFrac:F2} before");
                    // Who sits ON TOP of this point? This build's bindings have
                    // no Viewport.GuiFindAtPosition, so walk the HUD layer and ask
                    // each Control the same two questions a press would: is it
                    // visible, and does it stop the mouse. That names the overlay
                    // instead of guessing at it.
                    void Overlays(Vector2 pt)
                    {
                        int n = 0;
                        void Walk(Node node)
                        {
                            foreach (var ch in node.GetChildren())
                            {
                                if (ch is Control c &&
                                    (c.GetGlobalRect().HasPoint(pt) ||
                                     (_dumpAllControls && c.GetMouseFilter() == Control.MouseFilterEnum.Stop)))
                                {
                                    n++;
                                    var over = c.GetGlobalRect().HasPoint(pt);
                                    GD.Print($"PAN-PROBE   {(over ? "OVER" : "ctrl")} vis={c.IsVisibleInTree()} " +
                                             $"filter={c.GetMouseFilter()} {c.Name}<{c.GetClass()}> " +
                                             $"win='{c.GetWindow().Name}' global={c.GetGlobalRect()}");
                                }
                                Walk(ch);
                            }
                        }
                        _dumpAllControls = false;   // only the first probe point dumps the lot
                        Walk(GetTree().Root);
                        GD.Print($"PAN-PROBE   {(n == 0 ? "no Control stops the mouse here — the map should get the press" : n + " control(s) stop it here")}");
                    }
                    _dumpAllControls = tag == "#1";
                    Overlays(p);
                    void Push(Vector2 at, bool pressed)
                    {
                        GetViewport().PushInput(new InputEventMouseButton
                        {
                            Position = at, GlobalPosition = at,
                            ButtonIndex = MouseButton.Left, Pressed = pressed,
                        });
                    }
                    Push(p, true);
                    GetTree().CreateTimer(0.05).Timeout += () =>
                    {
                        // mid-drag state: is the grab even alive at this y?
                        GD.Print($"PAN-PROBE {tag} MID-DRAG dragging={_dragging} " +
                                 $"stripDrag={_stripDrag} pan=({_reviewPan.X:F2},{_reviewPan.Y:F2})");
                        GetViewport().PushInput(new InputEventMouseMotion
                        {
                            Position = q, GlobalPosition = q,
                            Relative = new Vector2(120f, 0f),
                            ButtonMask = MouseButtonMask.Left,
                        });
                        GetTree().CreateTimer(0.05).Timeout += () =>
                        {
                            Push(q, false);
                            GetTree().CreateTimer(0.12).Timeout += () =>
                                Snap($"{tag} y={yFrac:F2} after");
                        };
                    };
                };
            }
            // The clean sweep: same drag, five heights, nothing else touched.
            GetTree().CreateTimer(1.45).Timeout += () =>
            {
                // Every window this client owns, with its global rect: the drag
                // points are matched against these by hand in the log.
                GD.Print($"PAN-PROBE main vp={GetViewport().GetVisibleRect()}");
                foreach (var n in GetTree().Root.FindChildren("*", "", true, false))
                    if (n is Window w0 && w0 != GetTree().Root)
                        GD.Print($"PAN-PROBE WINDOW '{w0.Name}' visible={w0.Visible} " +
                                 $"pos={w0.Position} size={w0.Size} popup={w0.Popup} " +
                                 $"parent={w0.GetParent()?.Name}");
                foreach (var w in System.Linq.Enumerable.Empty<Window>())
                    GD.Print($"PAN-PROBE window '{w.Name}' visible={w.Visible} " +
                             $"pos={w.Position} size={w.Size} popup={w.Popup} " +
                             $"controls={w.GetChildren().OfType<Control>().Count()}" +
                             (w is Node2D ? " node2d" : ""));
            };
            DragAt("#1", 0.20, 1.6);
            DragAt("#2", 0.35, 2.6);
            DragAt("#3", 0.52, 3.6);
            DragAt("#4", 0.70, 4.6);
            // Then the action the operator does all day: centre the view on a
            // car (slot's "centre" = the hand-pin), then the SAME drag that
            // worked above. If _reviewPan moves but the camera does not, the
            // pan is not broken - it is SHADOWED by the pin that wins.
            GetTree().CreateTimer(5.6).Timeout += () =>
            {
                // The action the operator does all day: CENTRE the view on a
                // car from its detail window — that is the hand-pin — then the
                // SAME drag that worked above. If _reviewPan moves but the
                // camera does not, the pan is not broken: it is SHADOWED by the
                // pin that wins (this is the probe that proved it, 2026-10-24).
                var win = ProbeDetailWindow();
                if (win == null)
                {
                    GD.Print("PAN-PROBE centre step: no car on this frame - skipping");
                    return;
                }
                ClickInWindow(win, win.CentreButton, $"PAN-PROBE detail CENTRE uid {win.Uid}");
            };
            DragAt("#5-after-centre", 0.70, 7.0);   // the ONE band no satellite window covers
            GetTree().CreateTimer(8.2).Timeout += () =>
            {
                var vp = GetViewport().GetVisibleRect().Size;
                Snap("#6 before bare motion");
                GetViewport().PushInput(new InputEventMouseMotion
                {
                    Position = new Vector2(vp.X * 0.42f + 300f, vp.Y * 0.30f),
                    GlobalPosition = new Vector2(vp.X * 0.42f + 300f, vp.Y * 0.30f),
                    Relative = new Vector2(300f, 0f),
                });
                GetTree().CreateTimer(0.25).Timeout += () =>
                {
                    Snap("#6 after bare motion");
                    if (_screenshotPath != null) SaveShot(_screenshotPath);
                    GetTree().CreateTimer(0.3).Timeout += () => GetTree().Quit();
                };
            };
        }

        if (_testFocusProbe && _review)
        {
            // Everything below reads the tape the reviewer already loaded — no
            // host, no mouse. "Focus at cursor" is what ReviewCamera aims at.
            void Trace(string tag)
            {
                var fr = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)];
                var f = _focusAtCursor;
                long wantUid = FocusSubjectUid(f);       // the SAME resolver

                // THE number that answers "does it follow": the distance between
                // the view's centre and the focus car's OWN pose (W is the metre
                // flip only, so this is metres). ~0 = the view rides the car;
                // growing = it started on the car and drifted off.
                string carAt = "-", off = "-";
                foreach (var cf in fr.Cars)
                    if (cf.Uid == wantUid)
                    {
                        var pxCam = _cam.GlobalPosition;
                        var pxCar = W((float)cf.X, (float)cf.Y);
                        carAt = $"({cf.X:F2},{cf.Y:F2})";
                        off = $"{pxCam.DistanceTo(pxCar):F2} m";
                    }
                GD.Print($"FOCUS-P {tag}: frame {fr.Step} " +
                         $"focus={f?.Label ?? "none"}/{f?.Kind ?? "-"} " +
                         $"uid={wantUid} cam=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2}) " +
                         $"carSim={carAt} offCar={off} zoom={_cam.Zoom.X:F1}");
            }
            // A car/named declaration (something to FOLLOW) and the last note in
            // the tape (a different scenario, so a switch must happen).
            // A CAST member first ("named"): following a car the scenario NAMED
            // is the harder case (the uid must come out of the tape's name map),
            // and #18/#19 exist for exactly that.
            // RvFocus is a struct, so "first named, else first car" is a scan
            // into two nullables rather than Find(...) ??.
            RvFocus? named = null, carFocus = null;
            foreach (var nf in _tapeFocus)
            {
                if (named is null && nf.Kind == "named") named = nf;
                if (carFocus is null && nf.Kind == "car") carFocus = nf;
            }
            RvFocus? carNote = named ?? carFocus;
            GetTree().CreateTimer(1.2).Timeout += () =>
            {
                if (carNote == null)
                {
                    GD.Print("FOCUS-P: this tape declares no car focus - nothing to follow");
                    GetTree().Quit();
                    return;
                }
                _cursor = Math.Clamp(IndexOfStep(carNote.Value.Step) + 60, 0, _tape.Count - 1);
                ReviewCamera();
                Trace($"in '{carNote.Value.Label}' ({carNote.Value.Kind}) start");
                GD.Print($"FOCUS-P names on tape: {string.Join(",", _tapeNames.Select(kv => $"{kv.Key}:{kv.Value}").Take(6))}");
            };
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                // 4 s of sim later: FOLLOWING means the view moved with the car.
                _cursor = Math.Clamp(_cursor + 240, 0, _tape.Count - 1);
                ReviewCamera();
                Trace("same test, +240 substeps");
            };
            // PLAYBACK, not a seek: the user's report was "when PLAYBACK reaches
            // a new test" (2026-10-24), and a probe that only moves _cursor by
            // hand cannot see what a real ▶ does on the way. So: park just BEFORE
            // a boundary, press the real play button, and print the whole
            // precedence guard every half second while the cursor crosses it.
            GetTree().CreateTimer(3.4).Timeout += () =>
            {
                // Independent of the earlier steps (which leave the cursor at the
                // END of the tape): park just before the boundary of the note we
                // picked to follow, then let ▶ walk across it on its own.
                if (carNote == null) return;
                var nx = carNote.Value;
                _cursor = Math.Clamp(IndexOfStep(nx.Step) - 60, 0, _tape.Count - 1);
                GD.Print($"FOCUS-P PLAY: crossing into '{nx.Label}' ({nx.Kind}) at step {nx.Step}");
                void Guard(string tag)
                {
                    var fr = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)];
                    var f = _focusAtCursor;
                    long wantUid = FocusSubjectUid(f);   // the SAME resolver

                    // "the followed car is NOT in the center of the screen" is a
                    // measurement, not an impression: distance from the view's
                    // centre to the subject, in metres, next to everything that
                    // could account for it (a pin, a pan, a zoom anchored at the
                    // pointer, a slot take-over).
                    string off = "no-subject-on-frame";
                    foreach (var cf in fr.Cars)
                        if (cf.Uid == wantUid)
                            off = $"{_cam.GlobalPosition.DistanceTo(W((float)cf.X, (float)cf.Y)) * Math.Max(1e-6, _cam.Zoom.X):F0} px " +
                                  $"({(cf.X - _cam.GlobalPosition.X):+#0.00;-#0.00}, " +
                                  $"{(cf.Y + _cam.GlobalPosition.Y):+#0.00;-#0.00}) m";
                    GD.Print($"FOCUS-P {tag}: step={fr.Step} lbl={f?.Label ?? "-"} " +
                             $"kind={f?.Kind ?? "-"} wantUid={wantUid} offCentre={off} " +
                             $"zoom={_cam.Zoom.X:F1} src={_camSrc} " +
                             $"pan=({_reviewPan.X:F0},{_reviewPan.Y:F0}) " +
                             $"mmAsk={(_mmAsk == null ? "null" : "PIN")} " +
                             $"center={(_overrideCenter == null ? "null" : "SET")} " +
                             $"slotFollow={DetailFollowUid()} playing={_playing}");
                }
                Guard("before");
                ReviewButton("play");
                for (int sNo = 1; sNo <= 8; sNo++)
                {
                    int n = sNo;
                    // Half-way through, zoom the MAP the way the wheel does
                    // (anchored at a pointer in the middle of the screen) — the
                    // report pairs the two symptoms, so the trace has to show
                    // whether an anchored zoom is what moves the subject off
                    // the centre and whether the declaration now leaves it alone.
                    GetTree().CreateTimer(3.4 + n * 0.5).Timeout += () =>
                    {
                        if (n == 4)
                        {
                            var vpZ = GetViewport().GetVisibleRect().Size;
                            ZoomBy(ZoomStep, new Vector2(vpZ.X * 0.5f, vpZ.Y * 0.42f));
                            GD.Print("FOCUS-P (zoomed the map — the declaration must now leave the zoom alone)");
                        }
                        // A DRAG through the real input path: it must ANCHOR a
                        // pin (so the view is honestly the hand's, and a view
                        // that says FOLLOWING is always centred), and the next
                        // test boundary must take it back.
                        if (n == 6)
                        {
                            var vpD = GetViewport().GetVisibleRect().Size;
                            var at = new Vector2(vpD.X * 0.45f, vpD.Y * 0.40f);
                            var to = new Vector2(at.X + 140f, at.Y);
                            GetViewport().PushInput(new InputEventMouseButton {
                                Position = at, GlobalPosition = at,
                                ButtonIndex = MouseButton.Left, Pressed = true });
                            GetViewport().PushInput(new InputEventMouseMotion {
                                Position = to, GlobalPosition = to,
                                ButtonMask = MouseButtonMask.Left });
                            GetViewport().PushInput(new InputEventMouseButton {
                                Position = to, GlobalPosition = to,
                                ButtonIndex = MouseButton.Left, Pressed = false });
                            GD.Print("FOCUS-P (dragged the map through the input path)");
                        }
                        Guard($"t+{n * 0.5:F1}s");
                        // Keep playing across the boundary: the recording must
                        // re-claim the view from the hand on its own.
                        if (n == 8)
                        {
                            var nxN = _tapeFocus.FindIndex(m => m.Step > _tape[_cursor].Step + 40);
                            if (nxN >= 0)
                            {
                                var t = _tapeFocus[nxN];
                                // Park 40 substeps (0.7 s) before the boundary and
                                // let ▶ cross it: waiting out the real distance at
                                // 1x would be 16 s of probe for one log line.
                                int steps = IndexOfStep(t.Step) + 40 - _cursor;
                                _cursor = Math.Clamp(IndexOfStep(t.Step) - 40, 0, _tape.Count - 1);
                                GD.Print($"FOCUS-P handoff: was {steps} substeps from " +
                                         $"the boundary of '{t.Label}' ({t.Kind}); parked at " +
                                         $"step {_tape[_cursor].Step} with the hand holding " +
                                         $"a pin={_mmAsk != null}, playing across it now");
                                for (int k = 1; k <= 6; k++)
                                {
                                    int kk = k;
                                    GetTree().CreateTimer(7.6 + kk * 1.2).Timeout += () =>
                                    {
                                        Guard($"handoff+{kk * 1.2:F1}s");
                                        if (kk == 6)
                                        {
                                            if (_screenshotPath != null) SaveShot(_screenshotPath);
                                            GetTree().Quit();
                                        }
                                    };
                                }
                            }
                            else
                            {
                                if (_screenshotPath != null) SaveShot(_screenshotPath);
                                GetTree().Quit();
                            }
                        }
                    };
                }
                // (the handoff stage above owns the exit when there is a
                // boundary left to cross)
            };
            GetTree().CreateTimer(2.8).Timeout += () =>
            {
                var last = _tapeFocus[^1];
                _cursor = Math.Clamp(IndexOfStep(last.Step) + 30, 0, _tape.Count - 1);
                ReviewCamera();
                Trace($"into '{last.Label}' ({last.Kind})");
            };
        }

        if (_testBarUi)
        {
            // DIAGNOSTIC: the scrub bar's new zoom and its event markers,
            // pushed through the real input path. (1) wheel in / in / out must
            // keep the point under the cursor fixed and must never narrow the
            // range below one notch of detail; (2) hovering an event marker must
            // produce its tooltip text; (3) clicking it must move the cursor to
            // THAT substep, not to the pixel's fraction.
            GetTree().CreateTimer(1.8).Timeout += () =>
            {
                if (_scrubZone == null || _tape.Count < 2) { GD.Print("BAR-UI no tape/bar"); return; }
                var vp = GetViewport().GetVisibleRect().Size;
                float x = (float)(vp.X * 0.5);
                // The instrument FADES while the hand is elsewhere, and a hidden
                // control receives no input at all — so the probe pins the
                // pointer inside the band first, exactly what --test-bar-point
                // exists for. Without it every pushed event here was dropped and
                // the bar looked dead while it was only asleep.
                if (_testBarPoint == Vector2.Zero)
                    _testBarPoint = new Vector2(vp.X * 0.5f, vp.Y - 76.0f);
                // The band's y comes from the BAR's own geometry, not from
                // _zoneTop/_zoneBot: while the instrument has faded, those hold
                // the "no band" sentinel (1e9) and a click pushed there hits
                // nothing at all (measured: the probe reported zoomed=False).
                float y = (float)(vp.Y - 46.0 - 30.0);
                GetViewport().PushInput(new InputEventMouseMotion
                { Position = new Vector2(x, y), GlobalPosition = new Vector2(x, y) });
                void Wheel(bool up) => GetViewport().PushInput(new InputEventMouseButton
                {
                    Position = new Vector2(x, y), GlobalPosition = new Vector2(x, y),
                    ButtonIndex = up ? MouseButton.WheelUp : MouseButton.WheelDown,
                    Pressed = true,
                });
                // Land INSIDE a scenario first: the head of a tape recorded
                // mid-session belongs to no test at all, and "which test is this"
                // is then legitimately empty.
                if (_tapeFocus.Count > 0)
                {
                    _cursor = Math.Clamp(IndexOfStep(_tapeFocus[0].Step) + 5, 0, _tape.Count - 1);
                    GD.Print($"BAR-UI cursor into test '{_tapeFocus[0].Label}' at " +
                             $"index {_cursor} (step {_tape[_cursor].Step})");
                }
                GD.Print($"BAR-UI start range {_viewLo * (_tape.Count - 1):F0}.." +
                         $"{_viewHi * (_tape.Count - 1):F0} of {_tape.Count - 1} " +
                         $"· cursor {_tape[_cursor].Step}");
                GetTree().CreateTimer(0.2).Timeout += () =>
                {
                    Wheel(true);
                    GetTree().CreateTimer(0.2).Timeout += () => Wheel(true);
                    GetTree().CreateTimer(0.2).Timeout += () => Wheel(false);
                    GetTree().CreateTimer(0.3).Timeout += () =>
                    {
                        GD.Print($"BAR-UI after 2 in + 1 out: range " +
                                 $"{_viewLo * (_tape.Count - 1):F0}..{_viewHi * (_tape.Count - 1):F0} " +
                                 $"zoomed={_barZoomed} · ticks: " +
                                 $"{string.Join(",", _tickSim.Select(t => t.ToString("F1")))} s");
                        // The first marker in the hit list, hovered then clicked.
                        GD.Print($"BAR-UI hits now {_markerHits.Count} " +
                                 $"selected={_selected.Count} zoneRect={_scrubZone.GetGlobalRect()} " +
                                 $"barVisible={_bar?.Visible} tip={_tipLabel != null}");
                        if (_markerHits.Count == 0)
                        {
                            GD.Print("BAR-UI no marker hits (nothing selected?)");
                            GetTree().CreateTimer(0.2).Timeout += () => GetTree().Quit();
                            return;
                        }
                        var h = _markerHits[0];
                        float mx = (float)((h.X0 + h.X1) / 2.0);
                        GD.Print($"BAR-UI marker at x {h.X0:F0}..{h.X1:F0} step {h.Step}: {h.Text}");
                        GetViewport().PushInput(new InputEventMouseMotion
                        {
                            Position = new Vector2(mx, y), GlobalPosition = new Vector2(mx, y),
                        });
                        GetTree().CreateTimer(0.2).Timeout += () =>
                        {
                            // Wheel and bare motion are NOT delivered to handlers by
                        // PushInput in this build (measured: the button events
                        // arrive, these never do), so the two features that rest
                        // on them are exercised by calling the SAME entry points
                        // the real events feed, and a screenshot shows the result.
                        BarZoom(true, (float)(_markerHits[0].X0 + 4));
                        BarZoom(true, (float)(_markerHits[0].X0 + 4));
                        BarZoom(false, (float)(_markerHits[0].X0 + 4));
                        GD.Print($"BAR-UI after 2 in + 1 out (direct): range " +
                                 $"{_viewLo * (_tape.Count - 1):F0}..{_viewHi * (_tape.Count - 1):F0} " +
                                 $"zoomed={_barZoomed}");
                        ReviewMarkerHover(new Vector2(mx, y));
                        GD.Print($"BAR-UI tooltip visible={_tipLabel?.Visible} " +
                                     $"text='{_tipLabel?.Text}'");
                            GetViewport().PushInput(new InputEventMouseButton
                            {
                                Position = new Vector2(mx, y), GlobalPosition = new Vector2(mx, y),
                                ButtonIndex = MouseButton.Left, Pressed = true,
                            });
                            GetTree().CreateTimer(0.2).Timeout += () =>
                            {
                                GD.Print($"BAR-UI click -> frame {_tape[_cursor].Step} " +
                                         $"(wanted {h.Step}) cursorOffView={_cursorOffView}");
                                GetViewport().PushInput(new InputEventMouseButton
                                {
                                    Position = new Vector2(mx, y), GlobalPosition = new Vector2(mx, y),
                                    ButtonIndex = MouseButton.Left, Pressed = false,
                                });
                                GetTree().CreateTimer(0.3).Timeout += () =>
                                {
                                    // Back INSIDE a scenario, so the Test panel
                                    // has something to say (the click above put
                                    // the cursor before the first note).
                                    _cursor = Math.Clamp(IndexOfStep(_tapeFocus[0].Step) + 30,
                                                         0, _tape.Count - 1);
                                    FocusAtCursor();
                                    // The panel is refreshed on the frame tick;
                                    // ask it now, so the probe reports the panel
                                    // for THIS cursor instead of whatever the
                                    // last frame happened to show.
                                    UpdateTestPanel();
                                    GD.Print($"BAR-UI test panel:\n{_testPanelLabel?.Text}");
                                    GD.Print($"BAR-UI focus at cursor: label='{_focusAtCursor?.Label}' " +
                                             $"kind={_focusAtCursor?.Kind} " +
                                             $"boundaries={_tapeFocus.Count} results={_tapeResults.Count}");
                                    // Re-assert the framing the panel just
                                    // described, so the shot shows the SAME
                                    // instrument state the log line reports.
                                    ReviewMarkers();
                                    if (_screenshotPath != null) SaveShot(_screenshotPath);
                                    GetTree().Quit();
                                };
                            };
                        };
                    };
                };
            };
        }

        if (_testInfoWindowOut)
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
        // The child sweep above also kills the track lines and flag nodes that
        // were created before the map arrived (state polls start immediately) -
        // drop the dangling references and force a rebuild on the next frame.
        // Holding them is exactly the ObjectDisposedException storm of issue #1,
        // and it now covers the per-car tyre lines of #9 as well.
        _tyreLineNodes.Clear();
        _tyreLines.Clear();
        _lastTrailSig = "";
        _flagNodes.Clear();
        _lastFlagFp = "";
        // … and the courtesy cones of the cars (issue #1): ClearCars() below
        // frees the cars themselves, but a cone whose car is NOT in _carBuf
        // yet would survive here as a dead dictionary entry, and the next
        // DrawCourtesy would set Visible on a freed Polygon2D - an
        // ObjectDisposedException in _Process on every frame after that.
        foreach (var c in _courtesy.Values)
            if (GodotObject.IsInstanceValid(c)) c.QueueFree();
        _courtesy.Clear();
        ClearCars();

        // The stamp of the ground about to be drawn, BEFORE the geometry:
        // the check of a tape against it must run even when the payload has
        // no geometry the tape could match (a foreign map), and drawing that
        // foreign ground and then refusing the picture is exactly what a
        // mismatch looks like on screen.
        if (root.TryGetProperty("map_fingerprint", out var mfEl))
            _drawnMapHash = mfEl.GetString();
        CheckTapeAgainstMap();

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
                float widthM = mark.TryGetProperty("width_m", out var wm) ? wm.GetSingle() : PaintWidthM;
                _narrowPaint = Math.Min(_narrowPaint, widthM);

                var pts = Pts(mark.GetProperty("pts"));
                switch (style)
                {
                    case "dashed":
                        _markings.Add(new MarkingLine { Pts = pts, DashM = _lDash, GapM = _lGap,
                                                         Color = white, WidthM = widthM });
                        break;
                    case "p_dash":
                        _markings.Add(new MarkingLine { Pts = pts, DashM = _pDash, GapM = _pGap,
                                                         Color = white, WidthM = widthM });
                        break;
                    case "solid":
                        _markings.Add(new MarkingLine { Pts = pts, Solid = true,
                                                        Color = cream, WidthM = widthM,
                                                        Breitstrich = widthM >= 0.25f });
                        break;
                    case "guardrail":
                        _markings.Add(new MarkingLine { Pts = pts, Solid = true,
                                                        Color = rail, WidthM = widthM });
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
        // paint. Its width is BOUNDED BY THE CENTRE LINE (user decision
        // 2026-10-26: an outer white line must never read wider than the
        // middle line): the narrowest paint the map declares - which is the
        // centre/dash width, 0.15 m today - caps it. Line2D.Width measured as
        // a HALF width in this build (a 0.4 parameter shows 0.20 m, 0.25 shows
        // 0.125 m), so the parameter is twice the paint.
        // Screen ceiling, converted back to metres for THIS view.
        float edgePaint = Math.Min(PaintWidthM, _narrowPaint);
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
                    Width = 2f * edgePaint,
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
                    Width = 2f * PaintWidthM,   // bridge decks: no centre line
                    // there to bound it, so the paint width is the ceiling.
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

        // Camera: fit the whole map into the viewport (or use overrides) -
        // EXCEPT for a tape (issue #1): a review window looks at the cursor's
        // hotspot at the pace's own zoom, so a ground that arrives late (the
        // host answered only after the tape was painting) must leave the view
        // where the tape put it, not snap back to a fit of the whole map.
        // Before the tape has a frame under the cursor it owns nothing, and the
        // fit still runs - that is the live-tip window's opening view.
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        bool tapeOwnsView = (_review || _tapeLive) && _cursor >= 0;
        float zoom = tapeOwnsView
            ? _cam.Zoom.X
            : Mathf.Min(vp.X / _bounds.Size.X, vp.Y / _bounds.Size.Y) * 0.95f;
        var center = tapeOwnsView
            ? _cam.GlobalPosition
            : W(_bounds.Position.X + _bounds.Size.X / 2f,
                _bounds.Position.Y + _bounds.Size.Y / 2f);
        if (_overrideZoom.HasValue) zoom = _overrideZoom.Value;
        if (_overrideCenter.HasValue) center = _overrideCenter.Value;
        _cam.Zoom = new Vector2(zoom, zoom);
        if (!tapeOwnsView) _cam.GlobalPosition = center;
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
                //
                // The pan offset starts over here: the handle names an
                // ABSOLUTE place, and that is what must be centred - a stale
                // offset from an earlier drag would quietly move the spot the
                // hand just pointed at (and ReviewCamera now adds the pan to
                // the pin, so it has to be zeroed by whoever sets the pin).
            {
                _mmAsk = want;
                _reviewPan = Vector2.Zero;
            }
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

        ArmShotIfAsked();
        if (_resizeTo.X > 0 && _resizeTo.Y > 0)
        {
            // 1.5 s BEFORE the shot (and never at t=0 - the point of the hook
            // is a size change that happens AFTER the HUD exists), so a run
            // with both flags pictures the resized window.
            float when = Mathf.Max(0.6f, _shotDelay - 1.5f);
            GetTree().CreateTimer(when).Timeout += () =>
            {
                // Clamped to the real screen (the --watch size of 3393x1908 is
                // already near a 2560x1440 desktop): asking Godot for more than
                // the display puts part of the window off-screen and the HUD
                // rows (laid out from GetVisibleRect) sit in the invisible band.
                var scr = DisplayServer.ScreenGetSize();
                var w = (int)_resizeTo.X;
                var h = (int)_resizeTo.Y;
                if (scr.X > 0 && w > scr.X - 20) w = scr.X - 20;
                if (scr.Y > 0 && h > scr.Y - 20) h = scr.Y - 20;
                if (w < 320) w = 320;
                if (h < 200) h = 200;
                GetWindow().Size = new Vector2I(w, h);
                // Centre it on the screen, so the doubled window keeps the
                // title bar and both HUD corners inside the visible area.
                GetWindow().Position = new Vector2I((scr.X - w) / 2, (scr.Y - h) / 2);
                GD.Print($"window resized to {w}x{h} (--resize, clamped to " +
                         $"screen {scr.X}x{scr.Y}), centred at " +
                         $"{GetWindow().Position.X},{GetWindow().Position.Y}");
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

    /// <summary>The /run_test URL of THE HOST THIS WINDOW IS ATTACHED TO.
    /// `RunTestUrl` names :5001 exactly like `MapUrl`/`StateUrl` do, but the
    /// run controls have to reach the server the window actually talks to: a
    /// session started on another port (`scripts/open.sh --port 5011`, an
    /// embedded probe) clicked "Run Test" into the void - measured 2026-10-25
    /// on :5035, where the click produced neither a run nor a log line, because
    /// the request went to a free :5001.</summary>
    string RunTestUrlOf() =>
        $"http://127.0.0.1:{(_apiPort > 0 ? _apiPort : PortOfUrl(RunTestUrl))}/run_test";

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
    /// each other, which is what it looks like from above.
    ///
    /// With `--road-materials` a ribbon instead goes to DrawTexturedRibbon - a
    /// Polygon2D whose per-vertex texture offsets place the generated tile in
    /// metres of road; a type with no material, or a texture file that is not
    /// in the project, still falls through to the flat fill below, so the map
    /// is never half-painted because one file is missing.</summary>
    /// Deterministic pseudo-random for the wear: xorshift64, seeded from the
    /// road shape. Chosen over Godot's RandomNumberGenerator so the sequence
    /// is a pure function of that seed - same map, same cracks, in any run.
    private long _wearState = 0L;   // the wear PRNG, reseeded per road shape
    private string _wearDbg = "";   // first patch centres, for a screenshot probe

    /// Deterministic pseudo-random for the wear: xorshift64 over a member
    /// state. A pure function of the seed, so the same map draws the same
    /// wear in every run and every tape replay.
    private double WearNext()
    {
        _wearState ^= _wearState << 13;
        _wearState ^= (long)((ulong)_wearState >> 7);
        _wearState ^= _wearState << 17;
        var u = (ulong)_wearState;
        return (double)(u >> 11) / (double)(1L << 53);
    }

    /// [0,1) as a float - the shape of one jitter term.
    private float WearF()
    {
        var d = WearNext();
        return (float)d;
    }

    /// [0,max) as an int - which crack shape, how many arms.
    private int WearI(int max)
    {
        var d = WearNext();
        return (int)(d * max);
    }

    private static long WearSeed(Vector2[] cen)
    {
        long h = 0x5DE000000000000L;
        for (int i2 = 0; i2 < cen.Length; i2++)
        {
            long xx = (long)Math.Round(cen[i2].X * 769f), yy = (long)Math.Round(cen[i2].Y * 769f);
            h = (h ^ (xx * 1000003L + yy * 4000009L)) * 1099511628211L;
        }
        return h == 0L ? 1L : h;
    }

    /// Point, unit direction and half-width on the ribbon at `at` metres.
    private static bool RibbonAt(Vector2[] cen, double[] cum, float[] hw, int n,
                                 double at, out Vector2 p, out Vector2 dir, out float halfW)
    {
        for (int i2 = 0; i2 + 1 < n; i2++)
        {
            var segLen = cum[i2 + 1] - cum[i2];
            if (at >= cum[i2] && at < cum[i2 + 1] && segLen > 1e-6)
            {
                var t = (float)((at - cum[i2]) / segLen);
                p = cen[i2] + (cen[i2 + 1] - cen[i2]) * t;
                float dx = cen[i2 + 1].X - cen[i2].X, dy = cen[i2 + 1].Y - cen[i2].Y;
                var dl = (float)Math.Sqrt(dx * dx + dy * dy);
                dir = new Vector2(dx / dl, dy / dl);
                halfW = Math.Min(hw[i2], hw[i2 + 1]);
                return true;
            }
        }
        p = new Vector2(0, 0); dir = new Vector2(0, 0); halfW = 0f;
        return false;
    }

    /// <summary>Wear marks for ONE road ribbon: cracks and repair patches,
    /// placed along its centreline and limited by its own edge profile. Seeded
    /// from the ribbon's vertices, so the same map always shows the same wear
    /// - a tape replayed next week agrees with the run it recorded. Purely
    /// visual: the sim, the tests and the tape never see it.</summary>
    private void AddRoadWear(List<Vector2> left, List<Vector2> right, Color roadCol)
    {
        if (_wearDensity <= 0f || left.Count < 3) { return; }
        var n = Math.Min(left.Count, right.Count);
        var cen = new Vector2[n];
        var hw = new float[n];
        var cum = new double[n];
        for (int i2 = 0; i2 < n; i2++)
        {
            cen[i2] = new Vector2((left[i2].X + right[i2].X) * 0.5f,
                                  (left[i2].Y + right[i2].Y) * 0.5f);
            hw[i2] = left[i2].DistanceTo(right[i2]) * 0.5f;
            if (i2 > 0)
                cum[i2] = cum[i2 - 1] + cen[i2].DistanceTo(cen[i2 - 1]);
        }
        var total = cum[n - 1];
        if (total < 6.0) { return; }
        _wearState = WearSeed(cen);
        var at = 18.0 + WearNext() * 25.0;
        while (at < total - 5.0)
        {
            CrackAt(cen, cum, hw, n, at, roadCol);
            at += 55.0 / Math.Max(0.05f, _wearDensity);
        }
        at = 45.0 + WearNext() * 120.0;
        while (at < total - 12.0)
        {
            PatchAt(cen, cum, hw, n, at, roadCol);
            at += 260.0 / Math.Max(0.05f, _wearDensity);
        }
    }

    /// Cracks come in three shapes - a real street has a mix:
    /// 0 a seam across the carriageway, 1 a long crack ALONG it with
    /// branches, 2 a short star where the asphalt failed. Jitter changes the
    /// kink frequency, the branch count and the width, so two cracks on the
    /// same road never look alike.
    private void CrackAt(Vector2[] cen, double[] cum, float[] hw, int n,
                        double at, Color roadCol)
    {
        Vector2 p = new Vector2(0, 0);
        Vector2 dir = new Vector2(0, 0);
        float halfW = 0f;
        if (!RibbonAt(cen, cum, hw, n, at, out p, out dir, out halfW) || halfW < 0.5f) { return; }
        var up = new Vector2(-dir.Y, dir.X);
        var style = WearI(3);
        var thick = 0.045f + WearF() * 0.03f;
        var alpha = 0.38f + WearF() * 0.22f;
        var col = new Color(roadCol.R * 0.5f, roadCol.G * 0.5f, roadCol.B * 0.55f, alpha);
        var along = (WearF() - 0.5f) * halfW * 0.8f;
        var o = p + dir * along;
        if (style == 1)
        {
            var l2 = halfW * (1.2f + WearF() * 2.2f);
            var cur = o - dir * (l2 * 0.5f);
            var steps2 = 6 + WearI(8);
            for (int i2 = 0; i2 < steps2; i2++)
            {
                var wob = (WearF() - 0.5f) * halfW * 0.5f;
                var nxt = cur + dir * (l2 / steps2) + up * wob;
                AddStrip(_crackMesh, cur, nxt, thick, col);
                if (WearF() < 0.45f)
                {
                    var side = WearF() < 0.5f ? -1f : 1f;
                    var bl = halfW * (0.25f + WearF() * 0.5f) * side;
                    var bend = (WearF() - 0.5f) * 0.7f;
                    var b2 = cur + up * bl + dir * bl * bend;
                    AddStrip(_crackMesh, cur, b2, thick * 0.75f, col);
                    if (WearF() < 0.4f)
                        AddStrip(_crackMesh, b2, b2 + up * bl * 0.45f, thick * 0.6f, col);
                }
                cur = nxt;
            }
        }
        else if (style == 2)
        {
            var arms = 3 + WearI(3);
            var baseA = WearF() * 3.14159f;
            var reach = halfW * (0.25f + WearF() * 0.5f);
            for (int i2 = 0; i2 < arms; i2++)
            {
                var ang = baseA + i2 * (2f * 3.14159f / arms) + (WearF() - 0.5f) * 0.6f;
                var len2 = reach * (0.6f + WearF() * 0.7f);
                var d2 = new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang)) * len2;
                var mid = d2 * 0.5f + up * ((WearF() - 0.5f) * 0.25f) * len2;
                AddStrip(_crackMesh, o, o + mid, thick, col);
                AddStrip(_crackMesh, o + mid, o + d2, thick * 0.85f, col);
            }
        }
        else
        {
            var len = halfW * 2f * (0.45f + WearF() * 0.5f);
            var tilt = (WearF() - 0.5f) * 0.9f;
            var cur2 = o - up * (len * 0.5f) - dir * (len * 0.5f) * tilt;
            var steps3 = 4 + WearI(6);
            for (int i2 = 0; i2 < steps3; i2++)
            {
                var nxt2 = cur2 + up * (len / steps3)
                    + dir * ((WearF() - 0.5f) * thick * 10f);
                AddStrip(_crackMesh, cur2, nxt2, thick, col);
                cur2 = nxt2;
            }
        }
        _wearCracks += 1;
    }

    /// A repair patch IS basically rectangular - a tar cutter follows the
    /// crack it covers, and that reads as a quadrilateral along the road. What
    /// makes it look real is that the edges are not straight: each side is
    /// subdivided and wobbles a little sideways (a few centimetres of drift
    /// with a phase, not random spikes), and the corners are cut slightly
    /// inside. That is the user's correction 2026-10-26: right-angled overall,
    /// wobbling lines, never sharp-cornered, never a star.
    private void PatchAt(Vector2[] cen, double[] cum, float[] hw, int n,
                        double at, Color roadCol)
    {
        Vector2 p = new Vector2(0, 0);
        Vector2 dir = new Vector2(0, 0);
        float halfW = 0f;
        if (!RibbonAt(cen, cum, hw, n, at, out p, out dir, out halfW) || halfW < 0.7f) { return; }
        var up = new Vector2(-dir.Y, dir.X);
        var ry = halfW * (0.55f + WearF() * 0.4f);              // across the road
        var rx = ry * (0.8f + WearF() * 1.6f);                  // along the road
        var c = p + up * ((WearF() - 0.5f) * halfW * 0.3f)
            + dir * ((WearF() - 0.5f) * rx * 0.3f);
        // four corners of the rectangle in the road's frame
        var corners = new Vector2[4];
        corners[0] = c - dir * rx - up * ry;
        corners[1] = c + dir * rx - up * ry;
        corners[2] = c + dir * rx + up * ry;
        corners[3] = c - dir * rx + up * ry;
        // each edge gets a few mid points that drift sideways by a phase
        var pts = new List<Vector2>();
        var wobX = (WearF() - 0.5f) * 0.06f;
        var wobY = (WearF() - 0.5f) * 0.06f;
        for (int e = 0; e < 4; e++)
        {
            var a = corners[e];
            var b = corners[(e + 1) % 4];
            var d = b - a;
            var dl = (float)Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (dl < 1e-4f) { continue; }
            var nrm = new Vector2(-d.Y / dl, d.X / dl);
            var steps2 = 4 + WearI(3);
            pts.Add(a);
            for (int i2 = 1; i2 < steps2; i2++)
            {
                var t = (float)i2 / steps2;
                var ph = (float)Math.Sin(t * 6.283f * (1f + WearF() * 2f));
                var off = (float)(ph * dl * 0.02f);
                pts.Add(a + d * t + nrm * (off + (WearF() - 0.5f) * 0.06f));
            }
        }
        var pick = WearF();
        float f = pick < 0.55f ? 0.74f : 1.15f;
        var col = new Color(Math.Min(1f, roadCol.R * f), Math.Min(1f, roadCol.G * f),
                            Math.Min(1f, roadCol.B * f), 0.85f);
        AddPolygon(pts, col, 1);
        var seam = new Color(roadCol.R * 0.6f, roadCol.G * 0.6f, roadCol.B * 0.65f, 0.4f);
        for (int i2 = 0; i2 < pts.Count; i2++)
            AddStrip(_patchMesh, pts[i2], pts[(i2 + 1) % pts.Count], 0.05f, seam);
        _wearPatches += 1;
    }

    private void AddTri(ImmediateMesh m, Vector2 a, Vector2 b, Vector2 c, Color col)
    {
        m.SurfaceSetColor(col);
        m.SurfaceAddVertex(new Vector3(a.X, a.Y, 0));
        m.SurfaceSetColor(col);
        m.SurfaceAddVertex(new Vector3(b.X, b.Y, 0));
        m.SurfaceSetColor(col);
        m.SurfaceAddVertex(new Vector3(c.X, c.Y, 0));
    }

    private void AddStrip(ImmediateMesh m, Vector2 a, Vector2 b, float thick, Color col)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        var dl = (float)Math.Sqrt(dx * dx + dy * dy);
        if (dl < 1e-4f) { return; }
        float nx = -dy / dl * thick * 0.5f, ny = dx / dl * thick * 0.5f;
        var a1 = new Vector2(a.X - nx, a.Y - ny);
        var a2 = new Vector2(a.X + nx, a.Y + ny);
        var b1 = new Vector2(b.X - nx, b.Y - ny);
        var b2 = new Vector2(b.X + nx, b.Y + ny);
        AddQuad(m, a1, a2, b2, b1, col);
    }

    private void AddQuad(ImmediateMesh m, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
    {
        AddTri(m, a, b, c, col);
        AddTri(m, a, c, d, col);
    }

    private void AddRoadStrips(JsonElement stripsEl, Color color)
    {
        if (_crackMesh == null)
        {
            _crackMesh = new ImmediateMesh();
            _crackMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            _patchMesh = new ImmediateMesh();
            _patchMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        }
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        mesh.SurfaceSetColor(color);
        int quads = 0, textured = 0, flat = 0;
        foreach (var s in stripsEl.EnumerateArray())
        {
            var left = Pts(s.GetProperty("left"));
            var right = Pts(s.GetProperty("right"));
            if (left.Count != right.Count || left.Count < 2) continue;
            var hw = s.TryGetProperty("highway", out var hEl) ? hEl.GetString() : "";
            AddRoadWear(left, right, color);
            if (_roadTextured && DrawTexturedRibbon(left, right, hw) > 0)
            { textured += 1; continue; }
            flat += 1;
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
        if (quads > 0) { mesh.SurfaceEnd(); _world.AddChild(new MeshInstance2D { Mesh = mesh, ZIndex = 0 }); }
        if (_wearPatches > 0)
        {
            _patchMesh.SurfaceEnd();
            _world.AddChild(new MeshInstance2D { Mesh = _patchMesh, ZIndex = 1 });
        }
        if (_wearCracks > 0)
        {
            _crackMesh.SurfaceEnd();
            _world.AddChild(new MeshInstance2D { Mesh = _crackMesh, ZIndex = 2 });
        }
        if (_wearDensity > 0f)
            GD.Print("ROAD-WEAR ", _wearCracks, " cracks, ", _wearPatches,
                " repair patches at density ", _wearDensity,
                " - deterministic per road shape, --road-wear 0 turns them off");
        if (_wearDbg != "")
            GD.Print("ROAD-WEAR-PLACEMENTS\n", _wearDbg);
        if (textured > 0 || _roadTextured)
        {
            GD.Print("ROAD-MATERIALS ", textured, " ribbons painted with a material, ",
                flat, " with the flat fill (types without a material)");
        }
    }

    /// <summary>Paint one road ribbon with a tiling texture. Godot 4.7 has no
    /// runtime sampler-repeat API any more (the old `Texture.SET_REPEAT`/
    /// `set_repeat` surface is gone from the class docs and from the C#
    /// binding), and 2D textures CLAMP - so one polygon spanning a whole road
    /// would paint one stretched tile. The honest shape is one polygon per
    /// TILE PATCH: the ribbon is walked by arc length, cut into patches of
    /// `tileM` metres along the road and `tileM` metres across it, and every
    /// patch carries UV inside a single tile. The generated tiles are seamless
    /// at their own border, so patch edges meet continuously; a short final
    /// patch just samples a partial tile, which never leaves [0,1].
    /// Returns the number of patches painted - 0 means "no material for this
    /// type", the caller's flat-fill fallback.</summary>
    private int DrawTexturedRibbon(List<Vector2> left, List<Vector2> right, string highway)
    {
        var tileK = _roadTileK;
        var file = "";
        var tint = ColorPlain;
        if (_roadAllMaterial != "")
        {
            file = _roadAllMaterial;
        }
        else
        {
            if (!ROAD_MATERIALS.TryGetValue(highway, out var mm)) { return 0; }
            if (_roadFlat.Split(",").Any(x => x.Trim() == highway)) { return 0; }
            var (f2, k2, t2) = mm;
            file = f2;
            tileK = k2;
            tint = t2;
        }
        var tileM = tileK;
        if (_roadTileOverride > 0.0) { tileM = _roadTileOverride; }
        var tex = RoadTexture(file);
        if (tex is null) { return 0; }
        var n = left.Count;
        if (n < 2) { return 0; }
        // Cumulative arc length along the ribbon's centreline, in metres.
        var cum = new double[n];
        var acc = 0.0;
        for (int i = 1; i < n; i++)
        {
            var m0 = (left[i - 1] + right[i - 1]) * 0.5f;
            var m1 = (left[i] + right[i]) * 0.5f;
            acc += m0.DistanceTo(m1);
            cum[i] = acc;
        }
        var total = acc;
        if (total <= 1e-6) { return 0; }
        // These ribbons are constant-width by construction, so the lateral
        // split is the same everywhere; a wider-than-one-tile road is cut into
        // bands of tileM metres, each band keeping its UV inside one tile.
        var widthM = (double)left[n / 2].DistanceTo(right[n / 2]);
        if (widthM <= 1e-6) { return 0; }
        // The tile scale follows the ROAD: one tile covers `_roadTileK` road
        // widths, so a material reads with the same feature size relative to
        // the street it paints, on a 3.5 m lane and on a 14 m motorway alike.
        // A generator prompt cannot know the road it will be pasted onto, so
        // the metre-scale belongs here, not in the asset or in a per-type
        // constant. `--road-tile <m>` still overrides it for one run.
        if (_roadTileOverride <= 0.0) { tileM = _roadTileK * widthM; }
        int bands = widthM <= tileM ? 1 : (int)(widthM / tileM) + 1;
        var pxPerM = (double)tex.GetWidth() / tileM;
        int patches = 0;
        // Chunk boundaries are PROFILE POINTS, never an arc length between two
        // of them: with `s0 += tileM` the next chunk started at a point whose
        // cum is BELOW s0, so its first vertices got a negative u - clamped to
        // the texture's last column, which painted a combed band at every
        // chunk boundary (measured: one band per 8 m at 20 px/m).
        var i0 = 0;
        while (i0 + 1 < n)
        {
            var i1 = i0 + 1;
            while (i1 + 1 < n && cum[i1 + 1] - cum[i0] <= tileM) { i1 += 1; }
            for (int b = 0; b < bands; b++)
            {
                var f0 = (double)b * tileM / widthM;
                var f1 = Math.Min((double)(b + 1) * tileM / widthM, 1.0);
                if (f1 <= f0) { continue; }
                var pts = new List<Vector2>();
                var uv = new List<Vector2>();
                for (int i = i0; i <= i1; i++)
                {
                    var e = (float)(cum[i] - cum[i0]) * (float)pxPerM;
                    pts.Add(left[i].Lerp(right[i], (float)f0));
                    uv.Add(new(e, 0f));
                }
                for (int i = i1; i >= i0; i--)
                {
                    var e = (float)(cum[i] - cum[i0]) * (float)pxPerM;
                    var v = (float)((f1 - f0) * widthM) * (float)pxPerM;
                    pts.Add(left[i].Lerp(right[i], (float)f1));
                    uv.Add(new(e, v));
                }
                if (pts.Count >= 3)
                {
                    var poly = new Polygon2D { Polygon = pts.ToArray(), Texture = tex,
                        Color = tint, ZIndex = 0 };
                    poly.Call("set_uv", Variant.From(uv.ToArray()));
                    _world.AddChild(poly);
                    patches += 1;
                }
            }
            i0 = i1;
        }
        _texPolys += patches;
        return patches;
    }

    private static readonly Color ColorPlain = new(1f, 1f, 1f);

    /// <summary>Which material paints which OSM road type. Textures generated
    /// by scripts/comfy_texture.py at a metre scale that suits the type: a
    /// motorway reads its tiles over 8 m, a residential lane over 4 m. Tints
    /// stay near white so the judgement is about the picture, not about a
    /// darkening filter.</summary>
    /// <summary>Which material paints which OSM road type, and how large one
    /// tile is ON THAT ROAD: `TileK` is metres-per-tile as a multiple of the
    /// road's own width, so a material can never read as boulders on a lane or
    /// as wallpaper on a motorway. The values come from what the material must
    /// depict at real size, not from taste: fine asphalt aggregate is millimetres
    /// -> a tile must cover ~1.5 m of a 7 m road (K=0.2); the worn concrete that
    /// read right at full-road scale keeps K=1; paving stones at ~0.2 m need a
    /// tile of ~2.5 m on a 3.5 m lane (K=0.7). Tint stays near white so the
    /// judgement is about the picture. A type without a material keeps the flat
    /// fill; `--road-flat` forces flat per run without touching this table.</summary>
    private static readonly Dictionary<string, (string File, double TileK, Color Tint)>
        ROAD_MATERIALS = new()
    {
        ["motorway"] = ("road_main", 1.0, ColorPlain),
        ["motorway_link"] = ("road_main", 1.0, ColorPlain),
        ["trunk"] = ("road_main", 1.0, ColorPlain),
        ["trunk_link"] = ("road_main", 1.0, ColorPlain),
        ["primary"] = ("road_main", 1.0, ColorPlain),
        ["primary_link"] = ("road_main", 1.0, ColorPlain),
        ["secondary"] = ("road_cracked", 1.0, ColorPlain),
        ["secondary_link"] = ("road_cracked", 1.0, ColorPlain),
        ["tertiary"] = ("road_cracked", 1.0, ColorPlain),
        ["tertiary_link"] = ("road_cracked", 1.0, ColorPlain),
        ["residential"] = ("road_cobble", 0.7, ColorPlain),
        ["unclassified"] = ("road_cobble", 0.7, ColorPlain),
        ["service"] = ("road_cracked", 0.5, ColorPlain),
    };

    private bool _roadTextured = false;        // --road-materials
    private double _roadTileOverride = 0.0;    // --road-tile <m>: one scale for every type
    private double _roadTileK = 1.0;           // --tile-k: metres per tile = K * road width
    /// Dev flag: log the client's own frame cost and node count, every 2 s,
    /// so the material/wear path can be compared against the flat fill
    /// (docs/GODOT_FRONTEND.md: the renderer's cost scales with polygons, not
    /// with the tile's origin).
    private bool _perfProbe;
    private double _perfAccMs, _perfMaxMs, _perfWall0;
    private double _perfFrameAccMs, _perfFrameMaxMs;
    private int _perfFrames;
    private string _roadAllMaterial = "";      // --road-all <file>: one material for every type
    private float _wearDensity = 1.0f;         // --road-wear <0..4>
    private ImmediateMesh _crackMesh, _patchMesh;
    private int _wearCracks = 0, _wearPatches = 0;
    private int _texPolys = 0;   // textured polygons painted (the cost driver)
    /// The OFF-ROAD ground (docs/GODOT_FRONTEND.md, "the housing area"): the
    /// villa district between the streets is not flat green - it is gardens,
    /// hedges and lawn. Three generated aerial tiles are laid as a grid UNDER
    /// the road fill (the roads are opaque, so no boolean with the paved area
    /// is needed), and every cell picks its variant, rotation and mirror from
    /// its own coordinates - so the same map always gets the same patchwork,
    /// and a tape replay agrees with the run. Tile metres are the user's
    /// `--ground`; the count is capped, so the tile grows instead of the
    /// frame time exploding at a zoomed-out view.
    private const int GroundMaxTiles = 4000;
    /// How many tiles may be visible side by side at most. Below that number
    /// the user's --ground metres are kept (a real plot is ~40 m deep); above
    /// it the tile grows with the view, so a zoomed-out city shows whole
    /// blocks of greenery instead of a mosaic of individual gardens - a
    /// satellite-style map view, and the reason "too green and popping" (user,
    /// 2026-10-26) stopped at the overview rather than in the tiles.
    private const int GroundTilesAcross = 26;
    /// Zoom ramp (px/m) for the ground fade: at or above the first value the
    /// texture is untouched, below the second it is fully washed to the plain
    /// ground colour. Chosen around what a person actually reads as a city map.
    private const float GroundFadeAtPxPerM = 8.0f;
    private const float GroundFadeFullAtPxPerM = 2.2f;

    private string _roadFlat = "";             // --road-flat "a,b": types that keep the flat fill
    private readonly Dictionary<string, Texture2D> _roadTexCache = new();

    private Texture2D RoadTexture(string file)
    {
        if (_roadTexCache.ContainsKey(file)) { return _roadTexCache[file]; }
        var path = $"res://assets/textures/{file}.png";
        Texture2D tex = null;
        if (GD.Load<Texture2D>(path) is Texture2D t) { tex = t; }
        else
        {
            GD.Print("ROAD-MATERIAL '", file, "' is not in the project at ", path,
                " - that type falls back to the flat fill (generate with scripts/comfy_texture.py,",
                " then import with `Godot --headless --path . --import`)");
        }
        _roadTexCache[file] = tex;
        return tex;
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
    /// Paint fallback/cap, METRES: it mirrors the SIM's one paint width
    /// (Config.ROAD_PAINT_WIDTH_M - German markings are 0.10-0.12 m) so the
    /// client cannot invent a heavier line than the map declares. The payload's
    /// own widths are used where present; this value only caps the kerb line,
    /// which must never exceed the centre line (user decision 2026-10-26).
    const float PaintWidthM = (float)DrivingGame.Sim.Config.ROAD_PAINT_WIDTH_M;
    /// Narrowest paint width the current map declares (set while reading
    /// `lane_markings`, 9.9f = nothing seen).
    private float _narrowPaint = 9.9f;
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
        // DIAGNOSTIC (--test-pan-probe): what the handler is actually handed,
        // before anything can decline it.
        if (_testPanProbe && (e is InputEventMouseButton || e is InputEventMouseMotion))
        {
            string what = e is InputEventMouseButton mb0
                ? $"button {mb0.ButtonIndex} pressed={mb0.Pressed}"
                : $"motion mask={((InputEventMouseMotion)e).ButtonMask}";
            var pos = e is InputEventMouseButton mb1 ? mb1.Position
                    : ((InputEventMouseMotion)e).Position;
            var glb = e is InputEventMouseButton mb2 ? mb2.GlobalPosition
                    : ((InputEventMouseMotion)e).GlobalPosition;
            GD.Print($"PAN-INPUT {what} pos=({pos.X:F0},{pos.Y:F0}) " +
                     $"global=({glb.X:F0},{glb.Y:F0}) " +
                     $"tape={_review || _tapeLive} dragging={_dragging} stripDrag={_stripDrag} barTop={_barTop:F0}");
        }
        if (_review || _tapeLive) ReviewInput(e);
        if (_testPanProbe && e is not InputEventKey && _review)
            GD.Print($"PAN-INPUT   after ReviewInput: dragging={_dragging} " +
                     $"stripDrag={_stripDrag} pan=({_reviewPan.X:F1},{_reviewPan.Y:F1})");
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
                    if (_followUid.HasValue) { _followUid = null; _autoFollow = false; _tFocusManual = true; }
                    else if (_carNodes.Count > 0) _followUid = _carNodes.Keys.Min();
                }
                else if (k.Keycode == Key.T)     // the test window out of the way, and back
                {
                    if (_testInfoWin != null)
                    {
                        _testInfoWin.Visible = !_testInfoWin.Visible;
                        GD.Print($"TEST-INFO-{(_testInfoWin.Visible ? "SHOWN" : "HIDDEN")} (T key)");
                    }
                }
            }
        }
        else if (e is InputEventMouseButton mb)
        {
            if (_barAteWheel)
            {
                // Review mode: the bar already took this wheel notch for its own
                // time range. One event, one meaning.
                _barAteWheel = false;
            }
            else if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
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
                if (_testPanProbe)
                    GD.Print($"PAN-INPUT   left-button gate: y={mb.Position.Y:F0} vs barTop-6={_barTop - 6:F0} " +
                             $"stripDrag={_stripDrag} -> " +
                             ((_review && (mb.Position.Y > _barTop - 6.0 || _stripDrag))
                              ? "STRIP owns it, no grab" : "grab starts"));
                if ((_review || _tapeLive) && (mb.Position.Y > _barTop - 6.0 || _stripDrag))
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
                    // A hand that grabs the map has an opinion; from then on
                    // the run's declared focus does not re-aim the window
                    // (docs/TESTING.md "Camera focus"). --focus / --center set
                    // the same latch, so there is one rule: the operator wins.
                    if (mb.Pressed && !_review && !_tapeLive) _tFocusManual = true;
                    _dragStartMouse = mb.Position;
                    _dragStartCam = ClampViewCentre(_cam.GlobalPosition);
                    _dragStartPan = _reviewPan;
                    // IN REVIEW MODE a grab ANCHORS a pin, it does not just add
                    // an offset. Without the anchor the recording kept
                    // "FOLLOWING uid=22" while the pan pushed the view away from
                    // that car — the reported "the map follows the car, but the
                    // followed car is NOT in the center of the screen"
                    // (2026-10-24): the state said follow, the pixels said
                    // something else. A pin makes the hand honest — the view is
                    // the hand's for THIS scenario (src becomes "the hand (pin)")
                    // and the recording re-claims it at the next test boundary
                    // (FocusHandoff).
                    if (mb.Pressed && (_review || _tapeLive) && _mmAsk == null)
                    {
                        _mmAsk = _cam.GlobalPosition;
                        _reviewPan = Vector2.Zero;
                        _dragStartPan = Vector2.Zero;
                        GD.Print($"REVIEW-DRAG pinning at ({_mmAsk.Value.X:F1},{_mmAsk.Value.Y:F1}) " +
                                 $"— the hand owns this view until the next test " +
                                 $"(was: {_camSrc})");
                    }
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
            if (_review || _tapeLive)
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
    /// <summary>A scripted click on a control that lives in one of THIS
    /// client's satellite windows: the control's own rect is window-local, so
    /// the point is window origin + local centre. Probes drive the real input
    /// path this way — no private method is called, the button answers like a
    /// button.</summary>
    /// <summary>A scripted click on a control that lives in one of THIS
    /// client's satellite windows, driven through the real input path — a probe
    /// that calls a private method proves nothing about what a hand does.
    ///
    /// Two coordinates, both measured rather than reasoned about:
    ///   · `Window.Position` IS the client area's origin in the main window's
    ///     space; the chrome sits OUTSIDE it (`OuterRect` subtracts 8/32 to get
    ///     from one to the other). Adding the chrome "because a title bar is
    ///     32 px" puts every press 32 px too LOW — measured: a follow press
    ///     landed in the window's bottom frame and did nothing, while a row
    ///     press five rows up still hit a DIFFERENT row's button and the probe
    ///     reported a seek that had jumped somewhere else. A wrong answer that
    ///     looks green is the reason this is written down.
    ///   · the press goes to the ROOT viewport: a subwindow is a Viewport of
    ///     its own whose (0,0) is its client corner, so pushing main-window
    ///     numbers into `w.GetViewport()` puts the press outside that window
    ///     and no control ever sees it (this is what the minimap probe does).</summary>
    public void ClickInWindow(Window w, Control c, string tag)
    {
        var r = c.GetGlobalRect();
        var at = new Vector2(w.Position.X + r.Position.X + r.Size.X / 2f,
                             w.Position.Y + r.Position.Y + r.Size.Y / 2f);
        GD.Print($"{tag}: click at ({at.X:F0},{at.Y:F0}) " +
                 $"(window {w.Position} + local " +
                 $"({r.Position.X + r.Size.X / 2f:F0},{r.Position.Y + r.Size.Y / 2f:F0}) " +
                 $"· client {w.GetVisibleRect().Size})");
        GetViewport().PushInput(new InputEventMouseButton
        { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left,
          ButtonMask = MouseButtonMask.Left, Pressed = true });
        GetViewport().PushInput(new InputEventMouseButton
        { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left,
          ButtonMask = MouseButtonMask.Left, Pressed = false });
    }

    /// <summary>Absolute zoom (px/m), clamped like the wheel is. The declared
    /// per-test focus names a zoom, so it needs an absolute setter rather than
    /// a chain of relative notches.</summary>
    void SetZoom(double pxPerM)
    {
        if (pxPerM <= 0) return;
        float z = (float)Math.Clamp(pxPerM,
            Math.Max(ZoomMin, MinZoomOverMap()), ZoomMax);
        _cam.Zoom = new Vector2(z, z);
    }

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
        var perfT0 = Time.GetTicksMsec();
        // "save this information regularly, so that the window state can be
        // restored after a crash or when terminated externally" - the runner
        // does terminate this client from outside, so the memory may not hang
        // on the exit path alone.
        _winSaveAcc += delta;
        if (_winSaveAcc >= 0.5) { _winSaveAcc = 0; SaveWindowPlacement(); }

        // Embedded source (Phase 7): read the in-process snapshot every frame
        // and feed it through the SAME ingest path as the HTTP pipeline
        // (serialize + parse keeps state application single-sourced; the cost
        // is negligible vs an HTTP round trip - profiled at G5 via the EMBED
        // motionlog line).
        // Property of the SOURCE, not of a window: the day the live view reads
        // the tip of a growing tape, the tape owns the clock here too.
        // Property of the SOURCE, not of a window: a source that STATES a time
        // (a tape, file or tip) IS that time; the live `/state` stream has to be
        // ESTIMATED because samples land at network rate.
        _sourceOwnsClock = _review || _tapeLive;
        if (_review || _tapeLive)
        {
            if (_tapeLive)
                PullTapeTip();
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
        if (_perfProbe)
        {
            _perfAccMs += Time.GetTicksMsec() - perfT0;
            _perfFrameAccMs += delta * 1000.0;   // TRUE frame time incl. Godot's draw
            _perfFrameMaxMs = Math.Max(_perfFrameMaxMs, delta * 1000.0);
            _perfMaxMs = Math.Max(_perfMaxMs, Time.GetTicksMsec() - perfT0);
            _perfFrames += 1;
            var wall = Time.GetTicksMsec() / 1000.0;
            if (_perfWall0 == 0.0) { _perfWall0 = wall; }
            if (wall - _perfWall0 >= 2.0)
            {
                GD.Print($"PERF frames={_perfFrames} fps={_perfFrames / (wall - _perfWall0):F1} " +
                         $"avg_frame_ms={_perfFrameAccMs / _perfFrames:F3} max_frame_ms={_perfFrameMaxMs:F2} " +
                         $"avg_process_ms={_perfAccMs / _perfFrames:F2} wall_span={(wall - _perfWall0):F1} " +
                         $"world_nodes={_world.GetChildCount()} textured_polys={_texPolys} " +
                         $"wear={_wearCracks + _wearPatches}");
                _perfFrames = 0; _perfAccMs = 0; _perfMaxMs = 0; _perfWall0 = wall;
                _perfFrameAccMs = 0; _perfFrameMaxMs = 0;
            }
        }

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
            // Time went backwards => the sim restarted: wipe everything. Only
            // for a stream that OWNS the clock: a review window reads the live
            // /state beside its tape, and the live clock (a host started
            // minutes later, at t≈0) is not behind the tape's 98 s - it is a
            // different clock entirely. Without this guard the wipe ran every
            // frame and deleted the cars ReviewFeed had just fed (issue #1's
            // "reviewer beside a live host paints no cars").
            if (!(_review || _tapeLive) && t < _lastMaxTime - 0.5) ClearCars();
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
                    // The TYPE is `model`; `vehicle` is #10's transitional alias.
                    // `color` is NOT read here any more (#18): it carries the PAINT,
                    // so a client that still asked for the type under that word
                    // would turn a crimson car into a vehicle called "crimson".
                    m.Model = (c.TryGetProperty("model", out var cm)
                                 && cm.ValueKind == JsonValueKind.String ? cm
                                 : c.TryGetProperty("vehicle", out var cv)
                                 && cv.ValueKind == JsonValueKind.String ? cv
                                 : default).GetString() ?? "mercedes_c220";
                    // The PAINT (#18's word, with `livery` — #10's stand-in — still
                    // read): null/absent means the photographed colour, so a cast
                    // that declares nothing looks exactly as it always did.
                    m.Color = (c.TryGetProperty("color", out var cp)
                                 && cp.ValueKind == JsonValueKind.String ? cp
                                 : c.TryGetProperty("livery", out var liv)
                                 && liv.ValueKind == JsonValueKind.String ? liv
                                 : default).GetString() ?? "";
                    // The declared view options (issue #9): absent stays absent
                    // (null), so an unauthored cast keeps today's behaviour.
                    m.OptTracks = c.TryGetProperty("tyre_lines", out var tl) &&
                                  tl.ValueKind != JsonValueKind.Null ? tl.GetBoolean() : (bool?)null;
                    m.OptDetail = c.TryGetProperty("detail_window", out var dw) &&
                                  dw.ValueKind != JsonValueKind.Null ? dw.GetBoolean() : (bool?)null;
                    m.Driver = c.TryGetProperty("driver", out var drv)
                                && drv.ValueKind == JsonValueKind.String
                            ? drv.GetString() : "BICYCLE";
                    m.BlinkL = c.TryGetProperty("blinker_left", out var bl) && bl.GetBoolean();
                    m.BlinkR = c.TryGetProperty("blinker_right", out var br) && br.GetBoolean();
                    m.Hazard = c.TryGetProperty("hazard", out var hz) && hz.GetBoolean();
                    m.Braking = c.TryGetProperty("braking", out var bk2) && bk2.GetBoolean();
                    m.HeldBy = c.TryGetProperty("held_by", out var hbC) ? hbC.GetInt64() : 0L;
                    m.CapKey = c.TryGetProperty("cap_key", out var ckC) ? ckC.GetString() ?? "" : "";
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

            // WHAT THE RUNNING SCENARIO ASKS THE WINDOW TO LOOK AT
            // (POST /focus by the suite, exported here; docs/TESTING.md
            // "Camera focus"). Read once per state frame; acted on only when it
            // CHANGES, i.e. when a new test starts — the framing must not
            // re-assert itself every 16 ms and fight a hand on the mouse.
            if (root.TryGetProperty("focus", out var fo) &&
                fo.ValueKind == JsonValueKind.Object)
            {
                string Str(string k) => fo.TryGetProperty(k, out var v) &&
                    v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                double Dbl(string k) => fo.TryGetProperty(k, out var v) &&
                    v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;
                double DblN(string k, double d) => fo.TryGetProperty(k, out var v) &&
                    v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
                long Lng(string k) => fo.TryGetProperty(k, out var v) &&
                    v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0L;
                var f = (kind: Str("kind"), label: Str("label"),
                         scenario: Str("scenario"), name: Str("name"),
                         desc: Str("desc"),
                         x: Dbl("x"), y: Dbl("y"),
                         zoom: DblN("zoom", 0.0), uid: Lng("uid"),
                         nameUid: Lng("name_uid"));
                if (f.label != _tFocusLabel || f.desc != _tFocusDesc)
                {
                    _tFocusLabel = f.label;
                    _tFocusKind = f.kind; _tFocusName = f.name;
                    _tFocusDesc = f.desc;
                    _tFocusX = f.x; _tFocusY = f.y; _tFocusZoom = f.zoom; _tFocusUid = f.uid;
                    _tFocusNameUid = f.nameUid;
                    ApplyFocus();
                    TestInfoUpdate();      // the panel follows the test (see below)
                }
            }

            // The verdict of the CURRENT test, if the suite has filed one
            // (POST /test_result -> `test_results` in /state, also on the tape
            // as a `# result` note). Matched on the LABEL, not on the list
            // position: a full suite appends one note per scenario, and taking
            // the last entry while a later test is still running would show
            // another test's verdict as this one's state.
            _testVerdict = "";
            if (root.TryGetProperty("test_results", out var tr) &&
                tr.ValueKind == JsonValueKind.Array && _tFocusLabel.Length > 0)
                foreach (var r in tr.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    if (!(r.TryGetProperty("label", out var rl) &&
                          rl.ValueKind == JsonValueKind.String) ||
                        rl.GetString() != _tFocusLabel) continue;
                    bool pass = r.TryGetProperty("passed", out var pv) &&
                        pv.ValueKind == JsonValueKind.True;
                    string reason = r.TryGetProperty("reason", out var rr) &&
                        rr.ValueKind == JsonValueKind.String ? rr.GetString() : "";
                    double simS = r.TryGetProperty("sim_s", out var ss) &&
                        ss.ValueKind == JsonValueKind.Number ? ss.GetDouble() : 0.0;
                    int xng = r.TryGetProperty("crossings", out var xc) &&
                        xc.ValueKind == JsonValueKind.Number ? (int)xc.GetDouble() : 0;
                    int off = r.TryGetProperty("off_road", out var ofc) &&
                        ofc.ValueKind == JsonValueKind.Number ? (int)ofc.GetDouble() : 0;
                    _testVerdict = (pass ? "✅ passed" : "❌ failed: " + reason) +
                        $" at sim {simS:F1} s · wall {(r.TryGetProperty("wall_s", out var ws) && ws.ValueKind == JsonValueKind.Number ? ws.GetDouble() : 0.0):F1} s" +
                        $" · crossings {xng} · off-road {off}";
                    break;
                }

            TestInfoUpdate();   // every poll: the verdict can arrive while the focus stays
            UpdateDetailsLive();   // an open detail slot follows the live frames

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

            // Cars missing from this response are gone - for a LIVE surface.
            // A review window still polls /state (its readout and flags come
            // from there) but its cars belong to the TAPE, and this prune ran
            // AFTER ReviewFeed had filled the buffers: an idle live sim means
            // "no cars", so every recorded car was deleted in the same frame
            // it was fed - a reviewer beside any running host painted an
            // empty map. The tape owns the car buffers, so the live teardown
            // has no claim on them.
            if (!_review && !_tapeLive)
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
        // The courtesy cone is a _world child of THIS car: free it with the
        // car, or a despawned car leaves its warm cone painted forever.
        if (_courtesy.TryGetValue(uid, out var cone))
        { cone.QueueFree(); _courtesy.Remove(uid); }
        // … and its tyre lines (issue #9): the nodes are _world children, so
        // leaving them behind paints the tracks of a car that no longer exists.
        if (_tyreLineNodes.TryGetValue(uid, out var lines))
        {
            foreach (var track in lines)
                if (track != null && GodotObject.IsInstanceValid(track)) track.QueueFree();
            _tyreLineNodes.Remove(uid);
            _tyreLines.Remove(uid);
        }
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
        // NOTE: _detailDeclaredShown / _detailDismissed are deliberately NOT
        // cleared here — this runs on a map rebuild too, and forgetting the
        // operator's dismissal (or re-opening what is already open) is exactly
        // where the duplicate windows came from (#9). They reset with a new tape.
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
        string vehicle = _carMeta.TryGetValue(uid, out var meta) ? meta.Model : "blue";
        var tex = CarTexture(vehicle);
        var size = CarSizeOf(vehicle);
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
        // The livery material is set on the sprite, so keep the handle (and
        // re-take it whenever the node is built - a car that despawned and
        // respawned gets a fresh sprite).
        if (_carMeta.TryGetValue(uid, out var metaForSprite))
        {
            metaForSprite.Sprite = root.GetChild<Sprite2D>(0);
            metaForSprite.ColorApplied = " ";    // force the material to be re-applied
        }
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
        if (_sourceOwnsClock)
        {
            // Nothing to estimate: `_simNow` arrived stated by the source, so
            // the delay below is the only thing between it and the render
            // target. (Rate measurement and servo stay live-ingest business.)
            _lastFrameWall = nowWall;
        }
        else if (_simNow > double.NegativeInfinity)
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
            // The C# property assignment does NOT reach Godot's z_index here
            // (measured: a car node reports z_index 0 after `node.ZIndex = 10`,
            // so it sits at the road's own level and gets buried when the map
            // is rebuilt above it - the reviewer's "cars are invisible"). The
            // dynamic call is the binding that works in this build (same
            // pattern as Polygon2D's set_uv).
            node.Call("set_z_index", 10 + 20 * level);
            // The paint (issue #10): re-material only when the livery actually
            // changed, which is the common case of doing nothing at all.
            if (_carMeta.TryGetValue(kv.Key, out var metaLivery))
            {
                if (metaLivery.ColorApplied != metaLivery.Color ||
                    (metaLivery.Sprite != null && !GodotObject.IsInstanceValid(metaLivery.Sprite)))
                {
                    if (metaLivery.Sprite == null || !GodotObject.IsInstanceValid(metaLivery.Sprite))
                        metaLivery.Sprite = node.GetChild<Sprite2D>(0);
                    metaLivery.ColorApplied = metaLivery.Color;
                    metaLivery.Sprite.Material = PaintMaterial(metaLivery.Model, metaLivery.Color);
                }
            }

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
                    float noseLenM = CarSizeOf(meta.Model).L;
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
            // The dots are the cars the DATA speaks about, not whatever nodes
            // happen to exist: a car absent from the frame has nothing to
            // say about this moment, and its dot used to be painted from the
            // stale node (the same ghost that drew on the map).
            foreach (var kv in _carBuf)
            {
                var buf = kv.Value;
                if (buf.Count == 0) continue;
                var node = _carNodes.TryGetValue(kv.Key, out var nd) ? nd : null;
                Vector2 pos = node != null
                    ? node.GlobalPosition
                    : new Vector2(buf[^1].X, -buf[^1].Y);   // samples are sim metres, y north
                string photo = PhotoOf(_carMeta.TryGetValue(kv.Key, out var m) ? m.Model : "blue");
                _minimap.Cars.Add((pos, kv.Key == _followUid,
                                   CarColors.TryGetValue(photo, out var dot) ? dot : CarColors["blue"]));
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
                // The header used to read `tan (car)` - a PHOTO name plus a class
                // no part of the sim consults. A model answers for itself now (#18);
                // ModelFor resolves the old photo words, so a tape or a host that
                // still says `tan` shows what it actually is.
                var model = DrivingGame.Sim.Config.ModelFor(mSel?.Model);
                float kmh = 0f;
                if (_carBuf.TryGetValue(uid, out var bufSel) && bufSel.Count > 0)
                    kmh = bufSel[^1].SpeedKmh;
                // The speed row lives in its own Label (per-frame churn is
                // fine there). The TextEdit is only reset on a STRUCTURAL
                // change (new decision line, new car) - a full reset drops
                // the scroll position, so it must not happen more often.
                _infoSpeed.Text = $"speed   {kmh:F1} km/h";
                var sb = new System.Text.StringBuilder();
                sb.Append($"#{uid}   {model.Name}\n");
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
    /// <summary>Fire the live car paths from the script (`--test-car-menu`,
    /// `--test-car-slot`). The screen position is the camera's own transform,
    /// inverted: screen = (world − cam) × zoom + half the viewport.</summary>
    void DoTestCarHook()
    {
        if (_carBuf.Count == 0)
        {
            GD.Print("TEST-CAR no car on the map yet - nothing to pick " +
                     "(start a scenario with --run-test <n>)");
            return;
        }
        long uid = _selectedUid ?? _carBuf.Keys.Min();
        if (_testCarHook == 1)
        {
            var vp = GetViewport().GetVisibleRect().Size;
            var buf = _carBuf[uid];
            var fr = buf[^1];
            var scr = (W(fr.X, fr.Y) - _cam.GlobalPosition) * _cam.Zoom + vp / 2f;
            SelectCarAt(scr);       // the same entry point a click release uses
            return;
        }
        OpenDetailWindow(uid);
    }

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
            var size = CarSizeOf(m?.Model ?? "blue");
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
        // A click on a car offers what the reviewer's click offers: the MENU
        // that decides WHERE this car gets shown - an already open detail slot
        // or a new one (docs/CAR_DETAIL_WINDOW.md). The live viewer had only a
        // highlight, which is the asymmetry the user named on 2026-10-25
        // ("die Features, die wir sonst nur im Reviewerfenster haben: ein Auto
        // anklicken und über das Pop-up-Menü auswählen, dass es in den
        // Cardetails mitangezeigt wird").
        if (best.HasValue && !_review)
            OpenDetailMenu(screenPos, best.Value);
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
    /// <summary>The `--screenshot` one-shot: shoot after `--delay`, then quit
    /// itself. Armed by `Map ready` - AND by a ground that will never come: a
    /// NO-GROUND window is exactly the picture someone wants (its readout says
    /// why), and the contract of the one-shot is that it always ends the
    /// process. Measured without this: with an unbuildable map the shot never
    /// fired and the window sat open until the generation file was bumped.
    /// </summary>
    void ArmShotIfAsked()
    {
        if (_screenshotPath != null && !_testPan)
            GetTree().CreateTimer(_shotDelay).Timeout += () => SaveShot(_screenshotPath);
    }

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
        if (_review || _tapeLive) return;
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
        float z = _cam.Zoom.X;
        bool drift = _lastTrailZoom > 0 &&
                     Math.Abs(z - _lastTrailZoom) / _lastTrailZoom > 0.08f;
        // One signature for EVERY car that draws tracks (issue #9): watching the
        // followed car's tail alone meant a second tracked car never rebuilt and
        // its lines froze at the frame they appeared in.
        var sig = new System.Text.StringBuilder();
        if (_review || _tapeLive)
        {
            // A review rebuilds on every cursor move that a tracked car can see.
            sig.Append(_cursor);
            if (_cursor >= 0 && _cursor < _tape.Count)
                foreach (var cf in _tape[_cursor].Cars)
                    if (WantsTyreLines(cf.Uid)) sig.Append('#').Append(cf.Uid);
        }
        else
            foreach (var kv in _trails)
                if (kv.Value.Count > 0 && WantsTyreLines(kv.Key))
                    sig.Append(kv.Key).Append('#').Append(kv.Value.Count).Append(';');
        // A checkbox press must repaint at once, so the overrides are part of
        // what counts as "changed".
        foreach (var kv in _tyreOverride)
            sig.Append("o").Append(kv.Key).Append(kv.Value ? '1' : '0');
        string sigNow = sig.ToString();
        if (sigNow != _lastTrailSig || drift)
        {
            RebuildAllTyreLines();
            _lastTrailSig = sigNow;
            _lastTrailZoom = z;
        }

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

    // --- Tyre lines, per car (issue #9) -----------------------------------
    //
    // One entry per car whose wheel tracks are ON, holding the FOUR corner
    // colours individually: the default paints all four the same (the car's
    // livery, or a palette hue by uid), while the per-tyre scheme this file used
    // to hard-code for the single followed car survives as the debug mode it was
    // asked to keep - a lane-guard investigation wants FL/FR/RL/RR apart, an
    // operator watching three cars pass a junction wants one colour per car.
    private readonly Dictionary<long, Color[]> _tyreLines = new();
    private readonly Dictionary<long, Line2D[]> _tyreLineNodes = new();
    private string _lastTrailSig = "";

    /// <summary>The OPERATOR's per-car toggle (the checkbox in the car detail
    /// window, issue #9): it outranks what the test declared, for this window and
    /// session, in the live view and in a review alike. Deliberately NOT written
    /// to the tape — a decision about the picture is not a fact of the run, and
    /// someone reviewing the recording must see what the recording says.</summary>
    private readonly Dictionary<long, bool> _tyreOverride = new();

    void SetTyreLinesOverride(long uid, bool on)
    {
        _tyreOverride[uid] = on;
        GD.Print($"TYRE-LINES uid {uid} → {(on ? "on" : "off")} by the operator " +
                 "(outranks the test for this window; not recorded to the tape)");
    }

    /// <summary>How many track lines are on the map right now — the far end of
    /// the checkbox chain, read by the detail probe (issue #9).</summary>
    int TyreLineNodeCount()
    {
        int n = 0;
        foreach (var arr in _tyreLineNodes.Values)
            foreach (var l in arr)
                if (l != null && GodotObject.IsInstanceValid(l)) n++;
        return n;
    }

    /// <summary>Cars whose detail window the TEST asked for (issue #9). One open
    /// per car per cast: if the operator closes it again it stays closed — the
    /// declaration is an opening ask, not a leash. Cleared with the cast
    /// (ClearCars on a fresh /teleport) and with the tape (LoadTape).</summary>
    private readonly HashSet<long> _detailDeclaredShown = new();

    /// <summary>Cars whose auto-opened window the OPERATOR closed: the test's
    /// declaration is an opening ask, not a leash, so it does not come back
    /// (#9). Reset with a new tape, never with a map rebuild.</summary>
    private readonly HashSet<long> _detailDismissed = new();

    void ApplyDeclaredDetails(long uid, bool? declared)
    {
        if (declared != true || _detailDismissed.Contains(uid)) return;
        // Ask the truth, do not remember it: a remembered flag went stale when
        // BuildMap's ClearCars() ran AFTER the windows had opened (the /map
        // answer lands a second later than the first /state), and the next frames
        // opened a SECOND window per car - measured as four windows for two cars,
        // with DETAIL-AUTO logged twice each.
        foreach (var w in _details)
            if (w.Uid == uid && w.Showing) return;
        _detailDeclaredShown.Add(uid);
        OpenDetailWindow(uid);
        GD.Print($"DETAIL-AUTO #{uid} ({NameOf(uid)}) — the test declared detail_window");
    }

    /// <summary>The four-corner scheme of the old single-car view, kept as the
    /// debug mode issue #9 asked to preserve.</summary>
    static readonly Color[] CornerDebugColors = {
        new Color(1f, 0.314f, 0.314f),    // FL red     (255,80,80)
        new Color(0.314f, 1f, 0.314f),    // FR green   (80,255,80)
        new Color(0.314f, 0.549f, 1f),    // RL blue    (80,140,255)
        new Color(1f, 0.863f, 0f),        // RR yellow  (255,220,0)
    };

    /// <summary>Twelve well-separated hues, assigned by uid and cycling. A fixed
    /// list (repeatable from run to run, greppable in a screenshot) beats a hue
    /// wheel whose colours move when the cast grows.</summary>
    static readonly Color[] TrackPalette = {
        new(1f, 0.31f, 0.31f), new(0.35f, 0.85f, 1f), new(1f, 0.84f, 0.25f),
        new(0.55f, 1f, 0.55f), new(1f, 0.55f, 0.9f),  new(0.7f, 0.6f, 1f),
        new(1f, 0.62f, 0.3f),  new(0.4f, 0.95f, 0.85f), new(1f, 0.95f, 0.5f),
        new(0.65f, 1f, 0.75f), new(1f, 0.45f, 0.6f),  new(0.55f, 0.8f, 1f),
    };

    /// <summary>Does this car draw its tyre lines? A declaration wins - the live
    /// window reads `tyre_lines` from /state, a review reads `tracks=` off the
    /// tape header - and an explicit false turns even the followed car off.
    /// Undeclared keeps the standing behaviour of every run recorded so far: the
    /// followed car only.</summary>
    bool WantsTyreLines(long uid)
    {
        // The operator's click is the loudest voice (issue #9).
        if (_tyreOverride.TryGetValue(uid, out var forced)) return forced;
        if (_review || _tapeLive)
        {
            // In a review the TAPE decides, and /state never does: the window
            // still polls the live host (its readout and flags come from there),
            // but those cars belong to a different run and must not override what
            // this recording says (issue #9).
            var o = TapeOptionAt(uid);        // staged declaration + changes up
            if (o.Tracks is not null)         // to this substep (#10)
                return o.Tracks == true;
            // Standing rule: the PRIMARY car of the frame under the cursor - the
            // same car the reviewer's default focus names ("focus: the primary
            // car"), and the counterpart of the followed car in the live window.
            // (_followUid is the live camera's field; asking it here drew no
            // tracks at all - measured on a reviewed turn.)
            if (_cursor >= 0 && _cursor < _tape.Count && _tape[_cursor].Cars.Count > 0)
                return uid == _tape[_cursor].Cars[0].Uid;
            return false;
        }
        if (_carMeta.TryGetValue(uid, out var m) && m.OptTracks is not null)
            return m.OptTracks == true;
        return _followUid > 0 && _followUid.Value == uid;
    }

    /// <summary>The track colour of one car: its livery's hue, pushed to a
    /// lightness that reads on BOTH asphalt (mid grey) and grass (mid green) - a
    /// track is only useful if it survives the surface it lies on - else a
    /// palette hue by uid. `cornerDebug` returns the four-corner scheme.</summary>
    static Color[] TyreLineColors(long uid, CarMeta meta, bool cornerDebug = false)
    {
        if (cornerDebug) return CornerDebugColors;
        Color c;
        if (meta != null && !string.IsNullOrEmpty(meta.Color) &&
            meta.Color.Length == 7 && meta.Color[0] == '#')
        {
            c = new Color(meta.Color);
            // Keep the hue, leave the mid greys: the paint itself can be exactly
            // the colour of the road.
            float v = Math.Max(c.R, Math.Max(c.G, c.B));
            float mn = Math.Min(c.R, Math.Min(c.G, c.B));
            if (v > 0.34f && v < 0.72f && v - mn < 0.3f)
                c = v < 0.53f ? c.Lightened(0.45f) : c.Darkened(0.45f);
        }
        else
            c = TrackPalette[((uid - 1) % TrackPalette.Length + TrackPalette.Length)
                             % TrackPalette.Length];
        return new[] { c, c, c, c };
    }

    /// <summary>The four wheel tracks of one recorded path: rear-axle points
    /// become front tyres one wheelbase ahead and each pair `TireOutboardM`
    /// outboard (the geometry the single-car view already had).</summary>
    static Vector2[][] CarWheelTracks(List<(float X, float Y, float Hdg)> trail)
    {
        // The car renders at latest_time - RenderDelaySec (100 ms), but the
        // trail's newest point is "now" - without dropping it the trail tip
        // pokes out AHEAD of the delayed sprite. One point is exactly one 0.1 s
        // recording interval, matching the delay.
        int nPts = trail.Count - 1;
        if (nPts < 1) return null;
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
        var tracks = new Vector2[4][];
        for (int i = 0; i < 4; i++) tracks[i] = lists[i].ToArray();
        return tracks;
    }

    /// <summary>A review track is the path UP TO the cursor and no further: a
    /// line reaching into frames after the one under the cursor would draw a
    /// manoeuvre that has not happened yet. Stepping forward appends (the cheap
    /// common case, ▶ play), anything else — a scrub, a seek, the jump into the
    /// next test — rebuilds from the tape, bounded to <see cref="TrailMaxPoints"/>
    /// samples so a 20-minute run cannot cost a rebuild of its whole history.
    /// The live window never comes here: its history arrives with /state.
    /// </summary>
    List<(float X, float Y, float Hdg)> ReviewTrackOf(long uid)
    {
        const int EVERY = 6;    // 0.1 s of tape at 60 Hz == TrailIntervalSec
        if (_reviewTrailCursor != _cursor || !_reviewTrails.TryGetValue(uid, out var keep))
            keep = null;
        if (keep != null && _cursor > 0 && _reviewTrailCursor == _cursor - 1)
        {
            foreach (var cf in _tape[_cursor].Cars)
                if (cf.Uid == uid)
                {
                    keep.Add(((float)cf.X, (float)cf.Y, (float)cf.HeadingDeg));
                    if (keep.Count > TrailMaxPoints) keep.RemoveAt(0);
                    break;
                }
            return keep;
        }
        var list = new List<(float X, float Y, float Hdg)>();
        for (int i = _cursor; i >= 0 && list.Count < TrailMaxPoints; i -= EVERY)
        {
            foreach (var cf in _tape[i].Cars)
                if (cf.Uid == uid)
                {
                    list.Add(((float)cf.X, (float)cf.Y, (float)cf.HeadingDeg));
                    break;
                }
        }
        list.Reverse();
        _reviewTrails[uid] = list;
        return list;
    }

    private readonly Dictionary<long, List<(float X, float Y, float Hdg)>> _reviewTrails = new();
    private int _reviewTrailCursor = -1;

    /// <summary>Every car that wants them gets its own four track lines, in its
    /// own colour - which is the whole of issue #9's drawing half.</summary>
    void RebuildAllTyreLines()
    {
        // Which cars are on screen for THIS picture: the live buffers, or the
        // frame under the cursor in a review.
        var uids = new List<long>();
        if (_review || _tapeLive)
        {
            if (_cursor >= 0 && _cursor < _tape.Count)
                foreach (var cf in _tape[_cursor].Cars) uids.Add(cf.Uid);
        }
        else
            foreach (var uid in _trails.Keys) uids.Add(uid);

        foreach (var uid in _tyreLineNodes.Keys.ToArray())
        {
            if (uids.Contains(uid) && WantsTyreLines(uid)) continue;
            foreach (var n in _tyreLineNodes[uid])
                if (n != null && GodotObject.IsInstanceValid(n)) n.QueueFree();
            _tyreLineNodes.Remove(uid);
            _tyreLines.Remove(uid);
        }
        foreach (var uid in uids)
        {
            if (!WantsTyreLines(uid)) continue;
            var trail = (_review || _tapeLive) ? ReviewTrackOf(uid)
                          : (_trails.TryGetValue(uid, out var t) ? t : null);
            if (trail == null) continue;
            var tracks = CarWheelTracks(trail);
            if (tracks == null) continue;
            _carMeta.TryGetValue(uid, out var meta);
            var colors = TyreLineColors(uid, meta);
            if (!_tyreLineNodes.TryGetValue(uid, out var nodes))
            {
                nodes = new Line2D[4];
                _tyreLineNodes[uid] = nodes;
            }
            _tyreLines[uid] = colors;
            for (int i = 0; i < 4; i++)
            {
                var line = nodes[i];
                if (line == null || !GodotObject.IsInstanceValid(line))
                {
                    // ZIndex below the car sprite (10) and above the road paint
                    // (0-2): a track is on the road, under the car.
                    line = new Line2D { DefaultColor = colors[i], Closed = false, ZIndex = 5 };
                    _world.AddChild(line);
                    nodes[i] = line;
                }
                // Update in place (a track grows ~10x/s; recreating the node
                // every frame double-frees it - QueueFree is deferred).
                line.Call("set_z_index", 5);
                line.DefaultColor = colors[i];
                line.Points = tracks[i];
                line.Width = TrailWidthM;   // a tyre track is paint, not a screen px
            }
        }
        // The review caches belong to THIS cursor: remember it so the next frame
        // can tell "one step on" (append) from "moved elsewhere" (rebuild).
        if (_review || _tapeLive) _reviewTrailCursor = _cursor;
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
        // The pennant's FOOT IS the arrival mark: it is drawn AT the point the
        // sim stated (`SimEngine.cs` FlagRed: the centre of the line the car
        // must reach - the driving line for a running test, the parked car's
        // front-bumper line for the parking scenario). It used to be pushed 3 m
        // (apex 5.2 m) to the right of that point ON TOP of the sim's own
        // kerb offset, so the flag stood ~7 m off the centreline - in the grass
        // beside the road - while the suite's arrival test measured a point in
        // the LANE. Picture and criterion are now the same place (docs/TESTING.md
        // §3, the user's decision of 2026-10-25: the red flag goes into the
        // lane the car has to arrive in, and driving against it ends the test).
        var b = W(x, y);
        var p1 = W(x + fx * 0.9f, y + fy * 0.9f);
        var p2 = W(x - fx * 0.9f, y - fy * 0.9f);
        var ap = W(x + rx * 2.4f, y + ry * 2.4f);           // cloth leans right
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

    /// <summary>Re-aim the window for the scenario that just started
    /// (docs/TESTING.md "Camera focus"). Called ONCE per new focus declaration,
    /// so a run of 26 scenarios moves the camera 26 times and never again — a
    /// per-frame re-assert would fight every hand that touches the mouse.</summary>
    void ApplyFocus()
    {
        if (_review || _tapeLive || _tFocusManual) return;      // the hand outranks the run
        if (_tFocusKind == "loc")
        {
            // A PLACE is the subject: no car is followed at all. Same effect as
            // starting the client with --center, but arriving mid-run, when the
            // crossing test begins.
            _autoFollow = false;
            _followUid = null;
            _cam.GlobalPosition = ClampViewCentre(W((float)_tFocusX, (float)_tFocusY), "focus loc");
            if (_tFocusZoom > 0) SetZoom(_tFocusZoom);
            GD.Print($"FOCUS test '{_tFocusLabel}': the PLACE ({_tFocusX:F1},{_tFocusY:F1}) m" +
                     $"{(_tFocusZoom > 0 ? $" @ {_tFocusZoom:F1} px/m" : "")} - no car followed");
            return;
        }
        // A CAR is the subject. "car" = the primary one (the followed car /state
        // describes); "named" = the car the scenario gave that name to.
        // A named subject: the uid the scenario STAGED (name_uid) beats looking
        // the name up in the cast — roles are reused, so the name can be carried
        // by a different car by the time the view is aimed.
        long? uid = _tFocusKind == "named"
            ? (_tFocusNameUid > 0 ? _tFocusNameUid : UidByName(_tFocusName))
            : _tFocusUid;
        if (uid is null or 0)
            uid = _followUid;      // named car not spawned yet: the primary, and
                                   // the next state frame retries the binding
        if (uid is null or 0) return;
        _autoFollow = true;
        _followUid = uid;
        _lastManualMs = 0;         // no "manual wins" grace period to wait out
        if (_tFocusZoom > 0) SetZoom(_tFocusZoom);
        else FitViewOn(_tFocusX, _tFocusY);       // "fit the primary car"
        GD.Print($"FOCUS test '{_tFocusLabel}': {_tFocusKind} uid={uid} " +
                 $"@ {_cam.Zoom.X:F1} px/m");
    }

    /// <summary>The uid of the car whose HUD name is `name` — the scenario's own
    /// cast list: POST /teleport {"name": …} → TestFlags.HudLabel → /state.</summary>
    long? UidByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var kv in _carMeta)
            if (kv.Value.HudLabel == name) return kv.Key;
        return null;
    }

    /// <summary>Zoom so the view is <see cref="CarFocusViewM"/> metres across —
    /// "fit the primary car" (user decision 2026-10-24). Computed from the
    /// WINDOW's pixel width, so maximising keeps the same physical framing
    /// instead of quietly showing more country.</summary>
    void FitViewOn(double xM, double yM)
    {
        var vp = GetTree().Root.GetVisibleRect().Size;
        double z = Math.Clamp(vp.X / CarFocusViewM, ZoomMin, CrashZoom);
        SetZoom(z);
        if (xM != 0.0 || yM != 0.0)
            _cam.GlobalPosition = ClampViewCentre(W((float)xM, (float)yM), "focus fit");
    }

    /// <summary>Car-framing width in metres — the one number to argue about.
    /// 120 m is ~27 px/m in a 3200 px window and ~13 px/m in a 1600 px one,
    /// i.e. the framing the follow camera has always had, made explicit.</summary>
    internal const double CarFocusViewM = 120.0;

    /// <summary>The motion log is a buffered writer: a run that ends through
    /// the generation-file quit (not through --screenshot) would lose every
    /// frame still in the buffer — which, for a trace, is the whole tail.
    /// Disposing here flushes it on EVERY clean exit path.</summary>
    /// <summary>Open where the window was last closed (the pattern of
    /// docs/creating_applications "Remembering window position and size across
    /// sessions", which Godot does not do for you): `user://window.ini` holds
    /// screen/position/size, this reads them and states the rect ONCE, before
    /// the first frame. Three escape hatches, in this order of rank:
    /// `--window x y w h` (explicit), `--window-default` (ignore the memory),
    /// and a stored rect that no longer touches any connected screen - a
    /// monitor that has been unplugged since must not leave the window
    /// unreachable, so that falls back to the default too. The default is the
    /// geometry the user asked for on 2026-10-25: 2000x1000 at (100, 100).</summary>
    void RestoreWindowPlacement()
    {
        var w = GetWindow();
        if (w == null) return;
        var def = new Rect2I(100, 100, 2000, 1000);
        var rect = def;
        _winPlacedBy = "default";
        var cfg = new ConfigFile();
        int screen = -1;
        if (!_windowNoRestore && cfg.Load(WinCfgPath) == Error.Ok)
        {
            screen = (int)cfg.GetValue("main", "screen", -1);
            var pos = (Vector2I)cfg.GetValue("main", "position", Vector2I.Zero);
            var size = (Vector2I)cfg.GetValue("main", "size", Vector2I.Zero);
            var stored = new Rect2I(pos, size);
            if (size.X >= 320 && size.Y >= 200 && TouchesAScreen(stored))
            {
                rect = stored;
                _winPlacedBy = "remembered";
                if (screen >= 0 && screen < DisplayServer.GetScreenCount())
                    w.CurrentScreen = screen;    // the monitor it was closed on
            }
            else
            {
                var scr = "";
                for (int i = 0; i < DisplayServer.GetScreenCount(); i++)
                    scr += $" {i}={DisplayServer.ScreenGetUsableRect(i)}";
                GD.Print($"WINDOW-PLACEMENT stored {stored} is off every screen({scr}) " +
                         "- using the default");
            }
        }
        if (_windowAsk.Size.X >= 320 && _windowAsk.Size.Y >= 200)
            rect = _windowAsk;          // an explicit rect outranks the memory
        PlaceWindow(rect, _windowAsk.Size.X >= 320 ? "--window" : _winPlacedBy);
    }

    /// <summary>State the rect on the main window ONCE. Size first, position
    /// last: a resize re-anchors on some window managers and would otherwise
    /// undo the place. Every path (memory, `--window`, the default) ends here,
    /// so the log line always says WHICH one ruled.</summary>
    void PlaceWindow(Rect2I r, string by)
    {
        _winPlacedBy = by;
        var w = GetWindow();
        w.Size = r.Size;
        w.Position = r.Position;
        _winSavedRect = new Rect2I(w.Position, w.Size);   // what the file already says
        GD.Print($"WINDOW-PLACEMENT {by}: asked pos {r.Position} size {r.Size} " +
                 $"-> got {w.Position} {w.Size} screen {w.CurrentScreen} " +
                 $"(cfg {ProjectSettings.GlobalizePath(WinCfgPath)})");
    }

    /// <summary>True when a rect would still be reachable: it must overlap one
    /// connected screen by more than a sliver, otherwise the title bar - the
    /// only handle to drag it back - can sit on a display that is gone.</summary>
    static bool TouchesAScreen(Rect2I r)
    {
        for (int i = 0; i < DisplayServer.GetScreenCount(); i++)
        {
            var s = DisplayServer.ScreenGetUsableRect(i);   // the honest area: no menu bar, no Dock
            var ov = s.Intersection(r);
            if (ov.Size.X >= 200 && ov.Size.Y >= 120) return true;
        }
        return false;
    }

    /// <summary>Persist the geometry to open at next time. Called from the
    /// periodic tick AND from `_ExitTree`, because the docs' own note is right:
    /// a quit that comes from outside (the runner's cleanup, a crash) never runs
    /// the exit path, so saving only there would lose the last position. Skips
    /// a minimized or absurd window so a weird state cannot poison the memory.</summary>
    void SaveWindowPlacement()
    {
        var w = GetWindow();
        if (w == null || w.Mode == Window.ModeEnum.Minimized) return;
        if (w.Size.X < 320 || w.Size.Y < 200) return;
        var now = new Rect2I(w.Position, w.Size);
        if (now == _winSavedRect) return;           // a 0.5 s poll must not hammer the disk
        var cfg = new ConfigFile();
        cfg.Load(WinCfgPath);                       // keep any other section
        cfg.SetValue("main", "screen", w.CurrentScreen);
        cfg.SetValue("main", "position", w.Position);
        cfg.SetValue("main", "size", w.Size);
        cfg.Save(WinCfgPath);
        _winSavedRect = now;
        if (_winSaveDbg++ < 12 || _winSaveDbg % 20 == 0)
            GD.Print($"WINDOW-PLACEMENT saved #{_winSaveDbg}: pos {w.Position} size {w.Size} " +
                     $"screen {w.CurrentScreen} -> {WinCfgPath}");
    }


    public override void _ExitTree()
    {
        SaveWindowPlacement();      // the geometry of THIS close is next run's opening
        if (_motionLog != null)
        {
            _motionLog.Dispose();
            _motionLog = null;
        }
        base._ExitTree();
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

    // ---------------- the run controls, inside the test window ----------------

    /// <summary>The run row of the test window (the user's decision of
    /// 2026-10-25: "integriere das Test-Panel unten mit in das Test-Fenster,
    /// das die Ergebnisse anzeigt"). It used to sit bottom-left ON THE MAP:
    /// a place that (a) is not where you read the verdict, so starting a test
    /// and reading its result were two different corners of the screen, and
    /// (b) was positioned from the viewport size at build time, so it drifted
    /// out of place on a resize. Here it is part of a window the user drags
    /// and that is laid out in ITS OWN coordinates - the map underneath stays
    /// the map. Children go into the window's layer, not into `AddChild`.</summary>
    void BuildRunTestUi(CanvasLayer into)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(20 / 255f, 20 / 255f, 20 / 255f, 180f / 255f),
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        // A DROPDOWN of the scenarios instead of a number field (the user's
        // decision of 2026-10-25: "statt Eingabe einer Testnummer lieber eine
        // Dropdown-Box mit allen verfügbaren Tests, jeweils mit der Nummer
        // vorangestellt, und daneben der Run-Button"): typing "22" means the
        // list is already known, and a wrong number only fails afterwards. The
        // entries come from GET /tests, prefixed with their number, so the
        // dropdown IS the scenario list the docs keep generated.
        _testPick = new OptionButton
        {
            Position = new Vector2(16, 236),
            Size = new Vector2(700, 44),
        };
        _testPick.AddThemeFontSizeOverride("font_size", 22);
        // Clip inside the box: without this a long scenario description drew
        // PAST the control's width and ran under the Run button next to it
        // (visible in the 2026-10-25 screenshot of test #20).
        _testPick.ClipText = true;
        into.AddChild(_testPick);
        _runTestBtn = new Button
        {
            Text = "Run",
            Position = new Vector2(728, 236),
            Size = new Vector2(136, 44),
        };
        _runTestBtn.AddThemeFontSizeOverride("font_size", 22);
        _runTestBtn.Pressed += RunTestFromUi;
        into.AddChild(_runTestBtn);
        _testPick.ItemSelected += _ =>
            GD.Print($"TEST-PICK #{(_testPick.Selected >= 0 && _testPick.Selected < _testPickNums.Count
                ? _testPickNums[_testPick.Selected] : 0)} {_testPick.GetItemText(_testPick.Selected)}");
        var kb = new Label { Text = "list: GET /tests · T hides/shows this window" };
        kb.Position = new Vector2(16, 292);
        kb.AddThemeFontSizeOverride("font_size", 18);
        kb.AddThemeColorOverride("font_color", new Color(150 / 255f, 150 / 255f, 150 / 255f));
        into.AddChild(kb);
        // The run-status line (what `SetRunStatus` writes): the manual run's own
        // feedback - start messages, errors - next to the verdict it belongs to.
        _runStatus = new Label();
        _runStatus.Position = new Vector2(16, 330);
        _runStatus.Size = new Vector2(848, 36);
        _runStatus.AutowrapMode = TextServer.AutowrapMode.Off;
        // A finished run names its log file, and that path is longer than the
        // window: elide the tail instead of cutting a word in half at the edge.
        _runStatus.TextOverrunBehavior = TextServer.OverrunBehavior.TrimWordEllipsis;
        _runStatus.AddThemeStyleboxOverride("normal", sb);
        _runStatus.AddThemeColorOverride("font_color", new Color(1f, 1f, 0f));
        _runStatus.AddThemeFontSizeOverride("font_size", 20);
        _runStatus.Visible = false;       // no message yet - see SetRunStatus
        into.AddChild(_runStatus);
    }

    /// <summary>Fetch the scenario list once and put it in the dropdown -
    /// `<number> · <description>`, number first, in the suite's own order, so
    /// the window and `GET /tests` can never disagree about what test 22 is.
    /// Its own HttpRequest: `_cmdHttp` is single-flight and must stay free for
    /// the run/poll traffic. Bounded retries, because a client started a
    /// moment before its host answers would otherwise show an empty list
    /// forever - and an empty dropdown is the kind of failure nobody reads as
    /// "not loaded yet" instead of "there are no tests".</summary>
    void RequestTestList()
    {
        if (_testsHttp == null)
        {
            _testsHttp = new HttpRequest { UseThreads = true, Timeout = 5.0 };
            AddChild(_testsHttp);
            _testsHttp.RequestCompleted += (id, code, hdr, body) => OnTestListResponse(code, body);
        }
        _testsTries++;
        _testsHttp.Request($"http://127.0.0.1:{_apiPort}/tests");
    }

    void OnTestListResponse(long code, byte[] body)
    {
        if (code != 200)
        {
            if (_testsTries < 8)
                GetTree().CreateTimer(2.0).Timeout += RequestTestList;
            else
                SetRunStatus($"test list unavailable: GET /tests answered {code}", ok: false);
            return;
        }
        int added = 0;
        try
        {
            using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(body));
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                int num = t.TryGetProperty("number", out var nEl) ? nEl.GetInt32() : 0;
                string desc = t.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
                string key = t.TryGetProperty("key", out var kEl) ? kEl.GetString() ?? "" : "";
                if (num < 1) continue;
                _testPick.AddItem($"{num} \u00b7 {(desc.Length > 0 ? desc : key)}");
                _testPickNums.Add(num);
                added++;
            }
        }
        catch (JsonException e)
        {
            SetRunStatus($"test list unreadable: {e.Message}", ok: false);
            return;
        }
        if (added == 0)
        {
            if (_testsTries < 8) GetTree().CreateTimer(2.0).Timeout += RequestTestList;
            return;
        }
        if (_testPick.Selected < 0) _testPick.Selected = 0;
        // The popup inherits the button's font only for its own label; the rows
        // need the size on the popup itself (and it exists once in the tree).
        _testPick.GetPopup().AddThemeFontSizeOverride("font_size", 21);
        if (_autoRunTest > 0) SelectTestInList(_autoRunTest);
        GD.Print($"TEST-LIST {added} scenarios from GET /tests, first=" +
                 _testPick.GetItemText(0));
    }

    /// <summary>Move the dropdown to a given test number (`--run-test`, or any
    /// other path that starts a test without the dropdown being the source).</summary>
    void SelectTestInList(int n)
    {
        int i = _testPickNums.IndexOf(n);
        if (i >= 0) _testPick.Selected = i;
    }

    /// <summary>The test-info surface as a real, MOVABLE window (docs/TESTING.md
    /// §5, the user's decision of 2026-10-25: "make the window a window like the
    /// car detail window - moveable, bigger fonts, more structured, not text-only").
    /// Title bar = the test's own identity, body = rows (purpose / watch / state)
    /// plus a colour CHIP that answers at a glance (amber running, green passed,
    /// red failed) - a sentence alone is text-only and needs reading. `RegisterWindow`
    /// makes the shared `EnforceWindows` guard apply, so the box is draggable AND
    /// can never overlap the minimap or the readout (the readout window in
    /// MapRenderer.review.cs uses the same pattern, including the fact that a
    /// subwindow is a Viewport and its children sit in ITS coordinates).
    /// Always visible: it carries the run controls, so a drive WITHOUT a test
    /// is exactly the case where you need it (its info rows then read `idle`).
    /// `T` hides it again if it is in the way - `--run-test` and a new focus
    /// bring it back.</summary>
    void BuildTestInfo()
    {
        if (_testInfoWin != null) return;
        _testInfoWin = new Window
        {
            Title = "test",
            Transient = true,               // belongs to this client, not to the app
            Position = new Vector2I(13, 37),
            // Wide enough for the scenario list at a readable size, and tall
            // enough for the rows + the run row (the user's "die Fonts sind zu
            // klein" of 2026-10-25 is why the type grew from 19 to 24 px).
            Size = new Vector2I(880, 396),
        };
        AddChild(_testInfoWin);
        RegisterWindow(_testInfoWin);
        var tLayer = new CanvasLayer { Layer = 1 };
        _testInfoWin.AddChild(tLayer);
        _testBg = new ColorRect
        {
            Color = new Color(14 / 255f, 16 / 255f, 22 / 255f, 250f / 255f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _testBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        tLayer.AddChild(_testBg);
        // The state chip: a colour answers faster than the sentence does.
        _testChip = new ColorRect
        {
            Position = new Vector2(16, 122),
            Size = new Vector2(44, 44),
        };
        tLayer.AddChild(_testChip);
        _testChipIcon = new Label { Text = "\u25b6" };
        _testChipIcon.Position = new Vector2(24, 118);
        _testChipIcon.AddThemeFontSizeOverride("font_size", 32);
        tLayer.AddChild(_testChipIcon);
        _testPurpose = new Label
        {
            // A scenario description that does not fit the width loses its
            // tail with "…" rather than running under the window frame.
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimWordEllipsis,
        };
        _testWatch = new Label();
        _testState = new Label();
        _testNums = new Label();
        foreach (var l in new[] { _testPurpose, _testWatch, _testState, _testNums })
        {
            l.AutowrapMode = TextServer.AutowrapMode.Off;
            // A long scenario name or a long failure reason must not run under
            // the window frame - the row loses its tail with "…" instead.
            l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimWordEllipsis;
            l.AddThemeColorOverride("font_color", new Color(235 / 255f, 235 / 255f, 235 / 255f));
            l.AddThemeFontSizeOverride("font_size", 24);
        }
        // A WIDTH, not just a position: `TextOverrunBehavior` trims to the
        // label's box, and an auto-sized label has no box yet - with the trim
        // mode on and no width the four rows rendered COMPLETELY EMPTY (the log
        // still printed their text, so only the screenshot caught it).
        _testPurpose.Position = new Vector2(16, 14);  _testPurpose.Size = new Vector2(848, 34);
        _testWatch.Position = new Vector2(16, 62);    _testWatch.Size = new Vector2(848, 32);
        _testState.Position = new Vector2(76, 124);   _testState.Size = new Vector2(788, 36);
        _testNums.Position = new Vector2(16, 186);    _testNums.Size = new Vector2(848, 30);
        _testNums.AddThemeFontSizeOverride("font_size", 21);
        _testNums.AddThemeColorOverride("font_color", new Color(190 / 255f, 190 / 255f, 190 / 255f));
        tLayer.AddChild(_testPurpose);
        tLayer.AddChild(_testWatch);
        tLayer.AddChild(_testState);
        tLayer.AddChild(_testNums);
        BuildRunTestUi(tLayer);       // the run row lives here now
        RequestTestList();            // fill the dropdown from GET /tests
        _testInfoWin.CloseRequested += () =>
        {
            _testInfoWin.Visible = false;
            GD.Print("TEST-INFO-CLOSED the current test is off screen · the next focus re-opens it");
        };
    }

    /// <summary>Repaint the window from the current focus + the filed verdict.
    /// Runs on EVERY ingest, not only when the focus changes: the suite files its
    /// verdict on a LATER poll than the focus that started the test, and a window
    /// still saying `running` once that verdict exists is the lie this surface
    /// prevents - it is what made a finished test look alive on screen while the
    /// car drove on past the flag (docs/TESTING.md §3: passing the red flag ENDS
    /// a running test, the car is not asked to stop).</summary>
    void TestInfoUpdate()
    {
        if (_testInfoWin == null) return;
        if (!_testInfoWin.Visible)
            _testInfoWin.Visible = true;
        if (_tFocusLabel.Length == 0)
        {
            // No test context: a plain drive, or a manual run whose focus the
            // suite has not announced yet. The window keeps its controls and
            // says what it knows - grey chip, `idle`, and where the list is.
            _testInfoWin.Title = "test";
            _testPurpose.Text = "no test running";
            _testWatch.Text = "start one below · the list: GET /tests";
            _testChip.Color = new Color(0.42f, 0.45f, 0.5f);
            _testChipIcon.Text = "\u00b7";
            _testState.Text = "idle";
            _testNums.Text = "";
            return;
        }
        _testInfoWin.Title = $"test {_tFocusLabel}";
        _testPurpose.Text = _tFocusDesc.Length > 0 ? _tFocusDesc : "-";
        _testWatch.Text = _tFocusKind == "loc" ? "watch: this PLACE"
            : _tFocusKind == "named" ? $"watch: {_tFocusName}" : "watch: the car";
        var verdict = _testVerdict;
        if (verdict.Length == 0)
        {
            _testChip.Color = new Color(1f, 0.8f, 0.2f);           // amber: running
            _testChipIcon.Text = "\u25b6";
            _testState.Text = "running";
            _testNums.Text = "";
        }
        else
        {
            var bits = verdict.Split(" · ");
            var head = bits[0];
            bool passed = head.StartsWith("\u2705") || head.StartsWith("✅");
            _testChip.Color = passed ? new Color(0.2f, 0.8f, 0.3f)
                                     : new Color(0.85f, 0.2f, 0.2f);
            _testChipIcon.Text = passed ? "\u2713" : "\u2717";
            _testState.Text = passed ? "passed"
                : head.Replace("❌ failed: ", "").Replace("\u274c failed: ", "");
            _testNums.Text = bits.Length > 1
                ? string.Join(" · ", bits, 1, bits.Length - 1) : "";
        }
        if (_testDbg < 6 || (verdict.Length > 0 && _testDbg2 < 2))
        {
            if (verdict.Length > 0) _testDbg2++; else _testDbg++;
            GD.Print($"TEST-INFO '{_tFocusLabel}': title={_testInfoWin.Title} | purpose=" +
                     // the row's own text already carries the word "watch:"
                     $"{_testPurpose.Text} | {_testWatch.Text} | " +
                     $"state={_testChipIcon.Text} {_testState.Text} | nums={_testNums.Text}");
        }
    }

    /// <summary>The dropdown's Run button (and `Enter` anywhere it has focus):
    /// start the selected scenario. English-only text since the user's ruling of
    /// 2026-10-25 ("alles auf englisch") - the strings around this button used to
    /// be the last German ones on screen.</summary>
    void RunTestFromUi()
    {
        if (_testPickNums.Count == 0)
        {
            SetRunStatus("scenario list still loading (GET /tests) - try again in a second",
                ok: false);
            RequestTestList();
            return;
        }
        int i = _testPick.Selected;
        if (i < 0 || i >= _testPickNums.Count)
        {
            SetRunStatus("pick a scenario from the list, then Run", ok: false);
            return;
        }
        StartTestNumber(_testPickNums[i]);
    }

    /// <summary>One entry point for "run this scenario", whether it came from the
    /// dropdown, from `--run-test`, or from a click that had to queue. A press
    /// always means "interrupt whatever is running and start THIS test": the
    /// server kills the old test process before it spawns the new one, so the
    /// old polling chain is stopped here as well - its responses must not
    /// clobber the new run.</summary>
    void StartTestNumber(int n)
    {
        if (n < 1) return;
        _pollActive = false;
        SelectTestInList(n);
        _runTestBtn.Disabled = true;
        SetRunStatus($"starting test #{n} (a running test is interrupted) \u2026", ok: true);
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
        _lastRunNum = n;
        _cmdHttpBusy = true;
        _pendingCmd = "post";
        var body = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"number\": {n}, \"interrupt\": true}}");
        // RequestRaw: raw byte body + custom headers (Godot 4.7).
        _cmdHttp.RequestRaw(RunTestUrlOf(),
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
            int reqNum = _lastRunNum;
            if (code == 200)
            {
                SetRunStatus($"test #{reqNum} running \u2026", ok: true);
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
                SetRunStatus($"error: {err}", ok: false);
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
            SetRunStatus($"test #{n} running \u2026", ok: true);
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
                ? $"test #{n} done \u2705 (log: {logFile})"
                : $"test #{n} finished with rc={rc} \u274c (log: {logFile})",
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
        _cmdHttp.Request(RunTestUrlOf());
    }

    void SetRunStatus(string text, bool ok)
    {
        if (_runStatus == null) return;
        // One log line per CHANGED message (the poll repeats the same text every
        // half second), so "did the click reach the host?" is answerable from
        // the log instead of from a label only the screen can see.
        if (_runStatus.Text != text) GD.Print($"RUN-STATUS: {text}");
        _runStatus.Text = text;
        // The row is a band of its own style box, so an empty one is a black
        // bar in the window: it appears with the first message and goes when
        // there is nothing to say.
        _runStatus.Visible = text.Length > 0;
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
