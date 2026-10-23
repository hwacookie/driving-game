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
    private float _slotRowY = -1e9f;        // readout bottom the slot row sits under
    private int _seekDbg;
    private int _fontDbg;       // calls until the font probe prints (text must exist by then)
    private int _readoutFont;     // the size the readout is laid out at
    private double _zoneTop = 0.0, _zoneBot = 0.0;   // the seek band's y range
    private bool _testSeekFired;                     // --test-seek: once per run
    private bool _testScrubDragFired;                // --test-scrub-drag: once per run
    private Line2D _curveLine, _cursorTick, _headTick;
    private readonly List<Button> _buttons = new();
    private readonly List<Polygon2D> _markerPool = new();
    private readonly List<SlotWin> _slots = new();
    private readonly List<long> _selected = new();   // last-clicked first
    private readonly List<Line2D> _markTicks = new();

    /// <summary>One car's detail window (#5 of the 2026-10-22 review list): a
    /// real window with a title bar naming the car, draggable, closable,
    /// resizable - plus what WE last asked of it, so the operator's own
    /// placement is never fought by a relayout.</summary>
    private sealed class SlotWin
    {
        public Window Win; public Label Lab; public ColorRect Bg;
        public bool Closed;    // its × was used - only a new pick reopens it
        public long UidShown = -1L;
        // The two small controls in the window's LOWER-RIGHT corner (binding
        // user decision 2026-10-23): follow is a TOGGLE - on, the camera
        // follows this car, the same binding pressing F makes in the viewer,
        // and turning one on turns the others off. centre is MOMENTARY - the
        // press puts the view on this car and that is all it does.
        public Button FollowBtn; public Button CenterBtn;
        public bool Following;     // the follow toggle's own state
    }

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
        // The readout window is NOT torn down here: it is an OS window, and
        // minting a new one per rebuild is what stacked "readout" title bars
        // during a resize. The label lives inside it and keeps its text -
        // only the font budget is re-probed.
        _readoutFont = 0;
        _markerForSelected = -1L;      // the lane must be laid out for the new bar
        BuildReviewHud();
        BuildRunPicker();
        // The open windows follow the row, and the row follows the readout -
        // which the loop in ReviewSlots does once the new size has settled.
        _slotRowY = -1e9f;
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
    private bool LoadTape(string path)
    {
        _tape.Clear();
        string text;
        try { text = File.ReadAllText(path); }
        catch { return false; }
        _tapeName = path.Split("/")[^1];
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
                    // Three field generations: no colour, colour + capKey,
                    // colour + lamps + capKey. Accept all three rather than
                    // refusing the run.
                    Color = fld.Length >= 11 ? fld[9] : "",
                    Lamps = fld.Length >= 12 ? (int)ParseNum(fld[10], 0.0) : 0,
                    CapKey = fld.Length >= 12 ? string.Join(",", fld, 11, fld.Length - 11)
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
        // Uids that vanished between frames (a despawn) must not stay on screen.
        foreach (var uid in _carBuf.Keys.ToList())
            if (!seen.Contains(uid)) _carBuf[uid].Clear();

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
        ReviewSlots();
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
        int i = 0;
        foreach (var f in _tape)
        {
            double x = barX + barW * (i / Math.Max(1.0, _tape.Count - 1.0));
            double y = baseline - amp * (f.Fluidity / maxF);
            pts.Add(new Vector2((float)x, (float)y));
            i++;
        }
        _curveLine.Points = pts.ToArray();
        // cursor + head ticks
        double cx = barX + barW * (_cursor / Math.Max(1.0, _tape.Count - 1.0));
        cx = Math.Clamp(cx, barX + 3.0, barX + barW - 3.0);   // never half outside
        _cursorTick.Points = new Vector2[] {
            new Vector2((float)cx, (float)(baseline - amp)), new Vector2((float)cx, (float)barBase) };
        _headTick.Points = new Vector2[] {
            new Vector2((float)(barX + barW - 3.0), (float)(baseline - amp)),
            new Vector2((float)(barX + barW - 3.0), (float)barBase) };
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

    private void ReviewSelect(InputEvent e)
    {
        if (!(e is InputEventMouseButton m) || !m.Pressed) return;
        if (m.ButtonIndex != MouseButton.Left) return;
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
            GD.Print($"REVIEW-PICK hit uid {best} at ({m.Position.X:F0},{m.Position.Y:F0})");
        }
        var next = new List<long>();
        next.Add(best);
        foreach (var u in _selected)
            if (u != best && next.Count < 3) next.Add(u);
        _selected.Clear();
        foreach (var u in next) _selected.Add(u);
    }

    private void ReviewInput(InputEvent e)
    {
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
    /// <summary>The three detail windows (docs/SIMULATIONS_REVIEWER.md): the
    /// three last-clicked cars, and until anything is clicked the first three
    /// by uid - the panels are useful before the first click.</summary>
    private void BuildReviewSlots()
    {
        // THREE REAL WINDOWS. Each car gets a window whose TITLE BAR names the
        // car, with a close button of its own, draggable aside and resizable -
        // an operator comparing two cars puts them next to each other instead
        // of reading three fixed columns painted on the map.
        //
        // The earlier attempt at this gave up ("an embedded Window ignores the
        // position asked of it, its children landed outside the frame"): the
        // children had been placed in MAIN-window coordinates INSIDE the
        // subwindow's own viewport. A subwindow is a Viewport - its children
        // are positioned in ITS coordinates, and a full-rect anchor is what
        // follows it when the operator resizes it.
        SlotRowSize(out float slotW, out float slotH);   // the row's one rule
        for (int i = 0; i < 3; i++)
        {
            var w = new Window
            {
                Title = "car —",
                Transient = true,          // belongs to this window, not to the app
                Visible = false,           // until a car owns the slot
                Size = new Vector2I((int)slotW, (int)slotH),
                Position = new Vector2I((int)(12.0f + i * slotW),
                                        (int)(_hudLabelBottom + 10.0f)),
            };
            AddChild(w);
            RegisterWindow(w);   // the guard in MapRenderer.cs keeps it inside
            // the main window and WinGapPx from the other windows
            var layer = new CanvasLayer { Layer = 1 };
            w.AddChild(layer);
            // The window's own background is the theme's; this is the panel
            // colour the review surface uses everywhere.
            var bg = new ColorRect
            {
                Color = new Color(14 / 255f, 16 / 255f, 22 / 255f, 250f / 255f),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(bg);
            var lab = new Label
            {
                Text = "—",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                ClipText = true,
            };
            lab.AddThemeFontSizeOverride("font_size", SlotFontPx);
            lab.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.95f));
            layer.AddChild(lab);
            // The label keeps the bottom strip clear for the two corner
            // controls - a decision line that ends under a button is a line
            // that cannot be read.
            lab.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            lab.Position = new Vector2(12f, 10f);
            lab.Size = new Vector2((float)slotW - 24f,
                                   (float)(slotH - (SlotBtnH + SlotBtnInset + 14f)));
            // The two small controls in the LOWER-RIGHT corner - the corner
            // farthest from the title bar the window is read by, and the one
            // the decision trail never reaches (its lines are top-anchored).
            // Anchored to the window's bottom-right, so a drag or a resize of
            // the window carries them along instead of leaving them on the map
            // - the same rule the panel inside uses.
            var followBtn = new Button
            {
                Text = "follow",
                TooltipText = "follow this car - on/off; only one car is followed",
                FocusMode = Control.FocusModeEnum.None,
            };
            followBtn.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            followBtn.OffsetLeft = -(SlotBtnW * 2f + SlotBtnGap + SlotBtnInset);
            followBtn.OffsetRight = -(SlotBtnW + SlotBtnGap + SlotBtnInset);
            followBtn.OffsetTop = -(SlotBtnH + SlotBtnInset);
            followBtn.OffsetBottom = -SlotBtnInset;
            followBtn.AddThemeFontSizeOverride("font_size", 17);
            // The follow STATE is this window's own (SlotWin.Following), not
            // the engine's pressed flag: this build's Button bindings expose
            // neither one_shot nor a way to force another button's state, and
            // the decision says switching one ON switches the OTHERS off -
            // which only an owner-side loop can do. The button paints the
            // state, so what you see and what the camera does cannot part.
            var fIdx = i;
            followBtn.Pressed += () => ToggleFollowSlot(fIdx);
            layer.AddChild(followBtn);
            var centerBtn = new Button
            {
                Text = "centre",
                TooltipText = "put the view on this car (momentary)",
                FocusMode = Control.FocusModeEnum.None,
            };
            // MOMENTARY by construction: the press runs CenterViewOnSlot once
            // and the button keeps no state of its own, so holding it cannot
            // do more than the first click did.
            centerBtn.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            centerBtn.OffsetLeft = -(SlotBtnW + SlotBtnInset);
            centerBtn.OffsetRight = -SlotBtnInset;
            centerBtn.OffsetTop = -(SlotBtnH + SlotBtnInset);
            centerBtn.OffsetBottom = -SlotBtnInset;
            centerBtn.AddThemeFontSizeOverride("font_size", 17);
            var cIdx = i;
            centerBtn.Pressed += () => CenterViewOnSlot(cIdx);
            layer.AddChild(centerBtn);

            var sw = new SlotWin
            {
                Win = w, Lab = lab, Bg = bg,
                FollowBtn = followBtn, CenterBtn = centerBtn,
            };
            int idx = i;
            w.CloseRequested += () => { _slots[idx].Closed = true; w.Visible = false; };
            _slots.Add(sw);
        }
    }

    /// <summary>Where the row goes: under the readout, three across what the
    /// map window leaves.</summary>
    private void SlotRowSize(out float slotW, out float slotH)
    {
        var vp = GetViewport().GetVisibleRect();
        float slotFont = SlotFontPx;
        slotW = Mathf.Max(300.0f, ((float)vp.Size.X - MmInsetWidth() - MmFrameBorder
                                   - 36.0f) / 3.0f);
        // Room for the eight lines a full slot can carry - a decision text
        // that wraps must not be clipped away, it is the reason to look -
        // plus the button strip under them, so the corner controls never eat
        // a line of the reading.
        slotH = slotFont * 12.0f + SlotBtnH + SlotBtnInset * 2f;
    }

    /// <summary>Place one slot window in the row. Asked for ON SHOW, not every
    /// frame: an embedded window reports back a position that includes its own
    /// decoration, so comparing the report against the request reads as a drag
    /// that never happened - which is how the row used to come out scattered
    /// (the old code's "a Window ignores the position asked of it").</summary>
    private void PlaceSlot(int i)
    {
        SlotRowSize(out float slotW, out float slotH);
        var s = _slots[i];
        s.Win.Position = new Vector2I((int)(12.0f + i * slotW),
                                      (int)(_hudLabelBottom + 10.0f));
        s.Win.Size = new Vector2I((int)slotW, (int)slotH);
        s.Lab.AddThemeFontSizeOverride("font_size", SlotFontPx);
    }


    private void ReviewSlots()
    {
        if (_slots.Count == 0) return;
        // The row hangs under the readout, so it follows that box's height.
        // The height is not known on the frame the window appears: the font
        // loop needs a few frames to settle, and a window placed against a
        // height that then grows ends up ON the address it should sit under.
        // The one thing this must not do is fight a drag - which it cannot,
        // because dragging moves a window, not the readout.
        if (Mathf.Abs(_hudLabelBottom - _slotRowY) > 2.0f)
        {
            _slotRowY = _hudLabelBottom;
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i].Win.Visible) PlaceSlot(i);
        }
        var fr = _tape[Math.Clamp(_cursor, 0, _tape.Count - 1)];
        if (_selected.Count == 0)
        {
            // The named car owns slot 1; the rest fill by uid order.
            if (_reviewUid != 0L) _selected.Add(_reviewUid);
            foreach (var cf in fr.Cars)
            {
                if (_selected.Count >= 3) break;
                if (cf.Uid == _reviewUid) continue;
                _selected.Add(cf.Uid);
            }
        }
        else if (_reviewUid != 0L && _selected[0] != _reviewUid)
        {
            // A named review keeps its subject in slot 1 for the whole scrub,
            // whatever else was clicked along the way.
            var keep = new List<long> { _reviewUid };
            foreach (var s2 in _selected)
                if (s2 != _reviewUid && keep.Count < 3) keep.Add(s2);
            _selected.Clear();
            foreach (var s2 in keep) _selected.Add(s2);
        }
        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];
            if (i >= _selected.Count)
            {
                // An empty slot is not an instrument: no car, no window.
                s.Win.Visible = false;
                s.UidShown = -1L;
                continue;
            }
            var uid = _selected[i];
            TapeCarRev cf = null;
            foreach (var c2 in fr.Cars) if (c2.Uid == uid) cf = c2;
            // A NEW car in this slot is an explicit ask: a window the operator
            // closed with its × comes back for it, an old one stays closed.
            bool shown = s.Win.Visible;
            if (s.UidShown != uid) { s.UidShown = uid; s.Closed = false; }
            s.Win.Visible = !s.Closed;
            // A window that APPEARS appears in the row; one that was already
            // open is the operator's to keep where they put it.
            if (!shown && !s.Closed) PlaceSlot(i);
            if (s.Closed) continue;                   // stays out of the way
            // The content follows the window, not the other way round: the
            // operator can resize it and the text reflows inside.
            var inner = s.Win.GetVisibleRect().Size;
            s.Lab.Position = new Vector2(10, 8);
            s.Lab.Size = new Vector2(Mathf.Max(120.0f, inner.X - 20.0f),
                                     Mathf.Max(40.0f, inner.Y - 16.0f));
            if (cf == null)
            {
                s.Win.Title = $"#{uid}  — not on this frame";
                s.Lab.Text = $"#{uid}  (not on this frame)";
                continue;
            }
            var color = _carColors.GetValueOrDefault(uid, "");
            var (w, l) = DrivingGame.Sim.Config.SizeForColor(color);
            string cls = DrivingGame.Sim.Config.VEHICLE_CLASS.GetValueOrDefault(color, "car");
            // Identity lives in the title bar - that is what a title bar is for.
            s.Win.Title = $"#{uid} {OrText(NameOf(uid), color)} · {cls} · {l:F2} × {w:F2} m";
            var holds = ""; var held = "";
            foreach (var c3 in fr.Cars)
            {
                if (c3.CapKey.EndsWith(uid.ToString()) && c3.Uid != uid)
                    holds = $"car {c3.Uid}  ({c3.CapKey})";
                if (cf.CapKey.EndsWith(c3.Uid.ToString()) && c3.Uid != uid)
                    held = $"car {c3.Uid}";
            }
            var lines = new List<string>
            {
                $"speed {cf.SpeedMps * 3.6:F1} km/h",
                $"brake {GlyphOn(cf.Brake, "yes", "off")} · blink {GlyphOn((cf.Blinkers & 1) > 0, "left", GlyphOn((cf.Blinkers & 2) > 0, "right", "off"))}",
                $"x {cf.X:F2}  y {cf.Y:F2}  h {cf.HeadingDeg:F1}°",
                $"holds me: {OrText(holds, "-")}",
                $"I hold:   {OrText(held, "-")}",
                $"decisions: {DecisionTrail(uid, fr.Step)}",
            };
            s.Lab.Text = string.Join("\n", lines);
        }
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
                _decSteps[ev.Uid].Add(f.Step);
                _decMsg[$"{ev.Uid}:{f.Step}"] = ev.Msg;
            }
        }
    }

    private string DecisionTrail(long uid, long step)
    {
        var dec = _decSteps.TryGetValue(uid, out var d) ? d : null;
        if (dec == null || dec.Count == 0) return "none";
        // Count the entries at or before the cursor (the list is in step
        // order, so a binary search is enough).
        int lo = 0, hi = dec.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (dec[mid] <= step) lo = mid + 1; else hi = mid;
        }
        if (lo == 0) return "none";
        var last = _decMsg[uid + ":" + dec[lo - 1]];
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
        if (_selected.Count == 0 || _tape.Count < 2) return;
        var vp = GetViewport().GetVisibleRect();
        double barBase = vp.Size.Y - 46.0;
        double barX = 16.0;
        double barW = vp.Size.X - 32.0;
        int slot = 0;
        void quad(double fromStep, double toStep, Color col, double h)
        {
            if (slot >= _markerPool.Count) return;
            double x0 = barX + barW * (fromStep / (_tape.Count - 1.0));
            double x1 = barX + barW * (toStep / (_tape.Count - 1.0));
            if (x1 - x0 < 1.5) x1 = x0 + 1.5;
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
        foreach (var s in _fleetStops) quad(s, s + 1, new Color(0.75f, 0.62f, 0.2f, 0.35f), 4.0);
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
                foreach (var s in crash) quad(s, s, new Color(1.0f, 0.25f, 0.2f), 12.0);
        }
        // Decision hairlines from the index too: every step where ANY car
        // decided, merged into one sorted list.
        var decAll = new List<long>();
        foreach (var kv in _decSteps) foreach (var s in kv.Value) decAll.Add(s);
        decAll.Sort();
        // Readable hairlines: at 0.22 alpha over a dark strip they vanished,
        // which made a decision-dense stretch look like a flat line.
        for (int i = 0; i < decAll.Count && i < 600; i++)
            quad(decAll[i], decAll[i], new Color(0.78f, 0.78f, 0.85f, 0.55f), 22.0);
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
        for (var i = 0; i < _markTicks.Count; i++)
        {
            var frac = (i + 1) / (_markTicks.Count + 1.0);
            var cx = barX + barW * frac;
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
            _axisLabel.Text =
            $"0 s .. {(_tape.Count * _tapeDt):F0} s sim · " +
            $"{Math.Round(_tape.Count / 10.0):F0} substeps per tick · " +
            $"curve 0 .. {_curveMaxF:F1} m/s/car";
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

    /// <summary>The car a slot window is following right now (0 = none): the
    /// slot's own toggle button is the state, so the three toggles cannot
    /// disagree about who is followed - a click on one releases the others,
    /// and the last one standing is the camera's car.</summary>
    private long SlotFollowUid()
    {
        for (var i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];
            if (s.FollowBtn == null || !s.Following) continue;
            if (s.UidShown <= 0L) continue;
            return s.UidShown;
        }
        return 0L;
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

    /// <summary>The centre button: one press puts the view dead on this
    /// window's car and nothing else. It reuses the hand-pin the minimap's
    /// inset writes, because that is already "centre here, do not follow" -
    /// and it is clamped like every other pin, so a car parked outside the
    /// roads plus 100 m cannot drag the view into empty country.</summary>
    private void CenterViewOnSlot(int i)
    {
        if (i < 0 || i >= _slots.Count) return;
        var uid = _slots[i].UidShown;
        var pose = CarPose(uid);
        if (pose == null)
        {
            GD.Print($"SLOT-CENTRE slot {i + 1} has no car at this frame - nothing to centre on");
            return;
        }
        _mmAsk = ClampViewCentre(pose.Value, "slot centre");
        _camPrinted = true;   // the one-shot log line is spent; the button says more
        GD.Print($"SLOT-CENTRE slot {i + 1} · car {uid} -> view on ({_mmAsk.Value.X:F2},{-_mmAsk.Value.Y:F2}) m (sim)");
    }

    /// <summary>The follow TOGGLE: a click flips this window's car between
    /// followed and not. Turning one ON switches the others off (binding
    /// user decision 2026-10-23), because the camera has one car to follow -
    /// two windows both insisting is not a thing the view can do.</summary>
    private void ToggleFollowSlot(int i)
    {
        if (i < 0 || i >= _slots.Count) return;
        var s = _slots[i];
        if (s.UidShown <= 0L)
        {
            // The buttons belong to a CAR window: an empty slot has no car to
            // follow, so its toggle must not claim the camera - and must not
            // switch the others off either, or it would silently steal a
            // followed car.
            GD.Print($"SLOT-FOLLOW slot {i + 1} has no car - nothing to follow (pick one first)");
            return;
        }
        s.Following = !s.Following;
        if (s.Following)
        {
            for (var other = 0; other < _slots.Count; other++)
                if (other != i && _slots[other].Following)
                {
                    _slots[other].Following = false;
                    PaintFollow(_slots[other]);
                    GD.Print($"SLOT-FOLLOW displaced slot {other + 1} (only one car is followed)");
                }
            GD.Print($"SLOT-FOLLOW slot {i + 1} ON · car {s.UidShown} · the view pins on it for the whole scrub");
        }
        else
            GD.Print($"SLOT-FOLLOW slot {i + 1} OFF");
        PaintFollow(s);
    }

    /// <summary>The button wears its state: an ON follow reads "ON" in the
    /// text colour the followed car uses everywhere, an OFF one stays dim -
    /// so the camera's one car and the corner you look at agree by paint.</summary>
    private void PaintFollow(SlotWin s)
    {
        if (s.FollowBtn == null) return;
        s.FollowBtn.Text = s.Following ? "follow ON" : "follow";
        s.FollowBtn.AddThemeColorOverride("text_color",
            s.Following ? new Color(1f, 0.85f, 0.3f) : new Color(0.62f, 0.62f, 0.68f));
    }

    private void ReviewCamera()
    {
        if (_cursor < 0 || _cursor >= _tape.Count) return;
        var fr = _tape[_cursor];
        double cx = 0.0, cy = 0.0;
        foreach (var cf in fr.Cars) { cx += cf.X; cy += cf.Y; }
        int n = Math.Max(1, fr.Cars.Count);
        cx /= n; cy /= n;
        // A named car pins the view on IT for the whole scrub - the centroid
        // of a cast drifts with every car that drives by, and the question
        // under review is about one of them.
        if (_reviewUid != 0L)
        {
            foreach (var cf in fr.Cars)
                if (cf.Uid == _reviewUid) { cx = cf.X; cy = cf.Y; }
        }
        // --center is stored in GODOT space (already flipped by W at parse),
        // the tape centroid is in SIM metres - one flip, not two. The log
        // proved it: want=(100,-275) arrived at godot=(100,+275), which is
        // grass, and the double flip is why.
        // A slot's follow toggle wins over every other pin: it says "stay on
        // THIS car", so the pin is the car's own pose at the frame under the
        // cursor - the same binding pressing F makes in the viewer, held for
        // the whole scrub. It also supersedes a spot the hand pinned in the
        // inset: following is not a place, it is a car.
        var followUid = SlotFollowUid();
        if (followUid != 0L) _mmAsk = null;
        var pose = followUid != 0L ? CarPose(followUid) : null;
        if (pose != null)
            _cam.GlobalPosition = pose.Value;
        else if (_mmAsk != null)
        {
            // The hand pinned the view on a spot of the map THROUGH the
            // minimap's inset: an absolute place, so it keeps its meaning
            // while the tape scrubs and the cast's centroid drifts - and it
            // already is the whole answer, so the drag offset is not added on
            // top of it.
            _cam.GlobalPosition = _mmAsk.Value;
        }
        else
        {
            if (_overrideCenter != null)
                _cam.GlobalPosition = _overrideCenter.Value;
            else
                _cam.GlobalPosition = W((float)cx, (float)cy);
            _cam.GlobalPosition += _reviewPan;   // the operator's pan, on top
        }
        // Once per run: the numbers answer "where is the view" honestly, which
        // a downscaled screenshot cannot.
        if (!_camPrinted)
        {
            _camPrinted = true;
            GD.Print($"REVIEW-CAM godot=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2}) " +
                     $"zoom={_cam.Zoom.X:F1}  view centred on {(_mmAsk != null ? "the hand" : "tape-centroid")} " +
                     $"({(_mmAsk != null ? _mmAsk.Value : W((float)cx, (float)cy)).X:F2},{(_mmAsk != null ? _mmAsk.Value : W((float)cx, (float)cy)).Y:F2}) " +
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
                _drag = true;
                Seek?.Invoke(Frac(m.Position.X), true);
                AcceptEvent();
            }
            else if (_drag)
            {
                _drag = false;
                Seek?.Invoke(Frac(m.Position.X), false);
                AcceptEvent();
            }
        }
        else if (e is InputEventMouseMotion mm && _drag)
            Seek?.Invoke(Frac(mm.Position.X), true);
    }

    private double Frac(float x) => Mathf.Clamp(x / Mathf.Max(1.0f, Size.X), 0.0f, 1.0f);
}
