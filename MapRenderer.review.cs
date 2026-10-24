using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// MapRenderer.review.cs — the Simulationsreviewer (docs/SIMULATIONS_REVIEWER.md)
//
// A SECOND WINDOW of this client: the map drawing is unchanged, only the SOURCE
// of the cars changes. The live view reads the sim; the review view reads a TAPE
// that this client caches in memory - so a run under review survives the sim
// host dying, which is the user's decision of 2026-10-21.
//
// Truth model (the decision that split the viewer's mechanism in two):
//   ▶ play  → the SAME interpolation path as the live view, but with NO
//             RenderDelaySec: the tape is complete, and a permanent 100 ms
//             offset would make the frame readout lie. Its endpoints are
//             adjacent recorded substeps, so every smoothed pose is bracketed
//             by truth.
//   step    → the exact recorded substep, no lerp, at any ramp speed.
//
// The label IS the address: tape file, frame, sim time, camera, view mode - so
// a screenshot alone can be reconstructed later.

public partial class MapRenderer : Node2D
{
    // --- the tape, cached on the client ---------------------------------
    private sealed class TapeCarRev
    {
        public long Uid; public double X, Y, HeadingDeg, SpeedMps;
        public int Blinkers; public bool Brake, WrongSide;
        public long Crashes;
        public int Lamps;                 // bit0 headlights, bit1 taillights
        public int HeldBy;                // the CAR owning CapKey, 0 = nobody
        public string Color = "", CapKey = "";
    }
    private sealed class TapeEventRev { public long Uid; public string Msg = ""; }
    private sealed class TapeFrameRev
    {
        public long Step; public double Fluidity, Moving;
        public List<TapeCarRev> Cars = new();
        public List<TapeEventRev> Events = new();
    }

    private readonly List<TapeFrameRev> _tape = new();
    private double _tapeDt = 1.0 / 60.0;
    /// <summary>uid -> the role the scenario gave the car ("slow_car",
    /// "parked_car", "oncoming_car"), read from the tape header. A named
    /// cast is what a review is about: "why did THIS car stop" is a
    /// question one asks by role, not by number.</summary>
    private readonly Dictionary<long, string> _tapeNames = new();
    // --review-name <name>: the reviewer selects by ROLE, the same handle the
    // live client takes, resolved from the tape header rather than from the
    // live state. The named car owns slot 1 and the camera follows it, so a
    // review of "the pass of the parked car" stays on the parked car for
    // every frame of the scrub.
    private string _reviewName = "";
    private int _clickDbg;
    /// <summary>The HUD has ONE size - fixed fonts, fixed boxes, whatever the
    /// window is (binding user decision 2026-10-22: "one readable font size,
    /// regardless of the window size"). Nothing multiplies off the window
    /// height any more; the sizes below are the ones the HUD is laid out at.
    /// The single window-dependent number left is the readout font, which may
    /// only ever SHRINK from its fixed size, and only because its unwrapped
    /// line would otherwise run under the map window (ReviewLabelFont).</summary>
    private const int ReadoutFontPx = 28;   // the two-line readout
    private const int AxisFontPx = 20;      // the bar's time scale
    private const int ButtonFontPx = 32;    // the transport row
    private const int PickerFontPx = 23;    // the "open a tape" list
    private const int SlotFontPx = 26;      // the detail windows and their row
    private const int StripPx = 125;        // the instrument strip (half the old 250)
    private const int RowInsetPx = 8;       // dark margin above the button row
    private const int RowHpx = 58;          // the one row of buttons (66 x 58 boxes)
    private float _hudLabelBottom = 96.0f;   // where the readout ends, slots start below
    private int _playDbg;
    private Panel _tapePanel;      // the "open a tape" dialog (modal)
    private Label _axisLabel;      // the bar's time scale
    private double _curveMaxF = 0.1;   // the curve's vertical scale
    /// <summary>Per-uid index of the tape, built ONCE after the tape loads:
    /// decision steps, standstill runs (>= 2 s of SIM time), wrong-side steps
    /// and crash steps. Every one of these used to be a full walk of the tape
    /// PER RENDER FRAME, which at 35 905 substeps put the window at ~2 fps -
    /// the cursor then really did move only a frame or two between clicks.
    /// The index is data, not state: it never changes while scrubbing.</summary>
    private readonly Dictionary<long, List<long>> _decSteps = new();
    private readonly Dictionary<string, string> _decMsg = new();
    private readonly Dictionary<long, List<long>> _stillRuns = new();   // pairs from,to
    private readonly Dictionary<long, List<long>> _wrongSteps = new();
    private readonly Dictionary<long, List<long>> _crashSteps = new();
    private long _markerForSelected = -1L;
    private readonly List<long> _fleetStops = new();

    /// <summary>Walk the tape ONCE after it loads and turn every per-frame
    /// full-tape scan into a lookup. O(frames x cars) once, instead of the
    /// same per render frame - at 35 905 substeps that is the difference
    /// between a window at ~2 fps and one at 60.   // a hash-ish of the selection
    private bool _lampsPrinted;
    private long _reviewUid = 0L;
    private string _tapeName = "", _tapeMap = "", _tapeHash = "";

    private bool _review;
    private string _reviewPath = "";
    private int _cursor;
    private int _reviewAt = 0;   // --review-at: start the cursor inside the run                 // index into _tape
    private bool _playing;               // ▶ play (lerp) vs paused/step (exact)
    private double _playRate = 1.0;      // 0.5x .. 8x of sim time
    private double _scrubDir;            // -1 back / +1 forward while held
    private double _jogDir;              // the '.' / ',' keys: slow jog while held
    private double _jogAcc;              // the jog's own substep accumulator
    private int _jogDbg;                 // the first jogs, logged
    // Binding user decision 2026-10-23: "while I hold down '.' or ',' it shall
    // move slow forward/backward" - one step for the tap, then this fixed,
    // deliberate rate while the key stays down. Deliberately NOT the arrow
    // buttons' ramp: those accelerate the longer they are held, a jog must
    // not run up to two seconds of tape a second just because the finger is
    // still on the key.
    private const double JogStepsPerSecond = 6.0;
    private bool _btnWasHeld;          // scrub button down on the previous frame
    private double _holdSec;             // how long a scrub key has been held
    private double _scrubAcc;            // fractional steps carried over
    private double _scrubAcc2;           // play-mode accumulator
    private bool _reviewShotOnce;

    private Label _reviewLabel;
    private Window _readoutWin;    // the readout box, as a real window
    private ColorRect _readoutBg;  // its panel, full-rect inside the client
    private bool _readoutClosed;   // its own × was used - a new tape re-opens it
    private Control _barGroup;     // the whole instrument, one node, one alpha
    private Vector2 _testBarPoint = new Vector2(-1e6f, -1e6f);  // --test-bar-point <x> <y>
    private float _barAlpha = 1f;  // how much of the instrument is painted
    private float _barWant = 1f;   // what the pointer's PLACE asks of it
    private int _barDbg;           // the first idle decisions, logged
    private Label _camLabel;
    private double _hudW = 0.0;
    private double _hudX0 = 0.0;
    private CanvasLayer _hudLayer;
    private Control _bar;          // the instrument strip (a ColorRect)
    private ReviewScrubZone _scrubZone;   // the bar's seek surface
    private OptionButton _rateBtn;        // playback rate (a CHOICE, not a trigger)
    private Vector2 _hudBuiltAt;          // window size the HUD was laid out for
    private double _barTop = 1e9;         // top edge of the instrument strip
    private int _seekDbg;
    private int _fontDbg;       // calls until the font probe prints (text must exist by then)
    private int _readoutFont;     // the size the readout is laid out at
    private double _zoneTop = 0.0, _zoneBot = 0.0;   // the seek band's y range
    private bool _testSeekFired;                     // --test-seek: once per run
    private bool _testScrubDragFired;                // --test-scrub-drag: once per run
    private Line2D _curveLine, _cursorTick, _headTick;
    private readonly List<Button> _buttons = new();
    private readonly List<Polygon2D> _markerPool = new();
    /// <summary>What a click or a hover on the marker lane would mean: the x
    /// range (screen px, ALREADY padded), the step it points at and the text to
    /// show. Rebuilt with the quads, so a hit can never address a stale frame.</summary>
    private readonly List<(double X0, double X1, long Step, string Text)> _markerHits = new();
    private Label? _tipLabel;
    private string _tipShown = "";
    /// <summary>Half the hit width around an event marker, in px. A decision
    /// hairline is 1.5 px wide: correct to draw, impossible to hit, so the
    /// TARGET is padded rather than the picture (the marker stays thin, the
    /// mouse gets MarkerHitPadPx on either side).</summary>
    const double MarkerHitPadPx = 7.0;
    /// <summary>The open car detail windows, oldest first
    /// (docs/CAR_DETAIL_WINDOW.md). No cap: the operator opens as many as they
    /// want and closes each with its own x.</summary>
    private readonly List<CarDetailWindow> _details = new();
    private readonly List<long> _selected = new();   // = the open windows' cars
    /// <summary>The car whose chain id the hand is holding down: a red box on
    /// the map for as long as the button stays pressed.</summary>
    private long _detailHighlight;
    private Line2D _highlightBox;
    private readonly List<Line2D> _markTicks = new();

    static readonly List<double> Rates = new() { 0.5, 1.0, 2.0, 4.0, 8.0 };

    // The two small controls in a car window's LOWER-RIGHT corner (binding
    // user decision 2026-10-23). They are class-level because the slot's
    // HEIGHT has to reserve room for them: the eight lines a full slot carries
    // stay readable, the button strip is added BELOW them.
    private const float SlotBtnW = 76f;      // wide enough for the word
    private const float SlotBtnH = 30f;
    private const float SlotBtnGap = 10f;    // they must not read as one word
    private const float SlotBtnInset = 12f;  // off the window's edge

    // One click = one frame; a hold accelerates: 0.0 s -> 1, 0.3 s -> 6,
    // 0.8 s -> 20, 1.5 s -> 60, cap 120 frames/s. Same curve both ways.
    private static double ScrubRate(double held)
    {
        if (held < 0.3) return 1.0;
        if (held < 0.8) return 6.0 + (held - 0.3) * 28.0;
        if (held < 1.5) return 20.0 + (held - 0.8) * 57.1;
        return 120.0;
    }

    /// <summary>The newest tape this project wrote - the run picker of the
    /// review window. File names carry the run stamp, so alphabetical order IS
    /// run order.
    ///
    /// GLOBALIZED paths, not res:// ones: the tape is read with the file system
    /// API (so a review needs no host), and File.ReadAllText does not know
    /// Godot's virtual prefix - asking it for "res://tests/…" threw, was caught,
    /// and the window fell back to the live view with "no tape readable".
    /// The same wrong shape in ListRuns made every entry of the run picker a
    /// click that loaded nothing.</summary>
    private static string TapeDir() => ProjectSettings.GlobalizePath("res://tests");

    private static string NewestTape()
    {
        var d = DirAccess.Open("res://tests");
        if (d == null) return "";
        var bestName = "";
        var bestPath = "";
        foreach (var n in d.GetFiles())
        {
            if (!n.StartsWith("tape_") || !n.EndsWith(".txt")) continue;
            // Names carry the run stamp, so a plain comparison orders runs.
            if (n.CompareTo(bestName) > 0) { bestName = n; bestPath = TapeDir() + "/" + n; }
        }
        return bestPath;
    }

    private static string TapeNameOf(string path) => path.Split("/")[^1];

    // The six newest runs, oldest first - a dead host still has its history
    // on disk, so the picker reads the directory rather than /tape/list.
    private static List<string> ListRuns()
    {
        var runs = new List<string>();
        var d = DirAccess.Open("res://tests");
        if (d == null) return runs;
        foreach (var n in d.GetFiles())
        {
            if (!n.StartsWith("tape_") || !n.EndsWith(".txt")) continue;
            runs.Add(TapeDir() + "/" + n);
        }
        runs.Sort();
        if (runs.Count > 6) runs = runs.Slice(runs.Count - 6, 6);   // Slice is (begin, LENGTH)
        return runs;
    }

    private void BuildRunPicker()
    {
        // The dialog is a Control panel, NOT a Window: an embedded Window
        // ignores the absolute positions asked of it (the slot windows came
        // out scattered), and its children landed outside the frame - the
        // window opened empty. A Panel positions exactly where asked.
        _tapePanel = new Panel();
        var tpSb = new StyleBoxFlat
        {
            BgColor = new Color(16 / 255f, 16 / 255f, 22 / 255f, 235f / 255f),
            BorderWidthLeft = 1, BorderWidthTop = 1,
            BorderWidthRight = 1, BorderWidthBottom = 1,
            BorderColor = new Color(0.4f, 0.4f, 0.45f, 0.9f),
        };
        _tapePanel.AddThemeStyleboxOverride("normal", tpSb);
        _hudLayer.AddChild(_tapePanel);
        var runs = ListRuns();
        GD.Print($"REVIEW-RUNS found {runs.Count} tapes in {TapeDir()}");
        // Bottom-right, above the scrub bar: the slots own the top-left and
        // the minimap owns the top-right, so the run list goes where neither
        // instrument lives.
        var vp = GetViewport().GetVisibleRect();
        // Under the minimap: the top-left is the slots, the bottom strip is
        // the scrub bar, so the run list hangs off the map inset.
        // The picker shares the window width, so a long tape name still fits.
        float x = (float)vp.Size.X - Mathf.Max(360.0f, (float)vp.Size.X * 0.34f);
        float y = 350.0f;
        foreach (var path in runs)
        {
            var btn = new Button { Text = TapeNameOf(path) };
            btn.AddThemeFontSizeOverride("font_size", PickerFontPx);
            btn.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            btn.OffsetLeft = x; btn.OffsetTop = y;
            btn.OffsetRight = x + 340.0f; btn.OffsetBottom = y + 30.0f;
            var p = path;
            btn.Pressed += () => ReviewRunSelect(p);
            _tapePanel.AddChild(btn);
            y += 34.0f;
        }
        _tapePanel.OffsetLeft = 12.0f;
        _tapePanel.OffsetTop = _hudLabelBottom + 10.0f;
        _tapePanel.OffsetRight = 12.0f + 380.0f;
        _tapePanel.OffsetBottom = _hudLabelBottom + 10.0f + y;
        _tapePanel.Visible = false;      // modal: only while asked for
    }

    /// <summary>The dialog is MODAL: it appears on the button, and the
    /// choice that was made closes it - a list that stays open after the
    /// selection covers the map for the rest of the review.</summary>
    private void ShowTapeDialog()
    {
        _tapePanel.Visible = true;
        GD.Print($"REVIEW-DIALOG open (tapes listed: {_tapePanel.GetChildCount()})");
    }

    private void ReviewRunSelect(string path)
    {
        GD.Print($"REVIEW-RUN selected {path}");
        _tapePanel.Visible = false;   // the choice closes the dialog
        // A new run is a new tape: reload, rewind, and re-point the camera.
        // The window itself survives - it is long-lived, the run is not.
        _reviewPath = path;
        _tape.Clear();
        if (!LoadTape(path)) return;
        // A new tape is a new moment to review: its address has to be on
        // screen again, even if the operator had closed the box before.
        _readoutClosed = false;
        if (_readoutWin != null) _readoutWin.Visible = true;
        BuildTapeIndex();
        _cursor = 0;
        _playing = false;
        _reviewAt = 0;
        _selected.Clear();
        _carBuf.Clear();
        _carColors.Clear();
        _lastCheckedCursor = -1;
    }

    /// <summary>The review window's own surface: the two-line readout and the
    /// scrub bar with the fluidity curve. A CanvasLayer, so it ignores the
    /// camera - the bar is a screen instrument, not a world object.</summary>
    private void BuildReviewHud()
    {
        // The readout box is built once for the life of the client and only
        // re-fit on a rebuild - a WINDOW is an OS object, and tearing it down
        // to lay it out again is what made the boxes pile up while the main
        // window was resized (measured: four "readout" title bars after one
        // resize, because macOS steps the size and every step rebuilt the
        // surface). The slots learned the same rule before.
        BuildReviewReadout();
        var vpHud = GetViewport().GetVisibleRect();
        _hudBuiltAt = vpHud.Size;
        _hudLayer = new CanvasLayer { Layer = 11 };
        AddChild(_hudLayer);
        // The whole instrument is ONE group now, so the idle fade can put the
        // strip, the curve, the ticks, the scale and the buttons out of the
        // way with a single number (binding user decision 2026-10-23: "can we
        // make the scrub bar disappear when the mouse is not in the area,
        // like in the youtube player"). A Control's modulate multiplies its
        // children, so one alpha is all the group needs.
        _barGroup = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _barGroup.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hudLayer.AddChild(_barGroup);
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(16 / 255f, 16 / 255f, 22 / 255f, 235f / 255f),
            ContentMarginLeft = 10, ContentMarginRight = 10,
            ContentMarginTop = 7, ContentMarginBottom = 7,
        };
        // The bar itself: a dark strip along the bottom, so the curve, the
        // cursor and the buttons read as ONE instrument instead of a hairline
        // floating on grass.
        // A Panel's stylebox went through the theme and stayed translucent
        // on screen (measured: the strip over grass blended to (29,63,29)
        // from (14,16,22) at alpha 1). A ColorRect has one plain colour.
        _bar = new ColorRect { Color = new Color(14 / 255f, 16 / 255f, 22 / 255f) };
        _barGroup.AddChild(_bar);
        // The bar's SEEK surface, added before everything else so the buttons
        // (later children) keep priority over the small patch they share with
        // it. Without it the curve was decoration: the only way to a moment
        // was ▶ or a hundred presses of '.'.
        _scrubZone = new ReviewScrubZone();
        _scrubZone.Seek = ReviewSeek;
        _scrubZone.FracAt = BarTapeFracAtX;
        // (the wheel and the double-click are ReviewInput's, not the zone's:
        //  see the measurement there — a pushed wheel event never gets here)
        _scrubZone.Hover = ReviewMarkerHover;
        _scrubZone.MarkerClick = ReviewMarkerClick;
        _barGroup.AddChild(_scrubZone);

        _curveLine = new Line2D { Width = 2.0f, DefaultColor = new Color(0.45f, 0.85f, 0.95f) };
        _barGroup.AddChild(_curveLine);
        _cursorTick = new Line2D { Width = 2.0f, DefaultColor = new Color(1.0f, 1.0f, 1.0f) };
        _barGroup.AddChild(_cursorTick);
        _axisLabel = new Label();
        _axisLabel.AddThemeFontSizeOverride("font_size", AxisFontPx);
        _axisLabel.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.8f));
        _barGroup.AddChild(_axisLabel);
        _headTick = new Line2D { Width = 1.0f, DefaultColor = new Color(0.55f, 0.55f, 0.55f) };
        _barGroup.AddChild(_headTick);
        for (int i = 0; i < 8; i++)
        {
            var t = new Line2D { Width = 1.5f };
            _barGroup.AddChild(t);
            _markTicks.Add(t);
        }

        // The buttons. A click is an action, a HOLD of ◀ / ▶ accelerates along
        // the ramp curve - the keyboard does the same, the buttons make the
        // gesture visible to someone watching the window over a shared screen.
        // Marker pool: small filled quads on the marker lane. One node per
        // marker rather than one Line2D per type, because a Line2D cannot draw
        // disjoint segments - and the markers of different cars must not be
        // connected to each other.
        for (int i = 0; i < 256; i++)
        {
            var poly = new Polygon2D();
            _barGroup.AddChild(poly);
            _markerPool.Add(poly);
        }

        var glyphs = new List<string> { "▶", "⏸", "⏹", "●", "◀", "▶▶" };
        var actions = new List<string> { "play", "pause", "stop", "record", "back", "fwd" };
        // ONE row, ONE size. The transport glyphs ran at font 38, the camera
        // at 38, "open a tape" at 24 and the rates at 28 - so the row read as
        // five different widgets that happened to touch, and two of the boxes
        // sat 4 px higher than the rest.
        float bfont = ButtonFontPx;
        float bw = 66.0f, bh = (float)RowHpx;
        // Bottom-anchored like the strip itself: 46 px is the strip's own gap
        // to the window bottom, StripPx its height, RowInsetPx the dark margin
        // above the row - so the row cannot drift out of a half-height bar.
        float bt = -((float)46 + StripPx) + RowInsetPx;    // row top
        float bx = 16.0f;
        for (int i = 0; i < glyphs.Count; i++)
        {
            var btn = new Button { Text = glyphs[i], TooltipText = actions[i] };
            btn.AddThemeFontSizeOverride("font_size", (int)bfont);
            // A button that keeps the keyboard steals the scrub keys after the
            // first click - the bar is an instrument, not a form.
            btn.FocusMode = Control.FocusModeEnum.None;
            // Anchored to the BOTTOM-LEFT, so the buttons sit inside the scrub
            // bar itself - a floating row at the top collided with the readout
            // label and read as part of it.
            btn.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            btn.OffsetLeft = bx; btn.OffsetTop = bt;
            btn.OffsetRight = bx + bw;
            btn.OffsetBottom = bt + bh;
            var act = actions[i];
            btn.Pressed += () => ReviewButton(act);
            // The scrub buttons keep the direction held down, so the ramp
            // curve applies to the button exactly as it does to the key:
            // press starts the scrub, release ends it.
            _barGroup.AddChild(btn);
            _buttons.Add(btn);
            bx += bw + 10.0f;
        }
        // The whole-window screenshot, same box as its neighbours.
        var shotBtn = new Button { Text = "📷", TooltipText = "screenshot of this frame" };
        shotBtn.AddThemeFontSizeOverride("font_size", (int)bfont);
        shotBtn.FocusMode = Control.FocusModeEnum.None;
        shotBtn.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        shotBtn.OffsetLeft = bx; shotBtn.OffsetTop = bt;
        shotBtn.OffsetRight = bx + bw;
        shotBtn.OffsetBottom = bt + bh;
        shotBtn.Pressed += () => ReviewShot();
        _barGroup.AddChild(shotBtn);
        bx += bw + 10.0f;

        // The run picker as a GLYPH, so the row stays one instrument: words
        // in a 210 px box were the one wide thing among six squares. (The
        // eject glyph is not in the font and rendered as a triangle; 📂 is.)
        var loadBtn = new Button { Text = "📂", TooltipText = "open a tape" };
        loadBtn.AddThemeFontSizeOverride("font_size", (int)bfont);
        loadBtn.FocusMode = Control.FocusModeEnum.None;
        loadBtn.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        loadBtn.OffsetLeft = bx; loadBtn.OffsetTop = bt;
        loadBtn.OffsetRight = bx + bw;
        loadBtn.OffsetBottom = bt + bh;
        loadBtn.Pressed += () => ShowTapeDialog();
        _barGroup.AddChild(loadBtn);
        bx += bw + 22.0f;

        // Playback rate is a CHOICE, not six triggers: the old row of 0.5x..8x
        // buttons each did `_playRate = r; _playing = true`, so picking a rate
        // started the tape running - someone asking "what does 4x look like"
        // got motion they had not asked for. A dropdown sets the rate only;
        // ▶ is what plays.
        _rateBtn = new OptionButton { TooltipText = "playback rate" };
        _rateBtn.AddThemeFontSizeOverride("font_size", (int)bfont);
        foreach (var r in Rates)
            // "0.5x", not "1/2x": the fraction slash rendered as a plain
            // slash and read as a date.
            _rateBtn.AddItem(r < 1.0 ? "0.5x" : ((int)r).ToString() + "x");
        _rateBtn.Selected = Rates.IndexOf(1.0);
        _rateBtn.ItemSelected += i => SetPlayRate(Rates[(int)i]);
        _rateBtn.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _rateBtn.OffsetLeft = bx; _rateBtn.OffsetTop = bt;
        _rateBtn.OffsetRight = bx + 130.0f;
        _rateBtn.OffsetBottom = bt + bh;
        _barGroup.AddChild(_rateBtn);
        // The Test panel (what is being watched + how the run scored it) is
        // built with the HUD and survives its rebuilds: it is an OS window, and
        // re-minting it on every resize is how "readout" title bars stacked.
        BuildTestWindow();

    }

    /// <summary>One readout window for the life of the client. Rebuilding the
    /// review surface must not mint another OS window - the label, the panel
    /// and the frame are re-fitted to the new text every frame in ReviewLabels
    /// instead.</summary>
    private void BuildReviewReadout()
    {
        if (_readoutWin != null) return;   // ONE OS window, not one per rebuild
        // The readout is a WINDOW now, like the map window and the three slots
        // (binding user decision 2026-10-23: "the info box at the top-left
        // isn't a window" - in the maximized review shot its third line ran
        // straight across the slot window's title bar, and nothing about the
        // box answered to a hand). As a window it gets a title bar, a close
        // button of its own, and the shared guard keeps it 5 px inside the
        // main window and 5 px clear of every other window - the overlap it
        // used to have is now physically impossible.
        _readoutWin = new Window
        {
            Title = "readout",
            Transient = true,           // belongs to this window, not to the app
            // The client area may only start below the belt and the title
            // bar (5 + 8 side, 5 + 32 top) - the guard would put it there
            // anyway, this just asks for what is already legal.
            Position = new Vector2I(13, 37),
            Size = new Vector2I(720, 120),   // ReviewLabels re-fits it to the text
        };
        AddChild(_readoutWin);
        RegisterWindow(_readoutWin);
        var rLayer = new CanvasLayer { Layer = 1 };
        _readoutWin.AddChild(rLayer);
        // The panel colour the review surface uses everywhere - the window's
        // own theme background stays behind it, and the full-rect anchor is
        // what follows the client area when the operator resizes the box.
        _readoutBg = new ColorRect
        {
            Color = new Color(14 / 255f, 16 / 255f, 22 / 255f, 250f / 255f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _readoutBg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        rLayer.AddChild(_readoutBg);
        _reviewLabel = new Label { AutowrapMode = TextServer.AutowrapMode.Off };
        _reviewLabel.AddThemeColorOverride("font_color",
            new Color(235 / 255f, 235 / 255f, 235 / 255f));
        // A subwindow is a Viewport: its children sit in ITS coordinates, so
        // these are window-local numbers, not main-window ones (the slots
        // learned the same, and it is why the old attempt at windows came out
        // "scattered").
        _reviewLabel.Position = new Vector2(12, 12);
        rLayer.AddChild(_reviewLabel);
        _readoutWin.CloseRequested += () =>
        {
            _readoutClosed = true;
            _readoutWin.Visible = false;
            GD.Print("READOUT-CLOSED the tape/frame/camera address is off screen " +
                     "· a shot is no longer self-describing · a new tape re-opens it");
        };
    }

    /// <summary>Set the rate WITHOUT touching playback, and keep the dropdown
    /// honest when the change came from the keyboard (Kp1..Kp5).</summary>
    private void SetPlayRate(double r)
    {
        _playRate = r;
        int i = Rates.IndexOf(r);
        if (i >= 0 && _rateBtn != null && _rateBtn.Selected != i)
            _rateBtn.Selected = i;
        GD.Print($"REVIEW-RATE {_playRate:F1}x (cursor {(_playing ? "playing" : "paused")})");
    }

    /// <summary>A click or a drag on the bar IS a seek: x maps linearly onto
    /// the tape's substeps - the same address the readout prints. It pauses
    /// ▶ play, because the frame you aimed at has to be the frame you get
    /// (a lerp that is one frame past your click is not that frame).</summary>
    private void ReviewSeek(double frac, bool dragging)
    {
        if (_tape.Count == 0) return;
        _playing = false;
        _scrubAcc = 0.0; _scrubAcc2 = 0.0;
        _cursor = Math.Clamp((int)Math.Round(frac * (_tape.Count - 1)), 0, _tape.Count - 1);
        if (_seekDbg < 6)
        {
            _seekDbg++;
            GD.Print($"REVIEW-SEEK {(dragging ? "drag" : "click")} frac={frac:F3} -> " +
                     $"frame {_cursor + 1}/{_tape.Count} (step {_tape[_cursor].Step})");
        }
    }

    /// <summary>The readout's font: its ONE fixed size, shrunk only as far as
    /// it takes for the box to end before the map window. A wide window never
    /// grows the text past ReadoutFontPx; a narrow one may still have to give
    /// in, because the label does not wrap.
    ///
    /// Sized from the LABEL'S OWN laid-out width, not from a shaped estimate:
    /// a 0.50 em/char guess ran ~15 % optimistic, and `Font.GetStringSize` ran
    /// worse still - the theme-default font handle these bindings hand back
    /// measured a 90-char line at 18 px, which is not a measurement. The label
    /// cannot lie about its own text; it only lags a frame, so the size shrinks
    /// on the overshoot and grows again only when there is real slack.</summary>
    private void ReviewLabelFont()
    {
        if (_reviewLabel == null) return;
        var vp = GetViewport().GetVisibleRect();
        // What the box has to stay clear of, plus what rides with the text: the
        // map window's FRAME (its content width plus the ~14 px of border an
        // embedded window draws outside its Position), the readout's stylebox
        // margins and a gap.
        float avail = (float)vp.Size.X - MmInsetWidth() - MmFrameBorder
                      - 24.0f - 12.0f;
        int want = ReadoutFontPx;
        float painted = _reviewLabel.GetMinimumSize().X;
        if (_readoutFont <= 0) _readoutFont = want;
        if (painted > avail + 1.0f)
            _readoutFont = Mathf.Max(14, (int)(_readoutFont * avail / painted));
        else if (painted < avail * 0.88f && _readoutFont < want)
            _readoutFont = Math.Min(want, _readoutFont + Math.Max(1, _readoutFont / 16));
        _reviewLabel.AddThemeFontSizeOverride("font_size", _readoutFont);
        if (++_fontDbg == 60)
            GD.Print($"REVIEW-FONT size={_readoutFont} painted={painted:F0}px " +
                     $"budget={avail:F0}px want={ReadoutFontPx} fixed");
    }

    /// <summary>The map window's width - the inset is the map's shape now, so
    /// the readout and the slot row measure against it, not a constant.</summary>
    private float MmInsetWidth()
        => (_minimap != null ? _minimap.Size.X : MinimapNode.BoxSize)
           + 2 * MinimapNode.Margin;

    /// <summary>An embedded window draws a border OUTSIDE its Size (measured:
    /// 14 px at 1600x900), so its frame starts further left than its Position -
    /// anything that has to stop before the map window budgets for that.</summary>
    private const float MmFrameBorder = 16.0f;

    /// <summary>Tear the review surface down and lay it out again for the
    /// window it now has. Rare (a resize), so rebuilding beats tracking every
    /// derived number.</summary>
    private void RebuildReviewHud()
    {
        if (_hudLayer != null)
        {
            RemoveChild(_hudLayer);
            _hudLayer.QueueFree();
            _hudLayer = null;
        }
        _buttons.Clear(); _markTicks.Clear(); _markerPool.Clear();
        _curveLine = null; _cursorTick = null; _headTick = null;
        _axisLabel = null; _bar = null; _scrubZone = null; _rateBtn = null;
        _barGroup = null;   // it lives in the layer that was just freed
        _tipLabel = null; _tipShown = "";        // lived in that layer too
        _markerHits.Clear();                     // its x's died with the bar
        _testTicksFor = -1;                      // ditto, for the boundaries
        _testTickPool.Clear(); _testTickLabels.Clear();
        // The readout window is NOT torn down here: it is an OS window, and
        // minting a new one per rebuild is what stacked "readout" title bars
        // during a resize. The label lives inside it and keeps its text -
        // only the font budget is re-probed.
        _readoutFont = 0;
        _markerForSelected = -1L;      // the lane must be laid out for the new bar
        BuildReviewHud();
        BuildRunPicker();
        // The open windows follow the row, and the row follows the readout -
        // which the loop in UpdateDetails does once the new size has settled.
        GD.Print($"REVIEW-HUD rebuilt for {GetViewport().GetVisibleRect().Size} " +
                 "(fixed HUD sizes)");
    }

    private static double ParseNum(string s, double fallback)
    {
        try { return double.Parse(s); }
        catch { return fallback; }
    }

    /// <summary>Read a tape file into memory (docs/RECORD_PLAYBACK.md format):
    /// one line per substep, a trailing ";~uid=text" only where a decision was
    /// made. Client-side on purpose - playback then needs no host at all.</summary>
    /// <summary>One `# focus …` line of a tape: what the scenario asked the
    /// window to look at, from the substep it announced it at. The review
    /// client reads the FILE, so the run's camera intent has to survive in it —
    /// otherwise a replay would frame the run differently from the live run
    /// (kinds: "car" = the primary car, "named" = that car of the cast,
    /// "loc" = a place, and then NO car is followed).</summary>
    private readonly record struct RvFocus(long Step, string Kind, double X, double Y,
                                           double ZoomPxM, string Name, long Uid,
                                           string Label, string Scenario, string Desc,
                                           // The car the scenario STAGED under that
                                           // name — a name is not a key (reused in
                                           // scenario after scenario), so a replay
                                           // needs the uid. 0 = older tape, fall
                                           // back to the name map.
                                           long NameUid = 0);

    /// <summary>One `# result …` line: how the suite scored that scenario.</summary>
    private readonly record struct RvResult(long Step, int Number, string Label,
                                            string Scenario, bool Passed, string Reason,
                                            double SimS, double WallS, int Crossings, int OffRoad);

    private readonly List<RvFocus> _tapeFocus = new();
    private readonly List<RvResult> _tapeResults = new();

    /// <summary>The test the cursor is INSIDE (the newest focus note at or
    /// before it) — the key everything per-scenario in this window follows:
    /// the camera, the Test panel, the boundary ticks.</summary>
    private RvFocus? _focusAtCursor;
    private string _focusLabelShown = "";

    /// <summary>The wheel-zoomed range of the scrub bar, in SUBSTEPS. The tape
    /// is the whole run, but a 35 000-substep run drawn across one screen makes
    /// a single decision 0.3 px wide — so the bar gets its own zoom, independent
    /// of the MAP's zoom, and 10 s of a crossing is readable.</summary>
    private bool _barAteWheel;      // a wheel the bar consumed: not a map zoom
    private bool _barAteClickSeek;  // a double-click: reset, not a seek
    private double _viewLo = 0.0, _viewHi = 1.0;      // fractions of the tape
    private bool _barZoomed;
    private bool _cursorOffView;                       // clamped tick = a lie
    private readonly double[] _tickSim = new double[10];
    private readonly List<Line2D> _testTickPool = new();
    private readonly List<Label> _testTickLabels = new();
    private double _testTicksFor = -1;

    private Window? _testWin;
    private Label? _testPanelLabel;   // (the viewer's _testLabel is the "5/21"
                                      // HUD text under the minimap — different thing)
    private string _testShown = "";

    // --- The bar's own zoom ------------------------------------------------
    // The bar is the tape's TIME AXIS, so it gets the same courtesy as the
    // map: a range you can widen and narrow. Wheel over the bar zooms the
    // range about the pointer; the MAP's zoom is untouched (they answer two
    // different questions — "which 20 s" vs "how close").
    const double BarZoomMinSpanFrac = 1.0 / 400.0;   // ≥ 1/400 of the run visible
    const double BarZoomStep = 1.35;                 // per wheel notch

    /// <summary>The bar's x range on screen: the SAME geometry for the curve,
    /// the markers, the axis, the boundary ticks and the seek zone. Every
    /// step→x in this file goes through BarXOf, so zooming the bar cannot
    /// leave one layer behind (a marker lane drawn against the unzoomed axis
    /// is how a zoom feature ends up pointing at the wrong second).</summary>
    double BarX0() => 16.0;
    double BarW()
    {
        var vp = GetViewport().GetVisibleRect();
        return Math.Max(40.0, vp.Size.X - 32.0);
    }

    /// <summary>Index of the tape frame at a bar x, in the CURRENT bar zoom.</summary>
    double BarIndexAtX(float x)
    {
        double frac = (x - BarX0()) / BarW();
        double t = _viewLo + frac * (_viewHi - _viewLo);
        return Math.Clamp(t, 0.0, 1.0) * Math.Max(0, _tape.Count - 1);
    }

    /// <summary>Fraction of the tape (0..1, whole run) at a bar x — what a seek
    /// needs, expressed the way ReviewSeek has always taken it.</summary>
    double BarTapeFracAtX(float x) =>
        _tape.Count < 2 ? 0.0 : BarIndexAtX(x) / (_tape.Count - 1.0);

    /// <summary>Screen x of a tape INDEX at the current bar zoom. Points
    /// outside the visible range come back outside the bar on purpose: the
    /// callers cull them.</summary>
    double BarXOf(double index)
    {
        double span = Math.Max(1e-9, _viewHi - _viewLo);
        double t = _tape.Count < 2 ? 0.0 : index / (_tape.Count - 1);
        return BarX0() + BarW() * ((t - _viewLo) / span);
    }

    /// <summary>Wheel-zoom the bar's visible range about a bar x. `in`
    /// stretches the range (more detail), `out` shrinks it back; the range is
    /// clamped to the tape and never narrower than BarZoomMinSpanFrac.</summary>
    void BarZoom(bool inward, float atX)
    {
        double span = _viewHi - _viewLo;
        double anchorT = _viewLo + (atX - BarX0()) / BarW() * span;
        double f = inward ? 1.0 / BarZoomStep : BarZoomStep;
        double newSpan = Math.Clamp(span * f, BarZoomMinSpanFrac, 1.0);
        // Keep the point under the cursor where it was.
        double fracIn = span > 0 ? (anchorT - _viewLo) / span : 0.5;
        double lo = anchorT - fracIn * newSpan;
        double hi = lo + newSpan;
        if (lo < 0) { lo = 0; hi = newSpan; }
        if (hi > 1) { hi = 1; lo = 1 - newSpan; }
        _viewLo = lo; _viewHi = hi;
        _barZoomed = newSpan < 0.999;
        _markerForSelected = -1;      // every quad is drawn through this range
        GD.Print($"BAR-ZOOM {(inward ? "in" : "out")} range {lo * (_tape.Count - 1):F0}..{hi * (_tape.Count - 1):F0} " +
                 $"of {_tape.Count - 1} substeps ({(hi - lo) * 100:F1}% of the run, " +
                 $"{((hi - lo) * (_tape.Count - 1) * _tapeDt):F1} s of sim)");
    }

    /// <summary>Zoom all the way back out to the whole run.</summary>
    void BarZoomReset()
    {
        _viewLo = 0.0; _viewHi = 1.0; _barZoomed = false;
        _markerForSelected = -1;
    }

    /// <summary>Percent-decoding for the tape's free-text fields (the sim writes
    /// them with Uri.EscapeDataString so a reason with spaces stays one token).</summary>
    private static string Unesc(string v)
    {
        try { return string.IsNullOrEmpty(v) ? "" : Uri.UnescapeDataString(v); }
        catch { return v; }
    }

    private bool LoadTape(string path)
    {
        _tape.Clear();
        string text;
        try { text = File.ReadAllText(path); }
        catch { return false; }
        _tapeName = path.Split("/")[^1];
        _tapeFocus.Clear();
        _tapeResults.Clear();
        _focusAtCursor = null;
        _focusLabelShown = "";
        _viewLo = 0.0; _viewHi = 1.0; _barZoomed = false;
        foreach (var line in text.Split("\n"))
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("#"))
            {
                if (line.Contains("dt_fixed="))
                    _tapeDt = ParseNum(line.Split("dt_fixed=")[1].Split(" ")[0], 1.0 / 60.0);
                if (line.Contains("map_hash="))
                    _tapeHash = line.Split("map_hash=")[1].Split(" ")[0];
                if (line.Contains("names="))
                {
                    // The cast's roles, once per run (the sim names a car AT
                    // its spawn, so the header IS the cast list).
                    foreach (var pair in line.Split("names=")[1].Split(" ")[0].Split(","))
                    {
                        var kv = pair.Split(":");
                        if (kv.Length == 2) _tapeNames[(long)ParseNum(kv[0], -1.0)] = kv[1];
                    }
                }
                if (line.Contains("map=")) _tapeMap = line.Split("map=")[1].Split(" ")[0];
                if (line.StartsWith("# focus ") || line.StartsWith("# result "))
                {
                    // The run's own annotations: camera intent + verdicts. Old
                    // tapes simply have none, which reads as "no declaration"
                    // (= follow the primary car), never as an error.
                    var kv3 = new Dictionary<string, string>();
                    foreach (var tok in line.Substring(line.IndexOf(' ') + 1).Split(' '))
                    {
                        int eq = tok.IndexOf('=');
                        if (eq > 0) kv3[tok.Substring(0, eq)] = tok.Substring(eq + 1);
                    }
                    long Lg(string k, long d = 0) => kv3.TryGetValue(k, out var v) ? (long)ParseNum(v, d) : d;
                    double D(string k, double d = 0) => kv3.TryGetValue(k, out var v) ? ParseNum(v, d) : d;
                    string S(string k) => kv3.TryGetValue(k, out var v) ? Unesc(v) : "";
                    if (line.StartsWith("# focus "))
                        _tapeFocus.Add(new RvFocus(Lg("step"), S("kind"), D("x"), D("y"),
                                                   D("zoom"), S("name"), Lg("uid"),
                                                   S("label"), S("scenario"), S("desc"),
                                                   Lg("name_uid")));
                    else
                        _tapeResults.Add(new RvResult(Lg("step"), (int)Lg("number"), S("label"),
                                                      S("scenario"), S("passed") == "1", S("reason"),
                                                      D("sim"), D("wall"), (int)Lg("xng"),
                                                      (int)Lg("off")));
                }
                continue;
            }
            var col = line.Split(";");
            // Three generations of the format live on disk: the first wrote
            // "step;cars", the next added fluidity + moving share, the current
            // one adds the decision column. Accept all three - refusing an old
            // run would make a recorded test un-reviewable.
            if (col.Length < 2) continue;
            // step;cars                       (first generation)
            // step;fluidity;moving;cars       (second)
            // step;fluidity;moving;cars;~ev   (current)
            var carCol = col.Length >= 4 ? col[3] : col[1];
            var fr = new TapeFrameRev();
            fr.Step = (long)ParseNum(col[0], 0.0);
            fr.Fluidity = col.Length >= 4 ? ParseNum(col[1], 0.0) : 0.0;
            fr.Moving = col.Length >= 4 ? ParseNum(col[2], 0.0) : 0.0;
            foreach (var part in carCol.Split("|"))
            {
                var fld = part.Split(",");
                if (fld.Length < 10) continue;
                var cf = new TapeCarRev {
                    Uid = (long)ParseNum(fld[0], 0.0),
                    X = ParseNum(fld[1], 0.0), Y = ParseNum(fld[2], 0.0),
                    HeadingDeg = ParseNum(fld[3], 0.0),
                    SpeedMps = ParseNum(fld[4], 0.0),
                    Blinkers = (int)ParseNum(fld[5], 0.0),
                    Brake = fld[6] == "1", WrongSide = fld[7] == "1",
                    Crashes = (long)ParseNum(fld[8], 0.0),
                    // Four field generations: no colour, colour + capKey,
                    // colour + lamps + capKey, and colour + lamps + heldBy +
                    // capKey (docs/CAR_DETAIL_WINDOW.md). Accept all of them
                    // rather than refusing the run — the reviewer reads tapes
                    // that were recorded before the field existed. capKey
                    // stays LAST in every generation, because it can contain
                    // commas, which is exactly why heldBy went before it.
                    Color = fld.Length >= 11 ? fld[9] : "",
                    Lamps = fld.Length >= 12 ? (int)ParseNum(fld[10], 0.0) : 0,
                    HeldBy = fld.Length >= 13 ? (int)ParseNum(fld[11], 0.0) : 0,
                    CapKey = fld.Length >= 13 ? string.Join(",", fld, 12, fld.Length - 12)
                             : fld.Length >= 12 ? string.Join(",", fld, 11, fld.Length - 11)
                             : fld.Length >= 11 ? fld[10] : "",
                };
                fr.Cars.Add(cf);
            }
            if (col.Length >= 5 && col[^1].StartsWith("~"))
                foreach (var ev in col[4].Substring(1).Split("~"))
                {
                    int eq = ev.IndexOf('=');
                    if (eq <= 0) continue;
                    fr.Events.Add(new TapeEventRev {
                        Uid = (long)ParseNum(ev.Substring(0, eq), 0.0),
                        Msg = ev.Substring(eq + 1) });
                }
            if (fr.Cars.Count > 0) _tape.Add(fr);
        }
        return _tape.Count > 0;
    }

    /// <summary>Feed the car buffers from the tape at the cursor - the SAME
    /// buffers the live pipeline fills, so the whole renderer (sprites,
    /// blinkers, trails, occlusion) is reused unchanged.
    /// play  → two adjacent frames bracket the render target (the lerp).
    /// step  → one frame, and the target sits exactly on it (no lerp).</summary>
    private void ReviewFeed()
    {
        if (_cursor < 0 || _cursor >= _tape.Count) return;
        var fr = _tape[_cursor];
        double t0 = fr.Step * _tapeDt;
        double rt = t0;
        var next = _cursor + 1 < _tape.Count ? _tape[_cursor + 1] : null;
        if (_playing && next != null)
            rt = t0 + _scrubAcc2 * (next.Step * _tapeDt - t0);

        var seen = new HashSet<long>();
        foreach (var cf in fr.Cars)
        {
            seen.Add(cf.Uid);
            _carColors[cf.Uid] = cf.Color;
            // The lamps, the brake and the blinkers come from the RECORDED
            // substep, not from the live sim beside the tape. The per-frame
            // light pass only runs for a car that has a _carMeta entry, and
            // that entry used to belong to whatever live car shared this
            // uid - so every lamp of every tape car stayed stuck in its
            // construction state (white headlights, black tail dots, amber
            // blinkers) and the reviewer lied about the frame under the
            // cursor. The tail lights are BUILT black and re-coloured red
            // per frame, which is why the wrong ones read as holes.
            if (!_carMeta.TryGetValue(cf.Uid, out var meta))
                _carMeta[cf.Uid] = meta = new CarMeta();
            meta.Color = cf.Color;
            meta.BlinkL = (cf.Blinkers & 1) > 0;
            meta.BlinkR = (cf.Blinkers & 2) > 0;
            meta.Hazard = false;
            meta.Braking = cf.Brake;
            meta.HeadlightsOn = (cf.Lamps & 1) > 0;
            meta.TaillightsOn = (cf.Lamps & 2) > 0;
            if (!_carBuf.TryGetValue(cf.Uid, out var buf))
                _carBuf[cf.Uid] = buf = new List<CarSample>();
            buf.Clear();     // review mode owns its history: no live leftovers
            buf.Add(new CarSample {
                Time = t0, X = (float)cf.X, Y = (float)cf.Y,
                HeadingDeg = (float)cf.HeadingDeg,
                SpeedKmh = (float)(cf.SpeedMps * 3.6), Level = 0 });
            if (_playing && next != null)
                foreach (var nf in next.Cars)
                    if (nf.Uid == cf.Uid)
                        buf.Add(new CarSample {
                            Time = next.Step * _tapeDt, X = (float)nf.X,
                            Y = (float)nf.Y, HeadingDeg = (float)nf.HeadingDeg,
                            SpeedKmh = (float)(nf.SpeedMps * 3.6), Level = 0 });
        }
        // Uids that vanished between frames (a despawn) must not stay on
        // screen - and clearing the SAMPLES was not enough (measured on
        // tape_T_240 frame 3308: the frame carries uid 6 (blue, (99.42,277.82))
        // and uid 7 (mixer, (98.25,265.72)), yet a THIRD sprite - uid 2, the
        // silver parked car, last recorded at step 868 = SIM 14.5 s - was
        // painted at (98.75,275.5), i.e. 40 s of tape out of date, with the
        // blue car driving over it). The render pass skips an empty buffer
        // with `if (buf.Count == 0) continue;`, which leaves the sprite node
        // sitting at its last global transform: the review client drew a car
        // the frame does not have. So the despawn goes through the client's
        // own teardown, the one that also frees the node.
        foreach (var uid in _carBuf.Keys.ToList())
            if (!seen.Contains(uid)) RemoveCar(uid);
        // A node with no buffer at all (the buffer was dropped on a scrub
        // back, a phase switch) is the same lie: nothing on tape owns it.
        foreach (var uid in _carNodes.Keys.ToArray())
            if (!seen.Contains(uid) && !_carBuf.ContainsKey(uid)) RemoveCar(uid);

        // The live path renders at (_simNow - RenderDelaySec). The tape has
        // nothing to hide behind, so the delay is cancelled by aiming _simNow
        // exactly RenderDelaySec past the target - the ONE place the reviewer
        // differs from the viewer, and the reason the readout can be trusted.
        _simNow = rt + RenderDelaySec;
        _lastMaxTime = rt;
        _renderTarget = (float)rt;
        _rtClamped = true;
    }

    /// <summary>The instrument gives the picture back while the pointer is
    /// idle (binding user decision 2026-10-23, "like in the youtube player"):
    /// any pointer move or a hover over the bar's own band paints it up again
    /// at once, three seconds of a still pointer fades it out over a quarter
    /// second - so the operator never loses it by surprise and a screenshot
    /// taken while the hand rests shows the map, not an instrument.
    ///
    /// Only the PAINT comes and goes: the seek surface and the buttons stay
    /// in the tree, so the address of a moment does not move. A click always
    /// follows a move, which means the bar is up before the hand can reach a
    /// button it could not see.</summary>
    private void BarIdle(double delta)
    {
        if (_barGroup == null) return;
        var vp = GetViewport().GetVisibleRect();
        // The pointer's place, in MAIN-window pixels: the OS reports a
        // desktop position, the main window sits where the OS put it.
        var mp = DisplayServer.MouseGetPosition();
        var wp = DisplayServer.WindowGetPosition();
        var p = new Vector2((float)mp.X - (float)wp.X, (float)mp.Y - (float)wp.Y);
        // Motion is read from the pointer itself, not from input events:
        // events inside an embedded window (the minimap's own handle, a slot)
        // never reach this class' _Input, so a hand working there would leave
        // the instrument on its old timer and fade away mid-gesture.
        if (_testBarPoint.X > -1e5f)
        {
            // Dev hook (same family as --test-seek): pin the pointer where the
            // test asks, so "does the instrument fade when the hand is away"
            // is a scripted question and not one that depends on where the OS
            // pointer happens to rest while the run is recorded.
            p = _testBarPoint;
        }
        // The band the instrument occupies: the strip, the row of buttons
        // riding on top of it, and a small apron so a hand does not have to
        // land exactly on the paint to find it. The instrument appears ONLY
        // here (binding user decision 2026-10-23: "it shall only appear when
        // the mouse is where the scrub bar is located") - pointer motion
        // anywhere else on the map does not summon it.
        bool over = p.Y > _barTop - 24.0f && p.Y < vp.Size.Y &&
                    p.X > 0f && p.X < vp.Size.X;
        float want = over ? 1f : 0f;
        if (want != _barWant)
        {
            // Log the DECISION, not every frame of the fade: the alpha animates
            // over a quarter second and would otherwise spam the log.
            if (_barDbg < 8)
            {
                _barDbg++;
                var why = over ? "hand in the bar" : "hand elsewhere";
                GD.Print($"BAR-IDLE wants {want:F0} ({why}) " +
                         $"pointer@({p.X:F0},{p.Y:F0}) band>{_barTop - 24.0:F0} of {vp.Size.Y:F0} · " +
                         $"the seek surface and the buttons stay in the tree, only the paint comes and goes");
            }
            _barWant = want;
        }
        if (want != _barAlpha)
        {
            var step = (float)delta * 4.0f;
            _barAlpha = want > _barAlpha
                ? Mathf.Min(want, _barAlpha + step)
                : Mathf.Max(want, _barAlpha - step);
        }
        // MODULATE, not SelfModulate: a Control's own colour paints only that
        // item (the group draws nothing), while modulate is the colour
        // CHILDREN inherit - which is what has to fade.
        _barGroup.Modulate = new Color(1f, 1f, 1f, _barAlpha);
        if (_testBarPoint.X > -1e5f && _barDbg < 4 &&
            Time.GetTicksMsec() > 1500UL + 2500UL * (ulong)_barDbg)
        {
            _barDbg++;
            GD.Print($"BAR-TEST t={Time.GetTicksMsec():F0}ms alpha={_barAlpha:F2} over={over}");
        }
    }

    private void ReviewTick(double delta)
    {
        // A resize still gets a NEW HUD: the strip, the bar's seek band, the
        // marker lane and the readout's width budget are measured against the
        // viewport when they are built (fonts are FIXED, so nothing here is a
        // scale), and a stale one left the marker lane floating over the map.
        if (_hudLayer != null && GetViewport().GetVisibleRect().Size != _hudBuiltAt)
            RebuildReviewHud();
        // The instrument fades away while the pointer rests (see BarIdle) -
        // asked for every frame, before the early return below, because a
        // faded bar must come back even when no tape is loaded.
        BarIdle(delta);
        if (_tape.Count == 0) return;
        // Advance the cursor: ▶ play runs at playRate x sim time; a held scrub
        // key accelerates along the ramp curve.
        // A held scrub BUTTON ramps like a held key: the button keeps the
        // direction while it is down, release stops it.
        if (_buttons.Count >= 6)
        {
            bool bBack = _buttons[4].IsPressed(), bFwd = _buttons[5].IsPressed();
            if (bBack) _scrubDir = -1.0;
            else if (bFwd) _scrubDir = 1.0;
            else if (_btnWasHeld) _scrubDir = 0.0;   // release stops the ramp
            _btnWasHeld = bBack || bFwd;
        }
        // The slow jog of '.' and ',' while one of them is held (binding user
        // decision 2026-10-23). Its own accumulator, so a jog and a ▶ play
        // never share a fraction.
        if (_jogDir != 0.0)
        {
            _jogAcc += delta * JogStepsPerSecond * _jogDir;
            int whole = (int)_jogAcc;              // towards zero, so the sign is the direction
            if (whole != 0)
            {
                _jogAcc -= whole;
                _cursor = Math.Clamp(_cursor + whole, 0, _tape.Count - 1);
                if (_jogDbg < 6)
                {
                    _jogDbg++;
                    var sign = whole > 0 ? "+" : "";
                    var dir = _jogDir > 0.0 ? "fwd" : "back";
                    GD.Print($"REVIEW-JOG {sign}{whole} -> frame {_cursor}/{_tape.Count - 1} " +
                             $"at {JogStepsPerSecond:F0} substeps/s ({dir})");
                }
            }
        }
        if (_playing)
        {
            // 1x means ONE SECOND of tape per wall second, and the tape's
            // clock is substeps - so the rate is 1/dt_fixed substeps per
            // second, not 1. The old line multiplied by dt_fixed a second
            // time, which ran 1x at ONE substep per second: the window
            // looked frozen while the label honestly said "play" (measured:
            // the cursor sat at frame 1 of 35904 for a whole minute).
            _scrubAcc += delta * _playRate / _tapeDt;
            var whole = (int)_scrubAcc;
            if (whole > 0)
            {
                _scrubAcc -= whole;
                _cursor = Math.Min(_tape.Count - 1, _cursor + whole);
                if (_playDbg < 6)
                {
                    _playDbg++;
                    GD.Print($"REVIEW-PLAY +{whole} -> frame {_cursor}/{_tape.Count - 1} at {_playRate:F1}x");
                }
            }
        }
        if (_scrubDir != 0.0)
        {
            _holdSec += delta;
            _scrubAcc += delta * ScrubRate(_holdSec) * _scrubDir;
            int whole = (int)_scrubAcc;
            if (whole != 0)
            {
                _scrubAcc -= whole;
                _cursor = Math.Clamp(_cursor + whole, 0, _tape.Count - 1);
            }
        }
        else
        {
            _holdSec = 0.0;
            // The reset belongs to the SCRUB, not to the frame: clearing
            // _scrubAcc while ▶ play was running threw away the fraction
            // that had not reached a whole substep yet, so the accumulator
            // never crossed 1.0 and the cursor never moved at all (measured:
            // REVIEW-BTN play fired, zero REVIEW-PLAY lines followed).
            if (!_playing) _scrubAcc = 0.0;
        }
        // Resolve the role against the cast list the header carries.
        if (_reviewName.Length > 0 && _reviewUid == 0L)
            _reviewUid = MatchName(_reviewName, _tapeNames);
        _cursor = Math.Clamp(_cursor, 0, _tape.Count - 1);
        if (_reviewAt > 0 && _lastCheckedCursor < 0)
        {
            _cursor = Math.Clamp(_reviewAt, 0, _tape.Count - 1);
            _reviewAt = 0;
        }
        ReviewFeed();
        // The live surface belongs to the viewer, not to the review window.
        if (_speedLabel != null) _speedLabel.Visible = false;
        if (_flowLabel != null) _flowLabel.Visible = false;
        if (_testLabel != null) _testLabel.Visible = false;
        if (_runStatus != null) _runStatus.Visible = false;
        if (_infoPanel != null) _infoPanel.Visible = false;
        ReviewLabels();
        ReviewHud();
        ReviewPoseCheck();
        ReviewCamera();
        UpdateDetails();
        ReviewMarkers();
        ReviewAxis();
    }

    /// <summary>The two-line readout: the moment AND the view. A screenshot is
    /// only reconstructable if the crop and the scale are on it - a picture
    /// alone cannot say which part of the world it shows or how wide a painted
    /// metre is.</summary>
    private void ReviewLabels()
    {
        if (_reviewLabel == null) return;
        if (_readoutClosed || (_readoutWin != null && !_readoutWin.Visible))
        {
            // The box is closed: the row of slots may come up to the top
            // belt, there is nothing of the readout left above them.
            _hudLabelBottom = WinGapPx + WinTitleBarPx + WinFrameBottomPx + WinGapPx;
            return;
        }
        var fr = _tape.Count > 0 ? _tape[_cursor] : null;
        long step = fr != null ? fr.Step : 0;
        double simS = step * _tapeDt;
        // ONE clock: the frame number and its range are both 1-based, so
        // "frame 1 [1/3768]" cannot be read as "index 0 of 3767".
        // The fluidity window is 5 s of sim; at the head of a tape that
        // window has no history yet, and the number used to read 0.00 for a
        // fleet at 49.9 km/h (measured: tape step 1 carries 0.0000, step 2
        // 6.9338). The readout now names the coverage it actually averaged.
        double winS = Math.Min(5.0, simS);
        var camPos = _cam.GlobalPosition;
        _reviewLabel.Text = string.Join("\n", new[]
        {
            $"{_tapeName} · frame {step} [{_cursor + 1}/{_tape.Count}] · SIM {simS:F3} s",
            // Godot's 2D y grows DOWN while the sim's world y grows north, so
            // the camera readout flips back: the label must state the SIM frame.
            $"cam x {camPos.X:F2} m  y {(-camPos.Y):F2} m · zoom {_cam.Zoom.X:F1} px/m · " +
            $"view: {(_playing ? "play (lerp between recorded frames)" : "step (exact substep)")}" +
            " · units: meters",
            $"map {_tapeMap} · hash: {_tapeHash} · fluidity {(fr != null ? fr.Fluidity : 0.0):F2} m/s/car" +
            $" (window {winS:F2}/5.00 s) · " +
            $"{(_playing ? "▶ " + _playRate.ToString() + "x" : "⏸ step")}",
        });
        // The font comes from the WIDEST LINE actually on the box, which only
        // exists once the text does - so it is set here, and the box's height
        // follows the font it ended up with.
        ReviewLabelFont();
        // One label, three lines, one box: the box is the TEXT's height, so it
        // can no longer overlap a second box or clip its own first line, and
        // the slots know exactly where the readout ends. The label's Size is
        // asked to follow its minimum size each frame (no FitToParent in this
        // build's bindings), and the size is never fed back into the label's
        // own width - that was the runaway that darkened the whole map.
        var readout = _reviewLabel.GetMinimumSize();
        _reviewLabel.Size = readout;
        if (_readoutWin != null)
        {
            // The box IS the window: its client area hugs the text plus the
            // label's 12 px inset, so the title bar and the frame are the
            // chrome the guard counts - and the slot row starts clear of
            // BOTH, which is what stops the readout sitting on a title bar
            // again.
            _readoutWin.Size = new Vector2I((int)(readout.X + 24.0f),
                                            (int)(readout.Y + 24.0f));
            _hudLabelBottom = _readoutWin.Position.Y + _readoutWin.Size.Y
                              + WinFrameBottomPx + WinGapPx;
        }
        else
            _hudLabelBottom = _reviewLabel.Position.Y + readout.Y;
    }

    private void ReviewHud()
    {
        if (_hudLayer == null || _curveLine == null) return;
        var vp = GetViewport().GetVisibleRect();
        double barW = vp.Size.X - 32.0;
        double barX = 16.0;
        double barBase = vp.Size.Y - 46.0;
        double barTop = barBase - StripPx;
        _hudW = barW; _hudX0 = barX; _barTop = barTop;
        if (_bar != null)
        {
            // Anchors pinned to the top-left, and the strip starts ABOVE
            // the button row so the buttons, the axis note, the curve and
            // the marker lane are ONE instrument on one dark background.
            _bar.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _bar.OffsetLeft = 8; _bar.OffsetTop = (float)barTop;
            _bar.OffsetRight = (float)(vp.Size.X - 8.0);
            _bar.OffsetBottom = (float)(barBase + 10.0);
        }
        if (_scrubZone != null)
        {
            // The seek surface is the CURVE's band, starting under the button
            // row: clicking a button must never also be a seek.
            _scrubZone.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _scrubZone.OffsetLeft = (float)barX;
            _scrubZone.OffsetRight = (float)(barX + barW);
            _scrubZone.OffsetTop = (float)(barTop + RowInsetPx + RowHpx + 6.0);
            _scrubZone.OffsetBottom = (float)(barBase + 2.0);
            _zoneTop = barTop + RowInsetPx + RowHpx + 6.0;
            _zoneBot = barBase + 2.0;
        }
        if (_testSeek >= 0.0 && !_testSeekFired)
        {
            // Push a real click through the viewport, so the answer comes from
            // the same input path a hand takes: zone geometry -> _GuiInput ->
            // seek. Once per run - a HUD rebuild must not click again.
            _testSeekFired = true;
            float frac = (float)_testSeek;
            GetTree().CreateTimer(1.2).Timeout += () =>
            {
                float x = (float)(_hudX0 + _hudW * frac);
                float y = (float)((_zoneTop + _zoneBot) * 0.5);
                foreach (bool down in new[] { true, false })
                {
                    var ev = new InputEventMouseButton
                    {
                        Position = new Vector2(x, y),
                        GlobalPosition = new Vector2(x, y),
                        ButtonIndex = MouseButton.Left,
                        Pressed = down,
                    };
                    GetViewport().PushInput(ev);
                }
                GD.Print($"TEST-SEEK click at ({x:F0},{y:F0}) in band " +
                         $"{_zoneTop:F0}..{_zoneBot:F0} (frac {frac:F3})");
            };
        }
        if (_testScrubDrag >= 0.0 && !_testScrubDragFired)
        {
            // The reported bug: holding the mouse down IN THE BAR also panned
            // the map. A click is one event, so --test-seek cannot show it -
            // a drag is press -> motion -> release, and only the motion is
            // what the pan handler acts on. Push that exact sequence through
            // the viewport and print the pin before and after: if the pin
            // moved, the bar's own drag stole a second meaning.
            _testScrubDragFired = true;
            float frac = (float)_testScrubDrag;
            float x = (float)(_hudX0 + _hudW * frac);
            float y = (float)((_zoneTop + _zoneBot) * 0.5);
            var before = _reviewPan;
            // press -> move -> release, one event per input POLL (a drag is
            // three polls in the real hand case, and the pan handler only
            // acts on the motion between them).
            GetTree().CreateTimer(1.2).Timeout += () =>
            {
                GetViewport().PushInput(new InputEventMouseButton
                {
                    Position = new Vector2(x, y),
                    GlobalPosition = new Vector2(x, y),
                    ButtonIndex = MouseButton.Left,
                    Pressed = true,
                });
                GD.Print($"TEST-SCRUB-DRAG press x {x:F0} at y {y:F0} in band " +
                         $"{_zoneTop:F0}..{_zoneBot:F0}; pin before ({before.X:F1},{before.Y:F1})");
                GetTree().CreateTimer(0.15).Timeout += () =>
                {
                    GetViewport().PushInput(new InputEventMouseMotion
                    {
                        Position = new Vector2(x + 60f, y),
                        GlobalPosition = new Vector2(x + 60f, y),
                        Relative = new Vector2(60f, 0f),
                    });
                    GetTree().CreateTimer(0.15).Timeout += () =>
                    {
                        GetViewport().PushInput(new InputEventMouseButton
                        {
                            Position = new Vector2(x + 60f, y),
                            GlobalPosition = new Vector2(x + 60f, y),
                            ButtonIndex = MouseButton.Left,
                            Pressed = false,
                        });
                        GetTree().CreateTimer(0.3).Timeout += () =>
                            GD.Print($"TEST-SCRUB-DRAG after: pin ({_reviewPan.X:F1},{_reviewPan.Y:F1}) " +
                                     $"moved={_reviewPan != before} cam=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2})");
                        // The other half of the question: a drag on the MAP in
                        // review mode is still a pan, so the strip's decline
                        // must not have eaten the grab - and it must pan by the
                        // same 60 px the strip's drag used to steal.
                        GetTree().CreateTimer(0.6).Timeout += () =>
                        {
                            float my = (float)(_zoneTop * 0.4);
                            GetViewport().PushInput(new InputEventMouseButton
                            {
                                Position = new Vector2(x, my),
                                GlobalPosition = new Vector2(x, my),
                                ButtonIndex = MouseButton.Left,
                                Pressed = true,
                            });
                            GetTree().CreateTimer(0.15).Timeout += () =>
                            {
                                GetViewport().PushInput(new InputEventMouseMotion
                                {
                                    Position = new Vector2(x + 60f, my),
                                    GlobalPosition = new Vector2(x + 60f, my),
                                    Relative = new Vector2(60f, 0f),
                                });
                                GetTree().CreateTimer(0.45).Timeout += () =>
                                    GD.Print($"TEST-MAP-DRAG after: pin ({_reviewPan.X:F1},{_reviewPan.Y:F1}) " +
                                             $"moved={_reviewPan != before}");
                            };
                        };
                    };
                };
            };
        }
        // The fluidity curve over the whole tape - what makes two runs
        // comparable, and what the event markers are pinned to.
        var pts = new List<Vector2>(_tape.Count);
        double maxF = 0.1;
        foreach (var f in _tape) maxF = Math.Max(maxF, f.Fluidity);
        _curveMaxF = maxF;
        // The curve used a fixed 56 px amplitude inside a 283 px strip, so
        // ~120 px of dark space sat between the buttons and the line. The
        // amplitude now spans the band the bar actually has.
        double curveTop = barTop + RowInsetPx + RowHpx + 6.0;
        double baseline = barBase - 14.0;
        double amp = Math.Max(24.0, baseline - curveTop);
        // Sampled THROUGH THE BAR'S RANGE: at the full 35 000 substeps across
        // ~2 900 px, three frames share a pixel; zoomed in, the same loop is
        // thousands of points wide, so the stride keeps the polyline near one
        // point per pixel instead of drawing tens of thousands of invisible ones.
        double span = Math.Max(1e-9, _viewHi - _viewLo);
        int visCount = Math.Max(2, (int)Math.Round(span * _tape.Count));
        int stride = Math.Max(1, visCount / Math.Max(2, (int)barW * 2));
        int first = Math.Max(0, (int)Math.Floor(_viewLo * (_tape.Count - 1)) - stride);
        int last = Math.Min(_tape.Count - 1,
                            (int)Math.Ceiling(_viewHi * (_tape.Count - 1)) + stride);
        for (int i = first; i <= last; i += stride)
        {
            var f = _tape[i];
            pts.Add(new Vector2((float)BarXOf(i), (float)(baseline - amp * (f.Fluidity / maxF))));
        }
        if (last > first && (last - first) % stride != 0)
            pts.Add(new Vector2((float)BarXOf(last),
                        (float)(baseline - amp * (_tape[last].Fluidity / maxF))));
        _curveLine.Points = pts.ToArray();
        // cursor + head ticks
        double cx = BarXOf(_cursor);
        // A cursor OUTSIDE a zoomed range must not pretend to be at the edge it
        // is clamped to: the tick sticks to that edge and the axis note says
        // which way to scroll (a clamped tick that lies is worse than none).
        bool offView = cx < barX - 1.0 || cx > barX + barW + 1.0;
        _cursorOffView = offView;
        cx = Math.Clamp(cx, barX + 3.0, barX + barW - 3.0);   // never half outside
        _cursorTick.Points = new Vector2[] {
            new Vector2((float)cx, (float)(baseline - amp)), new Vector2((float)cx, (float)barBase) };
        // The head of the tape sits at the tape's end: at a zoomed range it is
        // off screen, and drawing it at the edge would claim the last recorded
        // substep is right there.
        double hx = BarXOf(Math.Max(0, _tape.Count - 1));
        _headTick.Points = hx >= barX && hx <= barX + barW
            ? new Vector2[] {
                new Vector2((float)hx, (float)(baseline - amp)),
                new Vector2((float)hx, (float)barBase) }
            : new Vector2[0];
        ReviewTestTicks(barX, barW, barBase, baseline - amp);
        // Event markers on the frame's own events (decisions). Blue/red/yellow
        // categories (wrong lane, crash, standstill) are slice B.
        foreach (var t in _markTicks) t.Points = new Vector2[0];
        int slot = 0;
        if (_tape.Count > 0)
        {
            var fr = _tape[_cursor];
            foreach (var ev in fr.Events)
            {
                if (slot >= _markTicks.Count) break;
                _markTicks[slot].Points = new Vector2[] {
                    new Vector2((float)cx, (float)(barBase - 40.0)),
                    new Vector2((float)cx, (float)(barBase - 34.0)) };
                _markTicks[slot].DefaultColor = new Color(0.9f, 0.8f, 0.3f);
                slot++;
            }
        }
    }

    /// <summary>The pick. RIGHT-click asks which window should show this car
    /// (docs/CAR_DETAIL_WINDOW.md: the only gesture that opens or re-points a
    /// window). A LEFT press on a car is the peek card's, and the card is the
    /// next step — until it exists, the left button on a map car does nothing
    /// but keep its pan.</summary>
    private void ReviewSelect(InputEvent e)
    {
        if (!(e is InputEventMouseButton m) || !m.Pressed) return;
        if (m.ButtonIndex != MouseButton.Right) return;
        var vp = GetViewport().GetVisibleRect();
        // A click on the instrument strip is the strip's business (seek, a
        // button, the dropdown) - never a pick. _Input runs BEFORE the GUI
        // takes the event, so this window has to decline it explicitly.
        if (m.Position.Y > _barTop - 6.0) return;
        var world = _cam.GlobalPosition +
                    new Vector2(m.Position.X - vp.Size.X / 2.0f,
                                m.Position.Y - vp.Size.Y / 2.0f) / _cam.Zoom.X;
        var fr = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)];
        long best = -1;
        double bestD = 12.0;      // 12 m pick radius
        foreach (var cf in fr.Cars)
        {
            var p = W((float)cf.X, (float)cf.Y);
            double d = p.DistanceTo(world);
            if (d < bestD) { bestD = d; best = cf.Uid; }
        }
        if (best < 0)
        {
            if (_clickDbg < 4)
            {
                _clickDbg++;
                GD.Print($"REVIEW-PICK miss at ({m.Position.X:F0},{m.Position.Y:F0}) world=({world.X:F1},{world.Y:F1})");
            }
            return;
        }
        if (_clickDbg < 4)
        {
            _clickDbg++;
            GD.Print($"REVIEW-PICK uid {best} at ({m.Position.X:F0},{m.Position.Y:F0})");
        }
        // The menu decides WHAT shows this car — a window's content, or a new
        // window (docs/CAR_DETAIL_WINDOW.md). The old tail of this function
        // rebuilt `_selected` here (last-clicked first, three deep); that list
        // is now DERIVED from the open windows, so a pick that rewrote it would
        // silently re-point the marker lane and the chart behind the windows'
        // backs — the kind of second source of truth the sync helper exists to
        // prevent.
        OpenDetailMenu(m.Position, best);
    }

    private void ReviewInput(InputEvent e)
    {
        // THE BAR'S OWN WHEEL (user request 2026-10-24: "make the scrub-bar
        // zoomable by using the mouse wheel"). Handled here rather than in
        // ReviewScrubZone because this is the ONE place that is handed every
        // event: the zone demonstrably receives clicks (a click seeks), but a
        // pushed wheel event never reached its _GuiInput (measured twice: the
        // range did not move). A wheel INSIDE the bar band zooms the bar's time
        // range and is eaten; everywhere else it stays the map zoom.
        if (_testBarUi && e is InputEventMouseButton wd0 &&
            (wd0.ButtonIndex == MouseButton.WheelUp || wd0.ButtonIndex == MouseButton.WheelDown))
            GD.Print($"BAR-UI ReviewInput saw a wheel: pressed={wd0.Pressed} " +
                     $"pos=({wd0.Position.X:F0},{wd0.Position.Y:F0}) " +
                     $"band={InBarBand(wd0.Position)} vp={GetViewport().GetVisibleRect().Size}");
        if (e is InputEventMouseButton wb && wb.Pressed &&
            (wb.ButtonIndex == MouseButton.WheelUp || wb.ButtonIndex == MouseButton.WheelDown) &&
            InBarBand(wb.Position))
        {
            BarZoom(wb.ButtonIndex == MouseButton.WheelUp, wb.Position.X);
            _barAteWheel = true;
            return;
        }
        // Double-click on the bar: back to the whole run.
        if (e is InputEventMouseButton dc && dc.Pressed && dc.DoubleClick &&
            dc.ButtonIndex == MouseButton.Left && InBarBand(dc.Position))
        {
            BarZoomReset();
            _barAteClickSeek = true;      // the seek below must not also jump
            GD.Print("BAR-ZOOM reset to the whole run");
            return;
        }
        ReviewSelect(e);
        if (!(e is InputEventKey k)) return;
        // A RELEASE is a decision for the jog keys and the scrub arrows:
        // letting go stops the movement. (The old code tested the release at
        // the bottom of this method - under a guard that had already returned
        // for a key that is not pressed - so an arrow kept scrubbing after the
        // finger came up.)
        if (!k.Pressed && !k.Echo)
        {
            if (k.Keycode == Key.Comma || k.Keycode == Key.Period) _jogDir = 0.0;
            if (k.Keycode is Key.Left or Key.Right) _scrubDir = 0.0;
            return;
        }
        // The REPEAT of a held key is not a new step: the hold itself is
        // carried by _jogDir / _scrubDir, so the first press steps once and
        // the jog rate takes over from there.
        if (k.Echo) return;
        // Bound by PHYSICAL position, so the gesture survives other layouts:
        // the two keys right of M step one substep back / forward.
        if (k.Keycode == Key.Comma)
        { _playing = false; _cursor = Math.Max(0, _cursor - 1); _scrubAcc = 0.0; _jogDir = -1.0; _jogAcc = 0.0; }
        else if (k.Keycode == Key.Period)
        { _playing = false; _cursor = Math.Min(_tape.Count - 1, _cursor + 1); _scrubAcc = 0.0; _jogDir = 1.0; _jogAcc = 0.0; }
        else if (k.Keycode == Key.Right) _scrubDir = 1.0;
        else if (k.Keycode == Key.Left) _scrubDir = -1.0;
        else if (k.Keycode == Key.Space) { _playing = false; _scrubDir = 0.0; _jogDir = 0.0; }
        else if (k.Keycode == Key.R) { _playing = true; _scrubAcc2 = 0.0; }
        else if (k.Keycode == Key.F8 && !k.Echo) ReviewShot();
        else if (k.Keycode == Key.Kp1) SetPlayRate(0.5);
        else if (k.Keycode == Key.Kp2) SetPlayRate(1.0);
        else if (k.Keycode == Key.Kp3) SetPlayRate(2.0);
        else if (k.Keycode == Key.Kp4) SetPlayRate(4.0);
        else if (k.Keycode == Key.Kp5) SetPlayRate(8.0);
    }

    /// <summary>The whole reviewer window as one PNG, named against the tape and
    /// the frame, and copied to the clipboard. The label is INSIDE the shot, so
    /// the image carries its own address.</summary>
    private void ReviewShot()
    {
        var fr = _tape.Count > 0 ? _tape[_cursor] : null;
        long step = fr != null ? fr.Step : 0;
        // logs/ is gitignored: a screenshot is an output of a review, and
        // writing it into tests/ made Godot import it as a texture (.import
        // files next to the run tapes, which is noise in the repo).
        string path = $"res://logs/review_{_tapeName}_f{step}.png";
        var img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(path);
        if (err == Error.Ok) GD.Print($"review screenshot saved: {path}");
        else GD.Print($"review screenshot failed: {err}");
        // Godot's own clipboard call (4.x): the OS holds the image, so the
        // shot can be dropped into a conversation without a path round-trip.
        // The image clipboard is not in this build's bindings (DisplayServer
        // exposes ClipboardSet for text only), so the clipboard carries the
        // shot's ADDRESS - the tape name and the frame are inside the image,
        // so a recipient can reconstruct the moment from the path alone.
        DisplayServer.ClipboardSet(path);
    }
    /// <summary>The tape says where a car is; the node says where it was drawn.
    /// Comparing the two in METRES is the only honest way to find out whether
    /// the review window shows the frame it claims - a screenshot cannot tell
    /// that by itself, and the camera follow moves the view after the feed.
    /// Printed once per cursor change, not per frame.</summary>
    private int _lastCheckedCursor = -1;
    private bool _camPrinted;
    // The level that moved the camera last frame ("declared car '19/27'
    // FOLLOWING uid=16" …) — a change of this is a change of intent, so it is
    // what the REVIEW-CAM trace keys on.
    private string _camSrc = "";
    // The scenario whose declaration last drove the view, and where its followed
    // subject was when it was last on screen (FocusHandoff / ReviewCamera).
    private string _lastFocusLabel = "";
    private bool _focusSeenOnce;
    private (double a, double b)? _focusLastPose;
    // The scenario whose declared zoom is already on the camera (empty = none).
    private string _focusZoomApplied = "";

    private void ReviewButton(string action)
    {
        GD.Print($"REVIEW-BTN {action} (pressed signal reached the control)");
        if (action == "play") { _playing = true; _scrubAcc2 = 0.0; }
        else if (action == "pause") { _playing = false; _scrubDir = 0.0; }
        else if (action == "back")
        { _playing = false; _cursor = Math.Max(0, _cursor - 1); _scrubAcc = 0.0; }
        else if (action == "fwd")
        { _playing = false; _cursor = Math.Min(_tape.Count - 1, _cursor + 1); _scrubAcc = 0.0; }
        else if (action == "stop" || action == "record")
        {
            // ⏹ and ● are the ONLY things that touch the sim: stop freezes it,
            // record resumes from the halted state. Everything else in this
            // window is a cursor over a tape (docs/SIMULATIONS_REVIEWER.md).
            PostFreeze(action == "stop");
        }
    }

    /// <summary>⏹ / ● reach the sim through its own REST surface - the review
    /// window holds no simulation state of its own.</summary>
    private void PostFreeze(bool frozen)
    {
        var h = new HttpRequest { UseThreads = false, Timeout = 2.0 };
        AddChild(h);
        var port = _embeddedPort > 0 ? _embeddedPort : PortOfUrl(StateUrl);
        h.Request($"http://127.0.0.1:{port}/freeze",
                  new string[] {}, HttpClient.Method.Post,
                  JsonSerializer.Serialize(new Dictionary<string, object?>
                      { ["frozen"] = frozen }));
    }
    private void ReviewPoseCheck()
    {
        if (_cursor == _lastCheckedCursor || _cursor >= _tape.Count) return;
        _lastCheckedCursor = _cursor;
        var fr = _tape[_cursor];
        foreach (var cf in fr.Cars)
        {
            if (!_carNodes.TryGetValue(cf.Uid, out var node)) continue;
            double dx = node.GlobalPosition.X - cf.X;
            double dy = -node.GlobalPosition.Y - cf.Y;
            if (Math.Abs(dx) > 0.05 || Math.Abs(dy) > 0.05)
                GD.Print($"REVIEW-POSE uid{cf.Uid} tape=({cf.X:F2},{cf.Y:F2}) " +
                         $"drawn=({node.GlobalPosition.X:F2},{-node.GlobalPosition.Y:F2}) " +
                         $"delta=({dx:F2},{dy:F2}) m at frame {fr.Step}");
        }
    }
    // --- the car detail windows (docs/CAR_DETAIL_WINDOW.md) -----------------
    // One window per car, opened by RIGHT-click on the car and closed by its
    // own ×. No cap and no fixed row: the operator places them, the shared
    // guard keeps them inside the main window and 5 px clear of each other,
    // and `_selected` — the marker lane and the chart — is simply the cars that
    // have a window, in the order they were opened.
    //
    // What this replaces (the three-slot row): a fourth car could not be
    // shown, a resized window fought its own stamped Label size, and a car's
    // whole decision history — up to 953 events on a stress run — was one
    // truncated line. The chain was a string match on the cap key, which is
    // why `held_by` went onto the tape first (step 1 of that document).

    /// <summary>Open a window for one car. Called by the right-click menu
    /// ("New slot"), by a chain id clicked, and by the detail probe.</summary>
    private CarDetailWindow OpenDetailWindow(long uid)
    {
        var w = new CarDetailWindow
        {
            CarTexture = CarTexture(_carColors.GetValueOrDefault(uid, "")),
            // A chain reads as the cast the scenario named, not as a column of
            // numbers: "slow_car → me → oncoming_car". A car the run never
            // named keeps its number — an invented name would be worse than
            // the uid (user decision 2026-10-24).
            ChainName = ChainLabel,
        };
        ApplyCarSize(w, _carColors.GetValueOrDefault(uid, ""));
        AddChild(w);
        RegisterWindow(w);          // inside the main window, 5 px from the others
        w.Position = DetailSpot(_details.Count);
        w.CloseRequested += () => CloseDetailWindow(w);
        w.FollowAsked = ToggleDetailFollow;
        w.CenterAsked = CenterDetail;
        w.SwitchAsked = (win, other) =>
        {
            // "click an id, read about THAT car" — the window changes subject,
            // which also drops its `show all` (that was an ask about the old car).
            win.CarTexture = CarTexture(_carColors.GetValueOrDefault(other, ""));
            ApplyCarSize(win, _carColors.GetValueOrDefault(other, ""));
            win.ShowCar(other);
            SyncSelectedFromDetails();
            GD.Print($"DETAIL-SWITCH window → #{other} ({NameOf(other)})");
        };
        w.HighlightAsked = (win, other, on) =>
        {
            if (on) _detailHighlight = other;
            else if (_detailHighlight == other) _detailHighlight = 0L;
            SetMapHighlight(_detailHighlight);
        };
        w.SeekAsked = (win, step) => SeekToStep(step);
        _details.Add(w);
        w.ShowCar(uid);
        SyncSelectedFromDetails();
        GD.Print($"DETAIL-OPEN #{uid} ({NameOf(uid)}) · {_details.Count} window(s) open " +
                 $"· at {w.Position} size {w.Size}");
        return w;
    }

    /// <summary>Draw (or erase) the red box the map shows while the hand holds
    /// a chain id. It hangs off the CAR's own node, so it rides the car without
    /// a per-frame write and inherits the node's rotation (the body box is then
    /// stated once, in the car's local metres).</summary>
    private void SetMapHighlight(long uid)
    {
        if (uid == 0L || !_carNodes.TryGetValue(uid, out var node))
        {
            if (_highlightBox != null) _highlightBox.Visible = false;
            return;
        }
        if (_highlightBox == null)
        {
            _highlightBox = new Line2D
            {
                Width = 3f,
                DefaultColor = new Color(1f, 0.25f, 0.22f, 0.95f),
                Closed = true,
                ZIndex = 20,
            };
        }
        if (_highlightBox.GetParent() != node) node.AddChild(_highlightBox);
        var color = _carColors.GetValueOrDefault(uid, "");
        var (bw, bl) = DrivingGame.Sim.Config.SizeForColor(color);
        float y0 = (float)(-DrivingGame.Sim.Config.REAR_AXLE_OFFSET_M - bl / 2.0);
        float y1 = y0 + (float)bl, x0 = (float)(-bw / 2.0), x1 = x0 + (float)bw;
        _highlightBox.Points = new[] { new Vector2(x0, y0), new Vector2(x1, y0),
                                       new Vector2(x1, y1), new Vector2(x0, y1) };
        _highlightBox.Visible = true;
    }

    private void CloseDetailWindow(CarDetailWindow w)
    {
        bool wasFollow = w.Following;
        if (_detailHighlight != 0 && w.HighlightUid == _detailHighlight) _detailHighlight = 0L;
        _details.Remove(w);
        w.QueueFree();
        SyncSelectedFromDetails();
        GD.Print($"DETAIL-CLOSE #{w.Uid} · {_details.Count} window(s) left" +
                 (wasFollow ? " · the follow went with it" : ""));
    }

    /// <summary>The marker lane and the chart draw for the cars that have a
    /// window — one truth, no second selection list to keep in step.</summary>
    private void SyncSelectedFromDetails()
    {
        _selected.Clear();
        foreach (var d in _details)
            if (d.Showing) _selected.Add(d.Uid);
    }

    /// <summary>Where a window with no remembered geometry goes: a cascade
    /// under the readout. The guard would separate overlapping windows anyway
    /// (`EnforceWindows`, the 5 px belt); asking for a free spot first is what
    /// keeps them where they were put instead of being shoved.</summary>
    private Vector2I DetailSpot(int n)
    {
        var vp = GetViewport().GetVisibleRect().Size;
        int w = CarDetailWindow.DefaultWidthPx, h = CarDetailWindow.DefaultHeightPx();
        int x = 14 + (n % 3) * (w / 3);
        int y = (int)(_hudLabelBottom > 0 ? _hudLabelBottom + 12 : 140) + (n / 3) * 28;
        x = Mathf.Clamp(x, 12, Mathf.Max(12, (int)vp.X - w - 12));
        y = Mathf.Clamp(y, 12, Mathf.Max(12, (int)vp.Y - h - 160));
        return new Vector2I(x, y);
    }

    /// <summary>The car the camera rides: the LAST window whose follow is on
    /// (one car is followed, and the newest ask is the one the operator made).</summary>
    private long DetailFollowUid()
    {
        for (var i = _details.Count - 1; i >= 0; i--)
        {
            var d = _details[i];
            if (d.Following && d.Showing) return d.Uid;
        }
        return 0L;
    }

    private void ToggleDetailFollow(CarDetailWindow w)
    {
        w.Following = !w.Following;
        if (w.Following)
        {
            // Only one car is followed: turning one on turns the others off
            // (the same binding the slots carried), and the pin itself is
            // re-anchored by ReviewCamera on the next frame.
            foreach (var other in _details)
                if (!ReferenceEquals(other, w)) other.Following = false;
        }
        GD.Print($"DETAIL-FOLLOW #{w.Uid} {(w.Following ? "ON — the pin rides the car" : "off")}" +
                 $" · followed={DetailFollowUid()}");
    }

    private void CenterDetail(CarDetailWindow w)
    {
        var pose = CarPose(w.Uid);
        if (pose == null)
        {
            GD.Print($"DETAIL-CENTRE #{w.Uid} not on this frame — the view stays");
            return;
        }
        _mmAsk = ClampViewCentre(pose.Value, "detail centre");
        _reviewPan = Vector2.Zero;      // CENTRE means dead centre, not centre+offset
        _camPrinted = true;             // the one-shot camera line is spent; say more
        GD.Print($"DETAIL-CENTRE #{w.Uid} -> view on " +
                 $"({pose.Value.X:F2},{-pose.Value.Y:F2}) m (sim)");
    }

    /// <summary>Jump the cursor to a recorded substep. The event rows carry the
    /// file's STEP; every internal list is an INDEX, and mixing the two is the
    /// off-by-one that once threw per frame (docs/RECORD_PLAYBACK.md).</summary>
    private void SeekToStep(long step)
    {
        long before = _cursor;
        _cursor = Math.Clamp(IndexOfStep(step), 0, Math.Max(0, _tape.Count - 1));
        GD.Print($"DETAIL-SEEK step {step} -> frame {_cursor}/{_tape.Count - 1} " +
                 $"(was {before}/{_tape.Count - 1}) · SIM " +
                 $"{SimOf(_tape[(int)_cursor].Step):F3} s");
    }

    /// <summary>A window for a probe: the first car on the frame under the
    /// cursor, opened exactly the way the right-click menu opens one.</summary>
    private CarDetailWindow ProbeDetailWindow()
    {
        if (_tape.Count == 0) return null;
        var fr = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)];
        if (fr.Cars.Count == 0) return null;
        foreach (var d in _details) if (d.Showing) return d;
        return OpenDetailWindow(fr.Cars[0].Uid);
    }

    /// <summary>--test-slot-buttons (the name predates the window): every claim
    /// the car detail window makes, measured through the real input path.
    /// Open → follow → resize (does ONLY the list grow?) → seek → second
    /// window. Each step prints the state it says it caused.</summary>
    private void DetailProbe()
    {
        var w = ProbeDetailWindow();
        if (w == null) { GD.Print("DETAIL-PROBE no car on this frame - nothing to open"); return; }
        // Measure ONE FRAME LATER. Asked at open time the containers have not
        // laid out and the frame loop has not painted yet, so the first probe
        // run printed header=8px, rows=0 and the title "car —" for a window it
        // had just filled — and then read "the header GREW on resize" off that
        // baseline. A measured claim needs a settled measurement.
        GetTree().CreateTimer(1.2).Timeout += () =>
        {
        var head0 = w.HeaderHeightForProbe();
        var size0 = w.Size;
        GD.Print($"DETAIL-PROBE open #{w.Uid} ({NameOf(w.Uid)}) size={size0} " +
                 $"header={head0:F0}px rows={w.RowCountForProbe()} " +
                 $"title='{w.Title}' pos={w.Position}");
        GD.Print($"DETAIL-PROBE header: {w.HeaderTextForProbe()}");
        // THEN the resize, THEN the buttons: the first run asked for the
        // follow press while the window was still too short for its own
        // content, and read the silence as "the button does not answer".
        GetTree().CreateTimer(0.4).Timeout += () =>
        {
            var headPre = w.HeaderHeightForProbe();
            var listPre = w.ListHeightForProbe();
            w.Size = new Vector2I(size0.X, size0.Y + 220);
            GetTree().CreateTimer(0.6).Timeout += () =>
            {
                // ±2 px is the container rounding on its own (a FitContent
                // line of 42 measures 41.5); the claim under test is that the
                // header does not take the +220, not that it is bit-exact.
                var head1 = w.HeaderHeightForProbe();
                GD.Print($"DETAIL-PROBE resized +220px: header {headPre:F0} -> {head1:F0} px " +
                         $"({(Math.Abs(head1 - headPre) <= 2.0 ? "UNCHANGED" : "GREW — wrong")}) " +
                         $"· list {listPre:F0} -> {w.ListHeightForProbe():F0} px " +
                         $"({(w.ListHeightForProbe() - listPre > 150 ? "took the growth" : "DID NOT take the growth — wrong")}) " +
                         $"· rows={w.RowCountForProbe()}");
                GD.Print($"DETAIL-PROBE layout after the resize: {w.LayoutForProbe()}");
                var cam0 = _cam.GlobalPosition;
                ClickInWindow(w, w.FollowButton, "DETAIL-PROBE follow");
                GetTree().CreateTimer(0.8).Timeout += () =>
                {
                    GD.Print($"DETAIL-PROBE after follow: following={w.Following} " +
                             $"followed={DetailFollowUid()} cam moved=" +
                             $"({(_cam.GlobalPosition.X - cam0.X):F2},{(_cam.GlobalPosition.Y - cam0.Y):F2}) " +
                             $"· pin={_mmAsk} · playing={_playing} " +
                             $"· button rect={w.FollowButton.GetGlobalRect()}");
                    // WHO ELSE is at that point? Every satellite window this
                    // client owns, its outer rect, and whether the press landed
                    // inside it: a click that changes nothing is usually a
                    // click another window took first, and the log has to say
                    // whose it was instead of leaving the reader to guess.
                    foreach (var other in _windows)
                    {
                        if (!other.IsInsideTree() || !other.Visible) continue;
                        var or = OuterRect(other);
                        GD.Print($"   window '{other.Title}' outer={or} " +
                                 $"covers the press={(or.HasPoint(new Vector2(64, 743)) ? "YES" : "no")}" +
                                 (ReferenceEquals(other, w) ? "  <= the detail window itself" : ""));
                    }
                    var cam1 = _cursor;
                    var stepBtn = w.FirstStepButtonForProbe();
                    if (stepBtn != null)
                    {
                        // Name the row being pressed: a press that lands on
                        // another row still seeks, and that must not read as a
                        // pass (see MapRenderer.ClickInWindow).
                        ClickInWindow(w, stepBtn, $"DETAIL-PROBE seek of {stepBtn.Text}");
                        GetTree().CreateTimer(0.6).Timeout += () =>
                        {
                            GD.Print($"DETAIL-PROBE after seek: cursor {cam1} -> {_cursor} " +
                                     $"· step {_tape[Math.Clamp((int)_cursor, 0, _tape.Count - 1)].Step}");
                            ProbeSecondWindow(w);
                        };
                    }
                    else ProbeSecondWindow(w);
                };
            };
        };
        };
    }

    /// <summary>The last probe step: a SECOND window — no cap any more, and a
    /// place of its own — then the shot (SaveShot quits, so it comes last).</summary>
    private void ProbeSecondWindow(CarDetailWindow first)
    {
        var fr = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)];
        long other = 0;
        foreach (var cf in fr.Cars) if (cf.Uid != first.Uid) { other = cf.Uid; break; }
        if (other != 0)
        {
            var w2 = OpenDetailWindow(other);
            GD.Print($"DETAIL-PROBE second window #{other} at {w2.Position} " +
                     $"(first at {first.Position}) · open={_details.Count} " +
                     $"· markers/chart cars={_selected.Count}");
        }
        // The gesture the whole window hangs on: a RIGHT-click on a car opens
        // the menu that names the windows. Pushed through the input path, so
        // what is measured is the pick radius, the button filter and the entry
        // list - not a private call.
        // Only a car the eye can see can be clicked: after the follow step the
        // camera sits on `first` at 26 px/m, and a car 100 m away is off the
        // screen entirely (measured: the first version of this step addressed a
        // press at (2469,4208) in a 2560 x 1378 window and reported no menu —
        // which was not a broken gesture, it was a press on empty country).
        var vpR = GetViewport().GetVisibleRect();
        var fr2 = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)];
        long target = 0;
        Vector2 targetAt = Vector2.Zero;
        foreach (var cf in fr2.Cars)
        {
            var wp = CarPose(cf.Uid);
            if (wp == null) continue;
            var sp = _cam.GetCanvasTransform() * wp.Value;
            if (!vpR.HasPoint(sp)) continue;
            // And clear of the instrument strip: a press inside its band is the
            // strip's business by rule (ReviewSelect declines it — the same rule
            // that keeps a scrub from doubling as a map grab), so aiming a
            // car-click there measures nothing about the pick.
            if (sp.Y > _barTop - 60.0) continue;
            target = cf.Uid; targetAt = sp;
            if (cf.Uid != first.Uid) break;      // prefer a car WITHOUT a window
        }
        if (target == 0)
            GD.Print($"DETAIL-PROBE right-click: no car in the clickable area " +
                     $"(view {vpR.Size}, strip starts at y={_barTop:F0}) - nothing to click");
        other = target;
        if (other != 0)
        {
            {
                var at = targetAt;
                // Driven as a CONSTRUCTED event, not a pushed one, and the
                // line says so. A pushed mouse event aimed at the map never
                // reached this client's _Input in a run where satellite
                // windows existed (measured: not ONE button event arrived at
                // _Input across the whole probe run — the subwindows take
                // the event first, which is exactly how the presses aimed
                // at their buttons got answered). So the gesture's DELIVERY
                // is Godot's, verified by a hand; what this step measures is
                // the client's own half: the pick radius, the decline over
                // the instrument strip, and the menu the pick opens.
                var ev = new InputEventMouseButton
                {
                    Position = at, GlobalPosition = at,
                    ButtonIndex = MouseButton.Right, Pressed = true,
                };
                ReviewSelect(ev);
                GD.Print($"DETAIL-PROBE right-click on #{other} at " +
                         $"({at.X:F0},{at.Y:F0}) fed straight to the pick " +
                         $"(delivery is Godot's) · windows open {_details.Count}");
            }
        }
        GetTree().CreateTimer(0.6).Timeout += () =>
        {
            if (_screenshotPath != null) SaveShot(_screenshotPath);
        };
    }

    /// <summary>Paint every open window from the frame the cursor sits on.</summary>
    /// <summary>Hand the window the vehicle's footprint, so the header icon
    /// keeps the class's shape: the first build drew every sprite into a square
    /// box and a 4.4 × 1.8 m car came out as a bus (reported 2026-10-24).</summary>
    private static void ApplyCarSize(CarDetailWindow w, string color)
    {
        var (cw, cl) = DrivingGame.Sim.Config.SizeForColor(color);
        w.SetCarSize(cw, cl);
    }

    /// <summary>How a chain addresses a car: the ROLE the scenario gave it
    /// ("slow_car"), and nothing else — a test label like "20/27" is identity
    /// for the title bar, but in a chain it reads as noise, and the fallback
    /// is the number.</summary>
    private string ChainLabel(long uid)
    {
        var n = _tapeNames.GetValueOrDefault(uid, "");
        if (n.Length == 0 || n.Contains("/")) return "";
        return n.Length > 16 ? n.Left(16) : n;
    }

    private void UpdateDetails()
    {
        if (_details.Count == 0 || _tape.Count == 0) return;
        var fr = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)];
        // One pass over the cast for the holder relation — the chain's other
        // direction is a walk over THIS map, not a scan per car per frame.
        var holderOf = new Dictionary<long, long>();
        foreach (var cf in fr.Cars) if (cf.HeldBy != 0) holderOf[cf.Uid] = cf.HeldBy;
        foreach (var w in _details)
        {
            if (!w.Showing) continue;
            w.CarTexture = CarTexture(_carColors.GetValueOrDefault(w.Uid, ""));
            w.Update(BuildDetailData(w.Uid, fr, holderOf, w.ShowAll));
        }
        // The highlighted car may have left the frame (or the window that asked
        // for it may be gone): the box follows the FACT, not the last click.
        if (_detailHighlight != 0L)
        {
            bool onFrame = false;
            foreach (var cf in fr.Cars) if (cf.Uid == _detailHighlight) onFrame = true;
            if (!onFrame) { _detailHighlight = 0L; SetMapHighlight(0L); }
        }
    }

    private CarDetailData BuildDetailData(long uid, TapeFrameRev fr,
                                          Dictionary<long, long> holderOf, bool showAll)
    {
        var d = new CarDetailData
        {
            Uid = uid,
            Name = NameOf(uid),
            Seekable = true,          // a tape can seek; a live run cannot
        };
        foreach (var cf in fr.Cars)
            if (cf.Uid == uid)
            {
                d.SpeedKmh = cf.SpeedMps * 3.6;
                d.Brake = cf.Brake;
                d.BlinkLeft = (cf.Blinkers & 1) > 0;
                d.BlinkRight = (cf.Blinkers & 2) > 0;
                d.X = cf.X; d.Y = cf.Y; d.HeadingDeg = cf.HeadingDeg;
                d.CapKey = cf.CapKey;
            }
        d.Following = AnyFollowOn(uid);

        // LEFT: every car whose holder is me. RIGHT: my holder, then the walk
        // away from me — with a visited set from the FIRST hop, because two
        // cars holding each other is ordinary negotiation (7 788 substeps of it
        // in one suite tape), not a deadlock.
        foreach (var cf in fr.Cars)
            if (cf.Uid != uid && holderOf.GetValueOrDefault(cf.Uid, 0L) == uid)
                d.HoldsUp.Add(cf.Uid);
        d.HoldsUp.Sort();
        var seen = new HashSet<long> { uid };
        long at = holderOf.GetValueOrDefault(uid, 0L);
        while (at != 0L && seen.Add(at) && d.HeldUpBy.Count < 12)
        {
            d.HeldUpBy.Add(at);
            at = holderOf.GetValueOrDefault(at, 0L);
        }
        // A walk that closes back on me is a CYCLE; it is printed as R28's
        // LOCKDOWN only when the car's own history says R28 said so — the two
        // are different facts (docs/CAR_DETAIL_WINDOW.md).
        if (at == uid) d.Lockdown = HasLockdownNote(uid);

        BuildDetailRows(uid, d, showAll);
        return d;
    }

    bool AnyFollowOn(long uid)
    {
        foreach (var w in _details) if (w.Following && w.Uid == uid) return true;
        return false;
    }

    bool HasLockdownNote(long uid)
    {
        var dec = _decSteps.TryGetValue(uid, out var l) ? l : null;
        if (dec == null) return false;
        foreach (var i in dec)
        {
            var m = _decMsg.GetValueOrDefault($"{uid}:{i}", "");
            if (m.Contains("LOCKDOWN")) return true;
        }
        return false;
    }

    /// <summary>The car's events at or before the cursor, NEWEST FIRST. The
    /// whole history is handed over (a stress run's worst car has 953 of them)
    /// and the window caps what it RENDERS at `show all`'s discretion — the
    /// stamp keeps a rebuild to the frames where a new event actually appeared.
    /// The live client fills the same struct from `GET /car/{uid}/decisions`,
    /// which the sim caps at 100 entries in memory — that is what
    /// <see cref="CarDetailData.TruncatedNote"/> is for.</summary>
    private void BuildDetailRows(long uid, CarDetailData d, bool showAll)
    {
        var dec = _decSteps.TryGetValue(uid, out var l) ? l : null;
        if (dec == null || dec.Count == 0) return;
        int upto = 0;
        while (upto < dec.Count && dec[upto] <= _cursor) upto++;
        d.RowsTotal = upto;
        int max = showAll ? upto : Math.Min(upto, CarDetailWindow.RowCap * 4);
        for (int k = upto - 1; k >= 0 && (upto - k) <= max; k--)
        {
            long idx = dec[k];
            d.Rows.Add(new CarDetailRow
            {
                Step = _tape[(int)idx].Step,
                // The SAME clock the readout prints (absolute tape step ×
                // dt_fixed) — two numbers for one instant on one screen is how
                // a review report starts lying.
                SimS = SimOf(_tape[(int)idx].Step),
                Msg = _decMsg.GetValueOrDefault($"{uid}:{idx}", "(no text)"),
            });
        }
        d.RowsLeft = Math.Max(0, upto - d.Rows.Count);
        d.TruncatedNote = "events are held back until you ask for them";
    }

    /// <summary>The right-click menu (docs/CAR_DETAIL_WINDOW.md): no window
    /// open → just "New slot"; windows open → one entry per window (labelled
    /// with the car it shows, which is what the operator is looking at) and
    /// then "New slot". Picking a window REPLACES its car — nothing swaps.</summary>
    private void OpenDetailMenu(Vector2 atPointer, long uid)
    {
        var acts = new List<System.Action>();
        var pm = new PopupMenu();
        foreach (var w in _details)
        {
            long shown = w.Uid;
            pm.AddItem($"#{shown} {NameOf(shown)}", acts.Count);
            acts.Add(() =>
            {
                w.CarTexture = CarTexture(_carColors.GetValueOrDefault(uid, ""));
                ApplyCarSize(w, _carColors.GetValueOrDefault(uid, ""));
                w.ShowCar(uid);
                SyncSelectedFromDetails();
                GD.Print($"DETAIL-REPLACE window #{shown} -> #{uid} ({NameOf(uid)})");
            });
        }
        pm.AddItem("New slot", acts.Count);
        acts.Add(() => OpenDetailWindow(uid));
        // SCREEN SPACE, and explicitly sized. Hung off this Node2D the popup
        // inherits the WORLD canvas — whose transform is the camera — and the
        // zoom multiplies its font: measured at 39.5 px/m, the entry "New slot"
        // came out roughly 1300 px wide and filled a third of the screen. The
        // HUD's own CanvasLayer is where every screen-space instrument lives
        // (the readout, the strip, the tape picker), so the menu belongs there
        // too, with the same font size the picker uses.
        pm.AddThemeFontSizeOverride("font_size", PickerFontPx);
        pm.AddThemeConstantOverride("v_separation", 4);
        pm.AddThemeConstantOverride("h_separation", 8);
        pm.AddThemeConstantOverride("item_start_padding", 12);
        pm.AddThemeConstantOverride("item_end_padding", 16);
        pm.AddThemeConstantOverride("separator_separation", 4);
        pm.Position = new Vector2I((int)atPointer.X, (int)atPointer.Y);
        pm.IndexPressed += i =>
        {
            int at = (int)i;
            if (at >= 0 && at < acts.Count) acts[at]();
        };
        pm.PopupHide += () => pm.QueueFree();
        (_hudLayer ?? (Node)this).AddChild(pm);
        pm.Popup();
        GD.Print($"DETAIL-MENU car #{uid} ({NameOf(uid)}) at ({atPointer.X:F0},{atPointer.Y:F0}) " +
                 $"· {acts.Count} entries ({_details.Count} window(s) already open)");
    }

    /// <summary>The role the scenario gave this car ("slow_car", "parked_car")
    /// from the tape header - empty when the cast was never named. The number
    /// the suite shows for the primary is not a role, so it stays out.</summary>
    private string NameOf(long uid)
    {
        var n = _tapeNames.TryGetValue(uid, out var v) ? v : "";
        if (n == "") return "";
        // A test label ("17/27") IS identity - it is just not a role. Dropping
        // it left the colour as the car's name, which reads as "#1 blue".
        // Quoted, so a label and a role cannot be confused on screen.
        if (n.Contains("/")) return n.Length > 14 ? n.Left(14) : n;
        return n.Length > 14 ? n.Left(14) : n;
    }

    private static string GlyphOn(bool on, string yes, string no)
    {
        return on ? yes : no;
    }
    private static string OrText(string v, string fallback)
    {
        return v.Length > 0 ? v : fallback;
    }

    /// <summary>The decision trail of one car up to the cursor. The tape is
    /// indexed ONCE per run (see BuildTapeIndex) - walking all 35 905 frames
    /// per slot per render frame is what put the window at 2 fps and made ▶
    /// look like it advanced "a frame or two".</summary>
    private void BuildTapeIndex()
    {
        _decSteps.Clear(); _decMsg.Clear(); _stillRuns.Clear();
        _wrongSteps.Clear(); _crashSteps.Clear(); _fleetStops.Clear();
        var runStart = new Dictionary<long, long>();
        var prevCrash = new Dictionary<long, long>();
        for (var i = 0; i < _tape.Count; i++)
        {
            var f = _tape[i];
            var anyMoving = false;
            foreach (var cf in f.Cars)
            {
                if (cf.SpeedMps > 0.5) anyMoving = true;
                if (cf.SpeedMps < 0.5)
                {
                    if (!runStart.ContainsKey(cf.Uid)) runStart[cf.Uid] = i;
                }
                else if (runStart.ContainsKey(cf.Uid))
                {
                    if ((i - runStart[cf.Uid]) * _tapeDt >= 2.0)
                    {
                        if (!_stillRuns.ContainsKey(cf.Uid)) _stillRuns[cf.Uid] = new List<long>();
                        _stillRuns[cf.Uid].Add(runStart[cf.Uid]); _stillRuns[cf.Uid].Add(i);
                    }
                    runStart.Remove(cf.Uid);
                }
                if (cf.WrongSide)
                {
                    if (!_wrongSteps.ContainsKey(cf.Uid)) _wrongSteps[cf.Uid] = new List<long>();
                    _wrongSteps[cf.Uid].Add(i);
                }
                var pc = prevCrash.GetValueOrDefault(cf.Uid, 0L);
                if (cf.Crashes > pc)
                {
                    if (!_crashSteps.ContainsKey(cf.Uid)) _crashSteps[cf.Uid] = new List<long>();
                    _crashSteps[cf.Uid].Add(i);
                }
                prevCrash[cf.Uid] = cf.Crashes;
            }
            // A standstill that reaches the end of the run still closes.
            foreach (var kv in runStart)
                if ((_tape.Count - kv.Value) * _tapeDt >= 2.0)
                {
                    if (!_stillRuns.ContainsKey(kv.Key)) _stillRuns[kv.Key] = new List<long>();
                    _stillRuns[kv.Key].Add(kv.Value); _stillRuns[kv.Key].Add(_tape.Count - 1);
                }
            if (!anyMoving && f.Cars.Count > 0) _fleetStops.Add(i);
            foreach (var ev in f.Events)
            {
                if (!_decSteps.ContainsKey(ev.Uid)) _decSteps[ev.Uid] = new List<long>();
                // The INDEX, not the file's 1-based step column: every other
                // marker list holds indices, and a step is only an index when
                // the tape happens to start at step 1 (a host that had already
                // been running records steps 8 802.. — measured).
                _decSteps[ev.Uid].Add(i);
                // KEYED BY THE INDEX TOO. The step column of THIS run starts at
                // 1, so index i and step f.Step differ by exactly one — and the
                // lookup in DecisionTrail builds its key from _decSteps, i.e.
                // from the index. Keying this dictionary by the file's step
                // therefore missed EVERY read: an exception per frame per slot
                // ("The given key '132:35174' was not present in the
                // dictionary", spammed for as long as a car sat in a slot). One
                // list, one dictionary, one space.
                _decMsg[$"{ev.Uid}:{i}"] = ev.Msg;
            }
        }
    }

    /// <summary>The car's decision count and last message AT OR BEFORE a TAPE
    /// INDEX (docs/RECORD_PLAYBACK.md: the file stores 1-based steps, every
    /// review internal works in indices — mixing them is the off-by-one that
    /// threw a KeyNotFoundException per frame).</summary>
    private string DecisionTrail(long uid, long index)
    {
        var dec = _decSteps.TryGetValue(uid, out var d) ? d : null;
        if (dec == null || dec.Count == 0) return "none";
        // Count the entries at or before the cursor (the list is in index
        // order, so a binary search is enough).
        int lo = 0, hi = dec.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (dec[mid] <= index) lo = mid + 1; else hi = mid;
        }
        if (lo == 0) return "none";
        // A missing message is a display detail, not a crash: this runs for
        // every slot on every frame.
        var last = _decMsg.TryGetValue(uid + ":" + dec[lo - 1], out var m) ? m : "(no text)";
        return $"{lo} here, last: " + (last.Length > 28 ? last.Left(28) : last);
    }

    private void ReviewMarkers()
    {
        // The markers depend on the SELECTION *and on the window*: every quad
        // is laid out against the bar's bottom edge. Caching on the selection
        // alone is what stranded the whole marker lane mid-screen after a
        // maximize (measured: hairlines at y=832 of a 1558 px window, i.e. the
        // barBase of the 900 px window the run started in).
        var vpM = GetViewport().GetVisibleRect().Size;
        long selHash = ((long)vpM.X * 131071L + (long)vpM.Y) * 1000003L;
        foreach (var s in _selected) selHash = selHash * 1000003L + s;
        if (selHash == _markerForSelected) return;
        _markerForSelected = selHash;
        foreach (var p in _markerPool) p.Polygon = new Vector2[] {};
        _markerHits.Clear();
        if (_tape.Count < 2) return;
        var vp = GetViewport().GetVisibleRect();
        double barBase = vp.Size.Y - 46.0;
        double barX = 16.0;
        double barW = vp.Size.X - 32.0;
        int slot = 0;
        void quad(double fromStep, double toStep, Color col, double h, string what = "")
        {
            if (slot >= _markerPool.Count) return;
            double x0 = BarXOf(fromStep), x1 = BarXOf(toStep);
            if (x1 - x0 < 1.5) x1 = x0 + 1.5;
            // Hit test through the SAME x's the quad was drawn at, padded — a
            // hit list computed against the unzoomed axis would point at a
            // different second than the mark you can see.
            if (x1 >= barX - 40 && x0 <= barX + barW + 40 && what.Length > 0)
                _markerHits.Add((x0 - MarkerHitPadPx, x1 + MarkerHitPadPx,
                                 (long)fromStep, what));
            var poly = _markerPool[slot++];
            poly.Polygon = new Vector2[]
            {
                new Vector2((float)x0, (float)(barBase - h)),
                new Vector2((float)x1, (float)(barBase - h)),
                new Vector2((float)x1, (float)(barBase - 1.0)),
                new Vector2((float)x0, (float)(barBase - 1.0)),
            };
            poly.Color = col;
        }
        // Fleet-level lane (thin, at the very bottom): a standstill ANYWHERE
        // in the fleet, so a slow stretch reads as traffic, not as an error
        // on one car. Per-car markers sit above it in their own colours.
        // Fleet-level standstill band: a substep where NO car is moving. The
        // per-frame scan of the whole tape is what made the window crawl, so
        // this is precomputed into _fleetStops at index time.
        foreach (var s in _fleetStops)
            quad(s, s + 1, new Color(0.75f, 0.62f, 0.2f, 0.35f), 4.0,
                 $"fleet standstill at {SimOf(s):F2} s SIM — no car was moving");
        // The index, not the tape: per selected car, walk only its own runs
        // and event steps (a handful each), never all 35 905 frames.
        foreach (var uid in _selected)
        {
            var runs = _stillRuns.TryGetValue(uid, out var rl) ? rl : null;
            if (runs != null)
                for (int i = 0; i + 1 < runs.Count; i += 2)
                    quad(runs[i], runs[i + 1], new Color(0.98f, 0.82f, 0.25f, 0.85f), 9.0);
            var wrong = _wrongSteps.TryGetValue(uid, out var wl) ? wl : null;
            if (wrong != null)
                foreach (var s in wrong) quad(s, s, new Color(0.35f, 0.6f, 1.0f), 12.0);
            var crash = _crashSteps.TryGetValue(uid, out var cl2) ? cl2 : null;
            if (crash != null)
                foreach (var c2 in crash)
                    quad(c2, c2, new Color(1.0f, 0.25f, 0.2f), 12.0,
                         $"car {uid}: CRASH at {SimOf(c2):F2} s SIM — click to jump there");
        }
        // Decision hairlines from the index too: every step where ANY car
        // decided, merged into one sorted list.
        var decAll = new List<long>();
        foreach (var kv in _decSteps) foreach (var s in kv.Value) decAll.Add(s);
        decAll.Sort();
        // Readable hairlines: at 0.22 alpha over a dark strip they vanished,
        // which made a decision-dense stretch look like a flat line.
        for (int i = 0; i < decAll.Count && i < 600; i++)
            quad(decAll[i], decAll[i], new Color(0.78f, 0.78f, 0.85f, 0.55f), 22.0,
                 DecisionText(decAll[i]));
        _markerHits.Sort((a, b) => b.Step.CompareTo(a.Step));   // newest hit wins a tie
    }

    private readonly Dictionary<long, string> _carColors = new();

    /// <summary>The bar's axis: ten frame ticks with the sim time at every
    /// other one, so a point on the curve can be named ("the dip at 60 s")
    /// instead of only pointed at (measured: the curve had no scale at all,
    /// which is what made a 19 000-substep run unreadable).</summary>
    private void ReviewAxis()
    {
        var vp = GetViewport().GetVisibleRect();
        double barX = 16.0, barW = vp.Size.X - 32.0;
        double barBase = vp.Size.Y - 46.0;
        // The axis names the VISIBLE range: at 4 % of the run the tenth tick is
        // not 90 % of the tape, and a scale that lies is worse than no scale.
        for (var i = 0; i < _markTicks.Count; i++)
        {
            var frac = _viewLo + (_viewHi - _viewLo) * ((i + 1) / (_markTicks.Count + 1.0));
            var cx = barX + barW * ((i + 1) / (_markTicks.Count + 1.0));
            double tickSim = frac * Math.Max(0, _tape.Count - 1) * _tapeDt;
            if (_markTicks[i].Points.Length == 2)
                _markTicks[i].Points = new Vector2[]
                {
                    _markTicks[i].Points[0],
                    new Vector2(_markTicks[i].Points[1].X, (float)(barBase - 12.0)),
                };
            _tickSim[i] = tickSim;
            _markTicks[i].Points = new Vector2[]
            {
                new Vector2((float)cx, (float)(barBase - 1.0)),
                new Vector2((float)cx, (float)(barBase - 12.0)),
            };
            _markTicks[i].DefaultColor = new Color(0.6f, 0.6f, 0.66f, 0.7f);
        }
        // The time labels live on the bar, above the ticks.
        if (_axisLabel != null)
        {
            // Named for what is ON SCREEN: a zoomed bar that still says
            // "0 s .. 588 s" is a scale that lies.
            double loSim = _viewLo * Math.Max(0, _tape.Count - 1) * _tapeDt;
            double hiSim = _viewHi * Math.Max(0, _tape.Count - 1) * _tapeDt;
            _axisLabel.Text =
                (_barZoomed ? $"{loSim:F1}–{hiSim:F1} s of {_tape.Count * _tapeDt:F0} s sim"
                            : $"0 s .. {(_tape.Count * _tapeDt):F0} s sim") +
                $" · {Math.Round((_viewHi - _viewLo) * _tape.Count / 10.0):F0} substeps/tick" +
                $" · curve 0 .. {_curveMaxF:F1} m/s/car" +
                (_barZoomed ? " · wheel=out, double-click=whole run" : " · wheel=zoom the bar") +
                (_cursorOffView ? $" · cursor {_tape[_cursor].Step} is " +
                                  $"{(_cursor / Math.Max(1, _tape.Count - 1) < _viewLo ? "LEFT of" : "RIGHT of")} this range" : "");
            // The scale text shares the BUTTON ROW and hangs off the bar's
            // right end: the row is one instrument - transport on the left,
            // its readout on the right - and the band under the row stays
            // free for the curve (binding user decision 2026-10-22, together
            // with halving the bar). Named against the label's own laid-out
            // width, so the right edge lands on the bar's right edge; the
            // width only lags a frame when the text changes length, and it is
            // fixed for the life of a tape.
            _axisLabel.Position = new Vector2(
                (float)(vp.Size.X - 8.0 - _axisLabel.Size.X),
                (float)(barBase - StripPx + RowInsetPx
                        + (RowHpx - _axisLabel.Size.Y) / 2.0));
        }
    }

    /// <summary>The newest focus note at or before the cursor — i.e. the
    /// scenario this frame belongs to — and the Test panel that goes with it.
    /// The notes are recorded in step order, so this is a backwards scan that
    /// stops at the first hit.</summary>
    /// <summary>Apply the recording's intent when the scenario under the cursor
    /// CHANGES: hand over the view from the operator's pin/pan to whatever the
    /// new scenario declared. Strongest input (a slot's FOLLOW toggle, an
    /// explicit "stay on THIS car" the operator is using as an instrument) is
    /// left alone; a pin, a drag-pan and the command-line `--center` are the
    /// framing of the moment they were made in.</summary>
    /// <summary>Set the zoom the scenario declared — ONCE, at the handoff into
    /// it. The per-frame write it replaces was reported as "zoom in the main
    /// window sometimes does not work. I need to zoom in the minimap, and only
    /// after that, it also works in the main window" (2026-10-24), and that is
    /// exactly what it does: the declaration re-asserted its px/m on every frame,
    /// so a wheel notch in the map was overwritten 16 ms later; a minimap zoom
    /// PINNED the view (hand > declaration), which stopped the re-assertion and
    /// "unlocked" the map wheel. The framing belongs to the moment a scenario
    /// starts; after that the operator owns the zoom. Position is a different
    /// matter — a follow has to be re-derived every substep to stay on its car.</summary>
    void ApplyFocusZoom(double zoomPxM, System.Action fit = null)
    {
        if (_focusZoomApplied == _focusAtCursor?.Label) return;   // already framed
        _focusZoomApplied = _focusAtCursor?.Label ?? "";
        if (zoomPxM > 0) _cam.Zoom = new Vector2((float)zoomPxM, (float)zoomPxM);
        else fit?.Invoke();
    }

    private void FocusHandoff()
    {
        string lbl = _focusAtCursor?.Label ?? "";
        if (!_focusSeenOnce)
        {
            // The framing the operator asked for on the command line is the
            // STARTING view — honour it, don't release it on frame one.
            _focusSeenOnce = true;
            _lastFocusLabel = lbl;
            return;
        }
        if (lbl.Length == 0 || lbl == _lastFocusLabel) return;
        _lastFocusLabel = lbl;
        var f = _focusAtCursor.Value;
        if (DetailFollowUid() != 0L)
        {
            GD.Print($"REVIEW-FOCUS '{lbl}': a slot's FOLLOW keeps the view (declared " +
                     $"{f.Kind} takes over when the slot stops following)");
            return;
        }
        bool released = _mmAsk != null || _reviewPan != Vector2.Zero || _overrideCenter != null;
        _mmAsk = null;
        _reviewPan = Vector2.Zero;
        _overrideCenter = null;
        _focusLastPose = null;      // a new scenario, a new subject
        _focusZoomApplied = "";     // …and the new scenario frames its own zoom
        GD.Print($"REVIEW-FOCUS '{lbl}': {f.Kind}" +
                 (released ? " — hand pin/pan released, the recording's focus applies"
                           : " — focus and follow item switch over"));
    }

    /// <summary>Which CAR a focus note means, in this tape. ONE function, so
    /// the camera and the diagnostics can never disagree about it (they did:
    /// the trace resolved by name while the camera resolved by name_uid, and
    /// reported a 1202 px error that existed only in the log).
    ///
    /// Order: the uid the scenario STAGED (name_uid) > the name map > the note's
    /// primary uid. The name map is only a fallback for tapes recorded before
    /// the uid was known — it keeps one entry per uid, so a role reused in a
    /// later scenario points at the wrong car ('19/27' resolved to a slow_car
    /// that is not on screen anywhere near that frame).</summary>
    long FocusSubjectUid(RvFocus? f)
    {
        if (f == null) return 0L;
        var v = f.Value;
        if (v.Kind == "loc") return 0L;          // a place has no subject car
        if (v.Kind == "named")
        {
            if (v.NameUid > 0) return v.NameUid;
            if (v.Name.Length > 0)
                foreach (var kv in _tapeNames)
                    if (kv.Value == v.Name) return kv.Key;
        }
        return v.Uid;
    }

    private void FocusAtCursor()
    {
        if (_tapeFocus.Count == 0) { _focusAtCursor = null; UpdateTestPanel(); return; }
        long step = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)].Step;
        RvFocus? found = null;
        for (int i = _tapeFocus.Count - 1; i >= 0; i--)
            // Both sides in STEP space — that is the unit the file speaks.
            if (_tapeFocus[i].Step <= step) { found = _tapeFocus[i]; break; }
        _focusAtCursor = found;
        UpdateTestPanel();
    }

    /// <summary>"Fit the view on the primary car" for the REVIEW window: the
    /// same physical framing the viewer uses (MapRenderer.CarFocusViewM), but
    /// written straight to the camera — the review pin is re-derived every
    /// substep, so a SetZoom() there would be undone by the next one.</summary>
    void FitViewOnCar(double xM, double yM)
    {
        var vp = GetTree().Root.GetVisibleRect().Size;
        double z = Math.Clamp(vp.X / CarFocusViewM, ZoomMin, CrashZoom);
        _cam.Zoom = new Vector2((float)z, (float)z);
    }

    /// <summary>Is a pointer inside the instrument strip's band (the bar and its
    /// curve)? That rectangle is what separates "zoom the timeline" from "zoom
    /// the map" — and it is the same geometry the strip was built with, so a
    /// resize cannot move the border out from under the mouse.</summary>
    /// <summary>Add a control to the instrument strip's own group, so it fades
    /// and rebuilds WITH the bar. Anything added to the bare layer instead stays
    /// lit after the instrument has faded (measured on a screenshot: three test
    /// boundary ticks floating over green with no bar under them).</summary>
    void AddBarChild(Node child)
    {
        if (_barGroup != null) _barGroup.AddChild(child);
        else _hudLayer?.AddChild(child);
    }

    bool InBarBand(Vector2 at)
    {
        var vp = GetViewport().GetVisibleRect();
        double barBase = vp.Size.Y - 46.0;
        return at.Y > barBase - StripPx - 4.0 && at.Y <= vp.Size.Y;
    }

    /// <summary>Index of the frame carrying a recorded STEP (binary search: the
    /// tape's step column is monotone but does not have to start at 1, so
    /// arithmetic on it is wrong for a tape recorded mid-session). Returns the
    /// first frame at or after that step.</summary>
    int IndexOfStep(long step)
    {
        int lo = 0, hi = _tape.Count - 1;
        if (hi < 0) return 0;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_tape[mid].Step < step) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>SIM seconds of a tape index — the unit every marker's text is
    /// quoted in, because the frame number alone cannot be read by an ear.</summary>
    double SimOf(double step) => step * _tapeDt;

    /// <summary>The decision text a marker stands for ("what happened there").
    /// The tape carries it at the event's own substep, so the tooltip quotes the
    /// driver's own line rather than a category.</summary>
    string DecisionText(long index)
    {
        long step = index + 1;      // what the readout and the log quote
        var fr = _tape[(int)Math.Clamp(index, 0, Math.Max(0, _tape.Count - 1))];
        var parts = new List<string>();
        foreach (var ev in fr.Events)
            parts.Add($"car {ev.Uid}: {ev.Msg}");
        string who = parts.Count > 0 ? string.Join(" · ", parts.Take(2)) : "decision";
        return $"{who} · frame {step} · {SimOf(index):F2} s SIM — click to jump there";
    }

    /// <summary>Where the TESTS begin, drawn at the TOP edge of the bar. One run
    /// is 26 scenarios in one tape, and without boundaries the bar is a
    /// continuous curve in which "the crossing test" has no place at all. A tick
    /// per announced focus note (every scenario announces one), labelled with
    /// the scenario number where the pixels allow it.</summary>
    private void ReviewTestTicks(double barX, double barW, double barBase, double curveTop)
    {
        if (_tape.Count < 2) return;
        long hash = (long)(_viewLo * 1e6) * 7919 + (long)(_viewHi * 1e6)
                    + _tape.Count * 31 + _tapeFocus.Count;
        if (hash == _testTicksFor) return;
        _testTicksFor = hash;
        // A note per scenario number, oldest kept: a re-run of the same label
        // must not paint a second boundary.
        var starts = new List<RvFocus>();
        foreach (var f in _tapeFocus)
            if (!starts.Exists(x => x.Label == f.Label)) starts.Add(f);
        double barTop = curveTop - RowHpx - RowInsetPx - 6.0;
        int used = 0;
        double lastLabelX = -1e9;
        foreach (var f in starts)
        {
            // A note's STEP is the tape's 1-based substep number; the client's
            // internal unit is the 0-based INDEX into _tape. Mixing them is a
            // 60-fold error (measured: a boundary at step 9 landed at 540).
            double x = BarXOf(IndexOfStep(f.Step));
            if (x < barX || x > barX + barW) continue;
            if (used >= _testTickPool.Count)
            {
                // In _barGroup, NOT the bare layer: the boundary marks are part
                // of the instrument, and an element that stays lit while the
                // strip it belongs to has faded is a leftover from a rebuild
                // (measured on a screenshot: three floating ticks, no bar).
                var ln = new Line2D { Width = 1.0f };
                AddBarChild(ln);
                _testTickPool.Add(ln);
            }
            var line = _testTickPool[used++];
            // From the very top of the strip down a hand's width: the curve owns
            // the middle of the band, so a boundary must not read as data.
            line.Points = new Vector2[]
            {
                new Vector2((float)x, (float)(barBase - 2.0)),
                new Vector2((float)x, (float)(barTop + 2.0)),
            };
            line.DefaultColor = new Color(0.55f, 0.85f, 0.95f, 0.55f);
            // The number only when it will not collide with its neighbour —
            // 26 ticks across a wide bar would otherwise be white noise.
            int slotNo = used - 1;
            while (_testTickLabels.Count <= slotNo)
            {
                var lab = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
                lab.AddThemeColorOverride("font_color", new Color(0.72f, 0.86f, 0.92f, 0.9f));
                lab.AddThemeFontSizeOverride("font_size", AxisFontPx);
                AddBarChild(lab);
                _testTickLabels.Add(lab);
            }
            var tl = _testTickLabels[slotNo];
            string num = f.Label.Contains("/") ? f.Label.Split('/')[0] : f.Label;
            if (x - lastLabelX > 46.0 && num.Length > 0 && num.Length <= 4)
            {
                tl.Text = num;
                tl.Visible = true;
                tl.Position = new Vector2((float)(x + 3.0), (float)barTop - AxisFontPx - 4);
                lastLabelX = x;
            }
            else { tl.Text = ""; tl.Visible = false; }
        }
        for (int i = used; i < _testTickPool.Count; i++) _testTickPool[i].Points = new Vector2[0];
        for (int i = used; i < _testTickLabels.Count; i++) _testTickLabels[i].Visible = false;
    }

    /// <summary>Hover over the marker lane: show WHAT happened at that mark.
    /// A 1.5 px hairline with no text is a decoration; the same hairline with a
    /// tooltip on hover is a table of contents for the run. The hit area is the
    /// marker's own x range plus <see cref="MarkerHitPadPx"/> on either side, so
    /// the thin marks are aimable without painting them fatter.</summary>
    private void ReviewMarkerHover(Vector2 at)
    {
        var hit = HitAt(at);
        if (_tipLabel == null && hit != null)
        {
            _tipLabel = new Label
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(320, 0),
            };
            _tipLabel.AddThemeColorOverride("font_color", new Color(0.96f, 0.97f, 1.0f));
            _tipLabel.AddThemeFontSizeOverride("font_size", SlotFontPx);
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(0.04f, 0.06f, 0.10f, 0.94f),
                BorderColor = new Color(0.55f, 0.8f, 0.95f, 0.9f),
                BorderWidthLeft = 1, BorderWidthTop = 1,
                BorderWidthRight = 1, BorderWidthBottom = 1,
                ContentMarginLeft = 10, ContentMarginRight = 10,
                ContentMarginTop = 7, ContentMarginBottom = 7,
            };
            _tipLabel.AddThemeStyleboxOverride("normal", sb);
            // Above the bar and inside the fading group: a tooltip for a marker
            // on an instrument that has faded away must fade with it.
            if (_barGroup != null) _barGroup.AddChild(_tipLabel);
            else _hudLayer?.AddChild(_tipLabel);
        }
        if (_tipLabel == null) return;
        string text = hit?.Text ?? "";
        if (text == _tipShown) { if (text.Length == 0) _tipLabel.Visible = false; return; }
        _tipShown = text;
        _tipLabel.Text = text;
        _tipLabel.Visible = text.Length > 0;
        if (text.Length > 0)
        {
            var vp = GetViewport().GetVisibleRect();
            _tipLabel.Position = new Vector2(
                (float)Math.Clamp(at.X - 160.0, 12.0, vp.Size.X - 780.0),
                (float)(vp.Size.Y - 46.0 - StripPx - 96.0));
        }
    }

    private (double X0, double X1, long Step, string Text)? HitAt(Vector2 at)
    {
        foreach (var h in _markerHits)
            if (at.X >= h.X0 && at.X <= h.X1) return h;
        return null;
    }

    /// <summary>A click on a marker jumps the cursor to that substep (the padded
    /// hit list already decided which event was meant).</summary>
    private bool ReviewMarkerClick(Vector2 at)
    {
        var h = HitAt(at);
        if (h == null) return false;
        int idx = (int)Math.Clamp(h.Value.Step, 0, Math.Max(0, _tape.Count - 1));
        _cursor = idx;
        _playing = false;
        _tipShown = "";
        GD.Print($"BAR-MARKER click -> frame {h.Value.Step} ({h.Value.Text})");
        return true;
    }

    /// <summary>The TEST panel: what is being watched, and how the run scored
    /// it. Its content follows the cursor's scenario — the same focus note the
    /// camera obeys — so scrubbing into the next test turns the page.</summary>
    private void BuildTestWindow()
    {
        if (_testWin != null) return;
        _testWin = new Window
        {
            Title = "test",
            Transient = true,           // belongs to this window, not to the app
            Size = new Vector2I(620, 132),   // UpdateTestPanel re-fits it
        };
        _testPanelLabel = new Label
        {
            Position = new Vector2(10, 8),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(560, 0),
        };
        _testPanelLabel.AddThemeColorOverride("font_color", new Color(0.93f, 0.95f, 0.99f));
        _testPanelLabel.AddThemeFontSizeOverride("font_size", SlotFontPx);
        var sb = new StyleBoxFlat { BgColor = new Color(0.05f, 0.07f, 0.11f, 0.96f) };
        sb.ContentMarginLeft = sb.ContentMarginRight = 12;
        sb.ContentMarginTop = sb.ContentMarginBottom = 10;
        _testPanelLabel.AddThemeStyleboxOverride("normal", sb);
        _testWin.AddChild(_testPanelLabel);
        AddChild(_testWin);
        _testWin.Size = new Vector2I(600, 132);
    }

    /// <summary>Rewrite the panel when the scenario under the cursor changes —
    /// not every frame: the text is a paragraph, and re-laying it out at 60 Hz
    /// is exactly the kind of per-frame churn this client has already been
    /// burned by.</summary>
    private void UpdateTestPanel()
    {
        if (_testPanelLabel == null) return;
        var f = _focusAtCursor;
        string label = f?.Label ?? "";
        var res = f == null ? default(RvResult?)
            : _tapeResults.FindLast(r => r.Label == f.Value.Label);
        string key = $"{label}|{res?.Passed.ToString()}|{res?.Reason}";
        if (key == _testShown) return;
        _testShown = key;
        string head = label.Length > 0
            ? $"Test {label} · {f?.Scenario}"
            // Two different nothings, and the operator needs to know which:
            // a run recorded BEFORE the feature exists (nothing to show, ever)
            // vs. a cursor parked in the idle time BETWEEN two scenarios
            // (scroll a little and a test appears).
            : _tapeFocus.Count > 0
                ? "Between scenarios — no test at this frame"
                : "Test (this tape records no scenarios)";
        string verdict = res == null
            ? "no verdict recorded for this scenario"
            : (res.Value.Passed ? "✅ PASSED" : "❌ FAILED") +
              $" · {res.Value.SimS:F1} s sim · {res.Value.WallS:F1} s wall" +
              $" · {res.Value.Crossings} centreline crossing(s) · {res.Value.OffRoad} off-road";
        string why = res != null && !res.Value.Passed && res.Value.Reason.Length > 0
            ? $"\nreason: {res.Value.Reason}" : "";
        string desc = f != null && f.Value.Desc.Length > 0 ? $"\n{f.Value.Desc}" : "";
        string focus = f?.Kind switch
        {
            "loc" => $"\nfocus: the PLACE ({f.Value.X:F0}, {f.Value.Y:F0}) m @ {f.Value.ZoomPxM:F0} px/m — no car followed",
            "named" => $"\nfocus: the named car '{f.Value.Name}'",
            _ => "\nfocus: the primary car (view fitted)",
        };
        _testPanelLabel.Text = head + "\n" + verdict + why + desc + focus;
        _testWin.Size = new Vector2I(620,
            (int)Math.Ceiling(_testPanelLabel.GetMinimumSize().Y) + 16);
        LayoutTestWindow();
    }

    /// <summary>Where the panel sits: under the readout's own box, above the
    /// instrument strip, inside the main window — the guard in EnforceWindows
    /// fixes it if a resize moves anything into it.</summary>
    private void LayoutTestWindow()
    {
        if (_testWin == null) return;
        var vp = GetViewport().GetVisibleRect();
        // Top-RIGHT, not under the readout. These are separate OS windows and
        // nothing orders them: stacked in the top-left column the Test panel
        // came out buried under the readout and a car slot, i.e. a window whose
        // whole job is to be read was unreadable (seen on a screenshot). The
        // right-hand column is free in the reviewer, and the panel is the one
        // window that is about the RUN rather than about a car.
        double x = Math.Max(12.0, vp.Size.X - _testWin.Size.X - 12.0);
        double y = 12.0;
        // …but STACKED under the minimap, which is the top-right resident (an
        // embedded Window of its own, 64 px from the top and as tall as the
        // inset). Right-aligned in the same column, one below the other: two
        // windows that both claim the corner end up with one unreadable (seen on
        // a screenshot: the panel's first line behind the minimap's caption).
        if (_mmWin is { Visible: true })
            y = Math.Max(y, _mmWin.Position.Y + _mmWin.Size.Y + 10.0);
        _testWin.Position = new Vector2I((int)x, (int)y);
    }

    /// <summary>The pose a slot's car has AT THE FRAME UNDER THE CURSOR, in
    /// Godot world metres - the pin both the follow toggle and the centre
    /// button aim at.</summary>
    private Vector2? CarPose(long uid)
    {
        if (_cursor < 0 || _cursor >= _tape.Count) return null;
        var fr = _tape[_cursor];
        foreach (var cf in fr.Cars)
            if (cf.Uid == uid) return W((float)cf.X, (float)cf.Y);
        return null;
    }

    private void ReviewCamera()
    {
        if (_cursor < 0 || _cursor >= _tape.Count) return;
        var fr = _tape[_cursor];
        double cx = 0.0, cy = 0.0;
        // WHICH of the five levels is driving the view, weakest first (the last
        // writer wins). "the camera looks right" and "the auto-focus works" are
        // different claims, and the old log could only answer the first: it
        // printed "tape-centroid" for a view that a --center was holding, so an
        // operator could not tell a missing declaration from an override they
        // had passed themselves (asked 2026-10-24: "looks like the auto-focus
        // does not work" — it was, and the log could not show why).
        string src = "cast centroid";
        foreach (var cf in fr.Cars) { cx += cf.X; cy += cf.Y; }
        int n = Math.Max(1, fr.Cars.Count);
        cx /= n; cy /= n;
        // Which scenario is the cursor inside, and what did IT ask to be shown?
        // (docs/TESTING.md "Camera focus".) Recomputed here because this is the
        // one place that runs for every cursor move — scrub, jog, play, seek.
        FocusAtCursor();
        // A TEST BOUNDARY RE-CLAIMS THE VIEW. Precedence is per scenario, not
        // per session: the hand outranks the recording WHILE the operator is
        // looking at this one moment, and the next scenario is a different
        // intent — which is exactly what the review is for ("when we switch to
        // the next test, the focus and follow shall change to that test's focus
        // and follow item", 2026-10-24). It used to latch: one stray map drag
        // and the log went `src=declared PLACE '17/27'` → `src=the hand (pin)`
        // and NEVER back, so from that drag on no scenario's focus or followed
        // car was ever applied again (measured in a live review session).
        FocusHandoff();
        // A named car pins the view on IT for the whole scrub - the centroid
        // of a cast drifts with every car that drives by, and the question
        // under review is about one of them.
        if (_reviewUid != 0L)
        {
            foreach (var cf in fr.Cars)
                if (cf.Uid == _reviewUid) { cx = cf.X; cy = cf.Y; }
            src = $"--review-name uid={_reviewUid}";
        }
        // --center is stored in GODOT space (already flipped by W at parse),
        // the tape centroid is in SIM metres - one flip, not two. The log
        // proved it: want=(100,-275) arrived at godot=(100,+275), which is
        // grass, and the double flip is why.
        // A car detail window's FOLLOW toggle (docs/CAR_DETAIL_WINDOW.md).
        // It is not a separate camera tier: following a car IS a hand-pin whose
        // anchor rides the car. That is what makes it beat a place the hand
        // pinned before (engaging a follow RE-ANCHORS the pin to the car's pose
        // at this frame), what keeps the operator's pan on top of it (the pin is
        // the anchor, `_reviewPan` the offset), and what stops the view from
        // jumping back when the follow is released: the pin simply keeps the
        // last pose it was given.
        var followUid = DetailFollowUid();
        if (followUid != 0L) src = $"window FOLLOW uid={followUid}";
        // The hierarchy of "what the view is on", strongest first:
        //   1. the HAND — a minimap pin, a window's CENTRE, a map drag, or a
        //      window's FOLLOW (the same pin again, re-anchored to that car
        //      every frame, so it outranks a place and still keeps the pan)
        //   2. --center / --review-name the operator passed on the command line
        //   3. WHAT THE SCENARIO DECLARED  ← the run's own intent
        //   4. the cast's centroid (a tape with no declarations at all)
        // A declared "loc" focus is deliberately NOT a car: the crossing and the
        // pass tests are about a place, and following one car out of 50 would
        // carry the camera away from the story (user decision 2026-10-24).
        if (followUid == 0L && _mmAsk == null && _overrideCenter == null &&
            _reviewUid == 0L && _focusAtCursor != null)
        {
            var f = _focusAtCursor.Value;
            if (f.Kind == "loc")
            {
                cx = f.X; cy = f.Y;
                src = $"declared PLACE '{f.Label}' ({f.X:F0},{f.Y:F0}) - no car followed";
                ApplyFocusZoom(f.ZoomPxM);
            }
            else
            {
                long wantUid = FocusSubjectUid(f);
                bool onFrame = false;
                foreach (var cf in fr.Cars)
                    if (cf.Uid == wantUid) { cx = cf.X; cy = cf.Y; onFrame = true; }
                // A subject that has left the cast (removed between phases, or a
                // name that belongs to a later scenario) must NOT throw the view
                // back to the fleet centroid mid-scenario: the camera stays where
                // it last had the car, and says so.
                if (onFrame) _focusLastPose = (cx, cy);
                else if (_focusLastPose != null) { cx = _focusLastPose.Value.a; cy = _focusLastPose.Value.b; }
                // FOLLOW, not just frame: this branch is re-derived for every
                // substep the cursor moves, so the view rides the car through
                // the scenario instead of starting on it and drifting off.
                src = onFrame
                    ? $"declared {f.Kind} '{f.Label}' FOLLOWING uid={wantUid}"
                    : $"declared {f.Kind} '{f.Label}' uid={wantUid} not on this frame";
                ApplyFocusZoom(f.ZoomPxM, () => FitViewOnCar(cx, cy));
            }
        }
        var pose = followUid != 0L ? CarPose(followUid) : null;
        if (pose != null)
        {
            // Re-anchor the pin on the car and let the pin branch below place
            // the camera: one writer of the final position, so the pan, the
            // window guard and the readout all see the same geometry.
            _mmAsk = pose.Value;
            src = $"window FOLLOW uid={followUid} (the pin rides the car)";
        }
        if (_mmAsk != null)
        {
            src = "the hand (pin)";
            // The hand pinned the view on a spot of the map THROUGH the
            // minimap's inset (or a slot's CENTRE button): an absolute place,
            // so it keeps its meaning while the tape scrubs and the cast's
            // centroid drifts.
            //
            // The pan DOES belong on top of it. It used not to ("the drag
            // offset is not added"), which made panning die silently: after
            // one CENTRE press the view obeyed the pin alone, so a grab kept
            // updating _reviewPan while the camera stayed where it was
            // (measured 2026-10-24: cam frozen at (1991.43,-1723.27) while
            // _reviewPan moved to (-273.68, 0), with nothing left to clear the
            // pin except engaging a follow). The pin is the ANCHOR now and the
            // hand's offset is relative to it — so the first pixel of a drag
            // moves the map, and CENTRE still means dead centre (both writers
            // of _mmAsk reset the offset).
            _cam.GlobalPosition = _mmAsk.Value + _reviewPan;
        }
        else
        {
            if (_overrideCenter != null)
            {
                _cam.GlobalPosition = _overrideCenter.Value;
                src = "--center (hand)";
            }
            else
                _cam.GlobalPosition = W((float)cx, (float)cy);
            _cam.GlobalPosition += _reviewPan;   // the operator's pan, on top
        }
        // Once per CAUSE, not once per run: the answer to "is the recording's
        // own focus driving the view, and did it change at the test boundary"
        // is a trace of these lines, and a single startup line cannot show a
        // switch. The numbers are honest about "where is the view", which a
        // downscaled screenshot cannot.
        if (src != _camSrc)
        {
            _camSrc = src;
            GD.Print($"REVIEW-CAM src={src} godot=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2}) " +
                     $"zoom={_cam.Zoom.X:F1} sim=({cx:F2},{cy:F2}) " +
                     $"cars={fr.Cars.Count} frame {fr.Step}");
        }
    }
}

/// <summary>The scrub bar's seek surface (#3 of the 2026-10-22 review list).
/// The bar's x IS an address — a fraction of the tape — so a click on it has
/// to move the cursor. Until now the curve was decoration: reaching a moment
/// meant ▶ and waiting, or pressing '.' a few hundred times.
///
/// Click and drag both seek (a drag is a scrub), and the zone reports the
/// FRACTION rather than a frame index: the tape's length is the renderer's
/// business, and the zone is laid out per frame anyway. The buttons sit on
/// later children of the same layer, so they keep their own clicks.</summary>
public partial class ReviewScrubZone : Control
{
    /// <summary>(fraction of the tape, true while dragging) - wired to
    /// MapRenderer.ReviewSeek.</summary>
    public System.Action<double, bool> Seek;
    /// <summary>(zoom in?, bar x) — the bar's own wheel zoom, wired to
    /// MapRenderer.BarZoom. The x is what stays under the cursor while the
    /// range stretches.</summary>
    public System.Action<bool, float>? Zoom;
    /// <summary>() — double-click: back to the whole run.</summary>
    public System.Action? ZoomReset;
    /// <summary>(pointer) — hover, for the event markers' tooltip.</summary>
    public System.Action<Vector2>? Hover;
    /// <summary>() — click on a padded event marker: jump to its step.</summary>
    public System.Func<Vector2, bool>? MarkerClick;

    private bool _drag;

    public ReviewScrubZone()
    {
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton m && m.ButtonIndex == MouseButton.Left)
        {
            if (m.Pressed)
            {
                // A click ON an event marker is a JUMP to that moment, not a
                // seek to the pixel — the markers are the interesting seconds,
                // and the padded hit area exists so they can be aimed at.
                if (MarkerClick?.Invoke(G(m.Position)) == true) { AcceptEvent(); return; }
                _drag = true;
                Seek?.Invoke(TapeFracAt(m.Position.X), true);
                AcceptEvent();
            }
            else if (_drag)
            {
                _drag = false;
                Seek?.Invoke(TapeFracAt(m.Position.X), false);
                AcceptEvent();
            }
        }
        else if (e is InputEventMouseButton mw && mw.Pressed &&
                 (mw.ButtonIndex == MouseButton.WheelUp || mw.ButtonIndex == MouseButton.WheelDown))
        {
            // Wheel over the bar = ZOOM THE BAR: stretching the range buys
            // detail (a decision hairline that was 0.3 px wide becomes 4 px).
            // The map's zoom is a different question and stays untouched.
            Zoom?.Invoke(mw.ButtonIndex == MouseButton.WheelUp, mw.Position.X);
            AcceptEvent();
        }
        else if (e is InputEventMouseButton md && md.Pressed &&
                 md.ButtonIndex == MouseButton.Left && md.DoubleClick)
        {
            ZoomReset?.Invoke();
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (_drag) Seek?.Invoke(TapeFracAt(mm.Position.X), true);
            else Hover?.Invoke(G(mm.Position));
        }
    }

    /// <summary>x → fraction of the WHOLE tape, through the bar's current
    /// zoom range (set by MapRenderer with BarTapeFracAtX).</summary>
    public System.Func<float, double>? FracAt;
    private double TapeFracAt(float x) => FracAt?.Invoke(x + Gx) ?? 0.0;

    /// <summary>_GuiInput hands out LOCAL coordinates; the bar's geometry (the
    /// curve, the marker lane, the boundary ticks) is laid out in the MAIN
    /// window's space, whose bar starts at x=16. Comparing the two without the
    /// offset costs exactly that 16 px — enough to make an event marker's padded
    /// hit area miss the mark you clicked on (measured: a click dead-centre on a
    /// marker seeked instead of jumping).</summary>
    float Gx => GetGlobalTransform().Origin.X;
    private Vector2 G(Vector2 local) => local + new Vector2(Gx, 0f);
}
