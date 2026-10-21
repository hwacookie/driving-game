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
    /// <summary>One HUD scale for the whole window, from its height. The
    /// reviewer is read by an operator over a shared screen, not squinted
    /// at from 30 cm away - a fixed 17 px label is unreadable in a 1560 px
    /// window and the same window on a hidpi display is worse. Everything
    /// the HUD draws (fonts AND the boxes that hold them) scales together,
    /// so the panels never clip the text they carry.</summary>
    private float _hudS = 1.0f;
    private float _hudLabelBottom = 96.0f;   // where the readout ends, slots start below
    private int _playDbg;
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
    private bool _btnWasHeld;          // scrub button down on the previous frame
    private double _holdSec;             // how long a scrub key has been held
    private double _scrubAcc;            // fractional steps carried over
    private double _scrubAcc2;           // play-mode accumulator
    private bool _reviewShotOnce;

    private Label _reviewLabel;
    private Label _camLabel;
    private double _hudW = 0.0;
    private double _hudX0 = 0.0;
    private CanvasLayer _hudLayer;
    private Panel _bar;
    private Line2D _curveLine, _cursorTick, _headTick;
    private readonly List<Button> _buttons = new();
    private readonly List<Polygon2D> _markerPool = new();
    private readonly List<Panel> _slotPanels = new();
    private readonly List<Label> _slotLabels = new();
    private readonly List<long> _selected = new();   // last-clicked first
    private readonly List<Line2D> _markTicks = new();

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
    /// run order.</summary>
    private static string NewestTape()
    {
        // The run picker: the newest tape this project wrote. File names carry
        // the run stamp, so alphabetical order IS run order.
        var d = DirAccess.Open("res://tests");
        if (d == null) return "";
        var bestName = "";
        var bestPath = "";
        foreach (var n in d.GetFiles())
        {
            if (!n.StartsWith("tape_") || !n.EndsWith(".txt")) continue;
            // Names carry the run stamp, so a plain comparison orders runs.
            if (n.CompareTo(bestName) > 0) { bestName = n; bestPath = "res://tests/" + n; }
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
            runs.Add("res://tests/" + n);
        }
        runs.Sort();
        if (runs.Count > 6) runs = runs.Slice(runs.Count - 6, 6);   // Slice is (begin, LENGTH)
        return runs;
    }

    private void BuildRunPicker()
    {
        var runs = ListRuns();
        GD.Print($"REVIEW-RUNS found {runs.Count} tapes in res://tests");
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
            btn.AddThemeFontSizeOverride("font_size", (int)(23 * _hudS));
            btn.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            btn.OffsetLeft = x; btn.OffsetTop = y;
            btn.OffsetRight = (float)vp.Size.X - 12.0f; btn.OffsetBottom = y + 30.0f * _hudS;
            var p = path;
            btn.Pressed += () => ReviewRunSelect(p);
            _hudLayer.AddChild(btn);
            y += 34.0f * _hudS;
        }
    }

    private void ReviewRunSelect(string path)
    {
        GD.Print($"REVIEW-RUN selected {path}");
        // A new run is a new tape: reload, rewind, and re-point the camera.
        // The window itself survives - it is long-lived, the run is not.
        _reviewPath = path;
        _tape.Clear();
        if (!LoadTape(path)) return;
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
        var vpHud = GetViewport().GetVisibleRect();
        _hudS = Mathf.Clamp((float)vpHud.Size.Y / 819.0f, 0.85f, 2.2f);
        _hudLayer = new CanvasLayer { Layer = 11 };
        AddChild(_hudLayer);
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(16 / 255f, 16 / 255f, 22 / 255f, 190f / 255f),
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 5, ContentMarginBottom = 5,
        };
        _reviewLabel = new Label();
        _reviewLabel.AddThemeStyleboxOverride("normal", sb);
        _reviewLabel.AddThemeColorOverride("font_color",
            new Color(235 / 255f, 235 / 255f, 235 / 255f));
        _reviewLabel.AddThemeFontSizeOverride("font_size", (int)(28 * _hudS));
        // The readout is one long sentence - at a scaled font it no longer
        // fits across the window, so it wraps inside a box that spans it
        // instead of running off the right edge.
        _reviewLabel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _reviewLabel.OffsetLeft = 12.0f; _reviewLabel.OffsetTop = 12.0f;
        _reviewLabel.OffsetRight = (float)vpHud.Size.X - 12.0f;
        _reviewLabel.OffsetBottom = 12.0f + 3.0f * 28.0f * _hudS;
        _hudLabelBottom = _reviewLabel.OffsetBottom;   // the slots start below it
        _reviewLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hudLayer.AddChild(_reviewLabel);

        _camLabel = new Label();
        _camLabel.AddThemeStyleboxOverride("normal", sb);
        _camLabel.AddThemeColorOverride("font_color",
            new Color(190 / 255f, 210 / 255f, 235 / 255f));
        _camLabel.AddThemeFontSizeOverride("font_size", (int)(25 * _hudS));
        _camLabel.Position = new Vector2(12, 34);
        _hudLayer.AddChild(_camLabel);

        // The bar itself: a dark strip along the bottom, so the curve, the
        // cursor and the buttons read as ONE instrument instead of a hairline
        // floating on grass.
        _bar = new Panel();
        var barSb = new StyleBoxFlat
        {
            BgColor = new Color(14 / 255f, 16 / 255f, 22 / 255f, 215f / 255f),
        };
        _bar.AddThemeStyleboxOverride("normal", barSb);
        _hudLayer.AddChild(_bar);

        _curveLine = new Line2D { Width = 2.0f, DefaultColor = new Color(0.45f, 0.85f, 0.95f) };
        _hudLayer.AddChild(_curveLine);
        _cursorTick = new Line2D { Width = 2.0f, DefaultColor = new Color(1.0f, 1.0f, 1.0f) };
        _hudLayer.AddChild(_cursorTick);
        _headTick = new Line2D { Width = 1.0f, DefaultColor = new Color(0.55f, 0.55f, 0.55f) };
        _hudLayer.AddChild(_headTick);
        for (int i = 0; i < 8; i++)
        {
            var t = new Line2D { Width = 1.5f };
            _hudLayer.AddChild(t);
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
            _hudLayer.AddChild(poly);
            _markerPool.Add(poly);
        }

        var glyphs = new List<string> { "▶", "⏸", "⏹", "●", "◀", "▶▶" };
        var actions = new List<string> { "play", "pause", "stop", "record", "back", "fwd" };
        float bx = 16.0f;
        for (int i = 0; i < glyphs.Count; i++)
        {
            var btn = new Button { Text = glyphs[i] };
            btn.AddThemeFontSizeOverride("font_size", (int)(38 * _hudS));
            // Anchored to the BOTTOM-LEFT, so the buttons sit inside the scrub
            // bar itself - a floating row at the top collided with the readout
            // label and read as part of it.
            btn.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            btn.OffsetLeft = bx; btn.OffsetTop = -132.0f * _hudS;
            btn.OffsetRight = bx + 56.0f * _hudS; btn.OffsetBottom = -88.0f * _hudS;
            var act = actions[i];
            btn.Pressed += () => ReviewButton(act);
            // The scrub buttons keep the direction held down, so the ramp
            // curve applies to the button exactly as it does to the key:
            // press starts the scrub, release ends it.

            _hudLayer.AddChild(btn);
            _buttons.Add(btn);
            bx += 64.0f * _hudS;
        }
        // Playback rate is part of the bar, not keyboard-only: someone
        // watching over a shared screen has to be able to say "run it at
        // 4x" without knowing the number keys.
        var shotBtn = new Button { Text = "📷" };
        shotBtn.AddThemeFontSizeOverride("font_size", (int)(38 * _hudS));
        shotBtn.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        shotBtn.OffsetLeft = bx; shotBtn.OffsetTop = -132.0f * _hudS;
        shotBtn.OffsetRight = bx + 56.0f * _hudS; shotBtn.OffsetBottom = -88.0f * _hudS;
        shotBtn.Pressed += () => ReviewShot();
        _hudLayer.AddChild(shotBtn);
        bx += 64.0f;

        var rates = new List<double> { 0.5, 1.0, 2.0, 4.0, 8.0 };
        foreach (var r in rates)
        {
            var sbtn = new Button { Text = (r < 1.0 ? "1/2x" : ((int)r).ToString() + "x") };
            sbtn.AddThemeFontSizeOverride("font_size", (int)(28 * _hudS));
            sbtn.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            sbtn.OffsetLeft = bx; sbtn.OffsetTop = -126.0f * _hudS;
            sbtn.OffsetRight = bx + 52.0f * _hudS; sbtn.OffsetBottom = -92.0f * _hudS;
            var rr = r;
            sbtn.Pressed += () => { _playRate = rr; _playing = true; };
            _hudLayer.AddChild(sbtn);
            bx += 60.0f * _hudS;
        }
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

    private void ReviewTick(double delta)
    {
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
    }

    /// <summary>The two-line readout: the moment AND the view. A screenshot is
    /// only reconstructable if the crop and the scale are on it - a picture
    /// alone cannot say which part of the world it shows or how wide a painted
    /// metre is.</summary>
    private void ReviewLabels()
    {
        if (_reviewLabel == null) return;
        var fr = _tape.Count > 0 ? _tape[_cursor] : null;
        long step = fr != null ? fr.Step : 0;
        double simS = step * _tapeDt;
        _reviewLabel.Text =
            $"{_tapeName} · frame {step} [{_cursor}/{_tape.Count - 1}] · " +
            $"SIM {simS:F3} s · map {_tapeMap} #{_tapeHash} · " +
            $"fluidity {(fr != null ? fr.Fluidity : 0.0):F2} m/s/car · " +
            $"{(_playing ? "▶ " + _playRate.ToString() + "x" : "⏸ step")}";
        // Godot's 2D y grows DOWN while the sim's world y grows north, so the
        // camera readout flips back: the label must state the SIM frame.
        var camPos = _cam.GlobalPosition;
        _camLabel.Text =
            $"cam x {camPos.X:F2} m  y {(-camPos.Y):F2} m  zoom {_cam.Zoom.X:F1} px/m  ·  " +
            $"view: {(_playing ? "play (lerp between recorded frames)" : "step (exact substep)")}  ·  units: meters";
    }

    private void ReviewHud()
    {
        if (_hudLayer == null || _curveLine == null) return;
        var vp = GetViewport().GetVisibleRect();
        double barW = vp.Size.X - 32.0;
        double barX = 16.0;
        double barBase = vp.Size.Y - 46.0;
        _hudW = barW; _hudX0 = barX;
        if (_bar != null)
        {
            _bar.OffsetLeft = 8; _bar.OffsetTop = (float)(barBase - 78.0 * _hudS);
            _bar.OffsetRight = (float)(vp.Size.X - 8.0);
            _bar.OffsetBottom = (float)(barBase + 10.0);
        }
        // The fluidity curve over the whole tape - what makes two runs
        // comparable, and what the event markers are pinned to.
        var pts = new List<Vector2>(_tape.Count);
        double maxF = 0.1;
        foreach (var f in _tape) maxF = Math.Max(maxF, f.Fluidity);
        int i = 0;
        foreach (var f in _tape)
        {
            double x = barX + barW * (i / Math.Max(1.0, _tape.Count - 1.0));
            double y = barBase - 56.0 * (f.Fluidity / maxF);
            pts.Add(new Vector2((float)x, (float)y));
            i++;
        }
        _curveLine.Points = pts.ToArray();
        // cursor + head ticks
        double cx = barX + barW * (_cursor / Math.Max(1.0, _tape.Count - 1.0));
        _cursorTick.Points = new Vector2[] {
            new Vector2((float)cx, (float)(barBase - 60.0)), new Vector2((float)cx, (float)barBase) };
        _headTick.Points = new Vector2[] {
            new Vector2((float)(barX + barW), (float)(barBase - 60.0)), new Vector2((float)(barX + barW), (float)barBase) };
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

    private void ReviewInput(InputEvent e)
    {
        ReviewSelect(e);
        if (!(e is InputEventKey k) || !k.Pressed || k.Echo) return;
        // Bound by PHYSICAL position, so the gesture survives other layouts:
        // the two keys right of M step one substep back / forward.
        if (k.Keycode == Key.Comma)
        { _playing = false; _cursor = Math.Max(0, _cursor - 1); _scrubAcc = 0.0; }
        else if (k.Keycode == Key.Period)
        { _playing = false; _cursor = Math.Min(_tape.Count - 1, _cursor + 1); _scrubAcc = 0.0; }
        else if (k.Keycode == Key.Right) _scrubDir = 1.0;
        else if (k.Keycode == Key.Left) _scrubDir = -1.0;
        else if (k.Keycode == Key.Space) { _playing = false; _scrubDir = 0.0; }
        else if (k.Keycode == Key.R) { _playing = true; _scrubAcc2 = 0.0; }
        else if (k.Keycode == Key.Kp1) _playRate = 0.5;
        else if (k.Keycode == Key.Kp2) _playRate = 1.0;
        else if (k.Keycode == Key.Kp3) _playRate = 2.0;
        else if (k.Keycode == Key.Kp4) _playRate = 4.0;
        else if (k.Keycode == Key.Kp5) _playRate = 8.0;
        else if (k.Keycode == Key.F8 && !k.Echo) ReviewShot();
        if (k.Keycode is Key.Left or Key.Right) _scrubDir = 0.0;   // release stops the ramp
    }

    /// <summary>The whole reviewer window as one PNG, named against the tape and
    /// the frame, and copied to the clipboard. The label is INSIDE the shot, so
    /// the image carries its own address.</summary>
    private void ReviewShot()
    {
        var fr = _tape.Count > 0 ? _tape[_cursor] : null;
        long step = fr != null ? fr.Step : 0;
        string path = $"res://tests/review_{_tapeName}_f{step}.png";
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
    /// <summary>The three detail slots (docs/SIMULATIONS_REVIEWER.md): the
    /// three last-clicked cars, and until anything is clicked the first three
    /// by uid - the panels are useful before the first click.</summary>
    private void BuildReviewSlots()
    {
        // The window is whatever size it is - a 960 px window cannot hold
        // three 360 px panels, so the row shares the width.
        var vp0 = GetViewport().GetVisibleRect();
        // The box must hold SEVEN lines at the scaled font, or the panel
        // clips its own text (measured: at a hidpi window the last two
        // lines - "I hold" and "decisions" - were cut off).
        float slotFont = 26.0f * _hudS;
        float slotW = Mathf.Max(240.0f, ((float)vp0.Size.X - 36.0f) / 3.0f);
        float slotH = slotFont * 10.5f;   // seven lines at the scaled font
        for (int i = 0; i < 3; i++)
        {
            var panel = new Panel();
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(14 / 255f, 16 / 255f, 22 / 255f, 205f / 255f),
            };
            panel.AddThemeStyleboxOverride("normal", sb);
            panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            panel.OffsetLeft = 12.0f + i * slotW; panel.OffsetTop = _hudLabelBottom + 10.0f;
            panel.OffsetRight = 12.0f + (i + 1) * slotW;
            panel.OffsetBottom = _hudLabelBottom + 10.0f + slotH;
            _hudLayer.AddChild(panel);
            _slotPanels.Add(panel);
            var lab = new Label();
            lab.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            lab.OffsetLeft = 18.0f + i * slotW; lab.OffsetTop = _hudLabelBottom + 14.0f;
            lab.OffsetRight = 6.0f + (i + 1) * slotW;
            lab.OffsetBottom = _hudLabelBottom + 8.0f + slotH;
            lab.AddThemeFontSizeOverride("font_size", (int)(26 * _hudS));
            lab.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.95f));
            // The panel is 288 px of text at a 12 px font - a decision line
            // longer than that used to spill into the NEXT slot and read as
            // part of it. Wrap inside the box, and cap the box: the count on
            // the line says how many there were, scrubbing walks the rest.
            lab.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            lab.ClipText = true;
            lab.Text = "—";
            _hudLayer.AddChild(lab);
            _slotLabels.Add(lab);
        }
    }

    /// <summary>The review window owns its camera: the view sits on the tape
    /// frame's cars (their centroid), so the manoeuvre under the cursor is
    /// always in the box; an explicit --center outranks it. The numbers are
    /// printed once per cursor change because a downscaled screenshot cannot
    /// answer "is the road where the label says it is" by eye.</summary>
    /// <summary>A click selects the nearest car - the three last clicked fill
    /// the detail slots. Selection is by the DRAWN pose, which the pose check
    /// already proved equals the tape frame.</summary>
    /// <summary>A click selects the nearest car - the three last clicked fill
    /// the detail slots. Selection is by the DRAWN pose, which the pose check
    /// already proved equals the tape frame.</summary>
    private void ReviewSelect(InputEvent e)
    {
        if (!(e is InputEventMouseButton m) || !m.Pressed) return;
        if (m.ButtonIndex != MouseButton.Left) return;
        var vp = GetViewport().GetVisibleRect();
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
            if (_clickDbg < 4) { _clickDbg++; GD.Print($"REVIEW-PICK miss at ({m.Position.X:F0},{m.Position.Y:F0}) world=({world.X:F1},{world.Y:F1})"); }
            return;
        }
        if (_clickDbg < 4) { _clickDbg++; GD.Print($"REVIEW-PICK hit uid {best} at ({m.Position.X:F0},{m.Position.Y:F0})"); }
        var next = new List<long>();
        next.Add(best);
        foreach (var u in _selected)
            if (u != best && next.Count < 3) next.Add(u);
        _selected.Clear();
        foreach (var u in next) _selected.Add(u);
    }

    private void ReviewSlots()
    {
        if (_slotLabels.Count == 0) return;
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
        for (int i = 0; i < _slotLabels.Count; i++)
        {
            if (i >= _selected.Count) { _slotLabels[i].Text = "—"; continue; }
            var uid = _selected[i];
            TapeCarRev cf = null;
            foreach (var c2 in fr.Cars) if (c2.Uid == uid) cf = c2;
            if (cf == null) { _slotLabels[i].Text = $"#{uid}  (not on this frame)"; continue; }
            var color = _carColors.GetValueOrDefault(uid, "");
            var (w, l) = DrivingGame.Sim.Config.SizeForColor(color);
            string cls = DrivingGame.Sim.Config.VEHICLE_CLASS.GetValueOrDefault(color, "car");
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
                $"#{uid} {NameOf(uid)} {color} · {cls}",
                $"{l:F2} × {w:F2} m",
                $"speed {cf.SpeedMps * 3.6:F1} km/h · brake {GlyphOn(cf.Brake, "YES", "-")} · blink {GlyphOn((cf.Blinkers & 1) > 0, "L", GlyphOn((cf.Blinkers & 2) > 0, "R", "-"))}",
                $"x {cf.X:F2}  y {cf.Y:F2}  h {cf.HeadingDeg:F1}°",
                $"holds me: {OrText(holds, "-")}",
                $"I hold:   {OrText(held, "-")}",
                $"decisions: {DecisionTrail(uid, fr.Step)}",
            };
            _slotLabels[i].Text = string.Join("\n", lines);
        }
    }

    /// <summary>The role the scenario gave this car ("slow_car", "parked_car")
    /// from the tape header - empty when the cast was never named. The number
    /// the suite shows for the primary is not a role, so it stays out.</summary>
    private string NameOf(long uid)
    {
        var n = _tapeNames.TryGetValue(uid, out var v) ? v : "";
        if (n == "" || n.Contains("/")) return "";   // "18/27" is a label, not a role
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
        // The markers depend on the SELECTION, not on the cursor - the bar
        // shows the whole run. Re-rendering them every frame walked the tape
        // 35 900 times a second; now they are redrawn only when the set of
        // selected cars changes.
        long selHash = 0;
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
                new Vector2((float)x1, (float)(barBase - 2.0)),
                new Vector2((float)x0, (float)(barBase - 2.0)),
            };
            poly.Color = col;
        }
        // Fleet-level lane (thin, at the very bottom): a standstill ANYWHERE
        // in the fleet, so a slow stretch reads as traffic, not as an error
        // on one car. Per-car markers sit above it in their own colours.
        // Fleet-level standstill band: a substep where NO car is moving. The
        // per-frame scan of the whole tape is what made the window crawl, so
        // this is precomputed into _fleetStops at index time.
        foreach (var s in _fleetStops) quad(s, s + 1, new Color(0.75f, 0.62f, 0.2f, 0.35f), 6.0);
        // The index, not the tape: per selected car, walk only its own runs
        // and event steps (a handful each), never all 35 905 frames.
        foreach (var uid in _selected)
        {
            var runs = _stillRuns.TryGetValue(uid, out var rl) ? rl : null;
            if (runs != null)
                for (int i = 0; i + 1 < runs.Count; i += 2)
                    quad(runs[i], runs[i + 1], new Color(0.98f, 0.82f, 0.25f, 0.85f), 20.0);
            var wrong = _wrongSteps.TryGetValue(uid, out var wl) ? wl : null;
            if (wrong != null)
                foreach (var s in wrong) quad(s, s, new Color(0.35f, 0.6f, 1.0f), 22.0);
            var crash = _crashSteps.TryGetValue(uid, out var cl2) ? cl2 : null;
            if (crash != null)
                foreach (var s in crash) quad(s, s, new Color(1.0f, 0.25f, 0.2f), 22.0);
        }
        // Decision hairlines from the index too: every step where ANY car
        // decided, merged into one sorted list.
        var decAll = new List<long>();
        foreach (var kv in _decSteps) foreach (var s in kv.Value) decAll.Add(s);
        decAll.Sort();
        for (int i = 0; i < decAll.Count && i < 400; i++)
            quad(decAll[i], decAll[i], new Color(0.7f, 0.7f, 0.75f, 0.22f), 46.0);
    }

    private readonly Dictionary<long, string> _carColors = new();

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
        if (_overrideCenter != null)
            _cam.GlobalPosition = _overrideCenter.Value;
        else
            _cam.GlobalPosition = W((float)cx, (float)cy);
        // Once per run: the numbers answer "where is the view" honestly, which
        // a downscaled screenshot cannot.
        if (!_camPrinted)
        {
            _camPrinted = true;
            GD.Print($"REVIEW-CAM godot=({_cam.GlobalPosition.X:F2},{_cam.GlobalPosition.Y:F2}) " +
                     $"zoom={_cam.Zoom.X:F1}  tape-centroid=({cx:F2},{cy:F2}) cars={fr.Cars.Count} frame {fr.Step}");
        }
    }
}
