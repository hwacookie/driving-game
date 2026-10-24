using Godot;
using System.Collections.Generic;

// CarDetailWindow.cs — the per-car inspector BOTH clients show
// (docs/CAR_DETAIL_WINDOW.md).
//
// One window per car, opened by right-click, closed by its own ×: identity in
// the title bar, the car's state at the frame the client is looking at, the
// holding chain in both directions, and the events that made it behave that
// way. The viewer feeds it from live `/state` + `GET /car/{uid}/decisions`,
// the reviewer from a tape frame + the tape's event index — the window knows
// neither: the client hands it a CarDetailData and wires the callbacks.
//
// Sizes are NAMED CONSTANTS, not a factor read off the window (binding user
// decision 2026-10-22, "One fixed HUD size": a maximized window shows the same
// readable text as the 900 px one).
//
// Children live in the SUBWINDOW's own coordinates — a Window is a Viewport.
// Placing them in main-window coordinates is what made an earlier attempt at
// this "come out scattered" (docs/SIMULATIONS_REVIEWER.md, review-list fix 5).

/// <summary>One row of a car's event list.</summary>
public sealed class CarDetailRow
{
    public long Step;              // the number in column 1 (tape step / sim step)
    public double SimS;            // run-relative seconds beside it
    public string Msg = "";
}

/// <summary>Everything the window paints, filled by whichever client owns it.
/// Rebuilt per frame is fine for the numbers; the row list is only re-rendered
/// when <see cref="RowStamp"/> changes.</summary>
public sealed class CarDetailData
{
    public long Uid;
    public string Name = "";
    public string VehicleClass = "";
    public double SpeedKmh;
    public bool Brake;
    public bool BlinkLeft, BlinkRight;
    public double X, Y, HeadingDeg;
    public string CapKey = "";                 // the rule owning the cap, "" = free
    public List<long> HoldsUp = new();         // left of the chain: their holder is me
    public List<long> HeldUpBy = new();        // right: me → …, the walk away from me
    public bool Lockdown;                      // R28's own verdict, not "cycle seen"
    public List<CarDetailRow> Rows = new();    // NEWEST FIRST
    public int RowsTotal;                      // rows the car has (>= Rows.Count)
    public string TruncatedNote = "";          // what RowsLeft says was dropped
    public int RowsLeft;
    public bool Following;
    public bool Seekable;                      // a tape can seek, a live run cannot
    /// <summary>What the row list is keyed on: anything that would change the
    /// rows. Recomputing 100 rows every frame is how the review window once
    /// ran at 2 fps — stamp it and rebuild on change only.</summary>
    public string RowStamp =>
        $"{Uid}|{Rows.Count}|{RowsTotal}|{RowsLeft}|{(Rows.Count > 0 ? Rows[0].Step : 0)}";
}

/// <summary>A small rotated sprite: the car's own PNG, nose along its heading.
/// <c>TextureRect</c> cannot rotate, and re-minting a <c>Sprite2D</c> per frame
/// inside a Control tree is the wrong tool — one <c>DrawSetTransform</c> is.</summary>
public partial class CarSpriteView : Control
{
    public Texture2D Tex;
    public float AngleDeg;

    public override void _Draw()
    {
        if (Tex == null) return;
        var sz = GetSize();
        DrawSetTransform(sz / 2.0f, Mathf.DegToRad(AngleDeg), Vector2.One);
        DrawTextureRect(Tex, new Rect2(-sz / 2.0f, sz), false,
                        new Color(1, 1, 1, 0.95f));
    }
}

public partial class CarDetailWindow : Window
{
    // --- the one place the window's sizes are written (see the header) -----
    public const int BodyFontPx = 24;      // class, chain, rows, status
    public const int HeadFontPx = 36;      // the header row: 1.5 × body
    public const int BtnFontPx = 17;
    public const int RowCap = 100;         // newest rows shown before `show all`
    public const int MarginPx = 12;
    /// <summary>Width a window asks for when the operator has not sized it.
    /// The header row of a real car (sprite, speed, two dots, x/y/h) measures
    /// ~570 px at these fonts; the probe's first 560 px default overflowed it.</summary>
    public const int DefaultWidthPx = 620;
    public const int RowHeightPx = BodyFontPx + 6;
    /// <summary>Rows the window is high enough for by default (user decision:
    /// "the static part plus exactly three event rows").</summary>
    public const int DefaultVisibleRows = 3;

    public long Uid { get; private set; }
    public bool Showing => Uid != 0;
    /// <summary>The follow TOGGLE's state, owned by the window (the client
    /// decides what one means: the camera's pin rides this car). Turning one
    /// on switches the others off — only one car can be followed, and the
    /// window that asks cannot know about its siblings.</summary>
    public bool Following;
    /// <summary>`show all` was pressed: the cap is off for this window until
    /// it shows a different car.</summary>
    public bool ShowAll;
    /// <summary>The chain id the pointer is over while a button is held — the
    /// highlight the client draws on the map (docs: press-and-hold an id).</summary>
    public long HighlightUid;

    // --- what the client is asked to do (a window never moves the camera or
    //     the cursor itself: it names the ask, the client owns the state) -----
    public System.Action<CarDetailWindow> FollowAsked;
    public System.Action<CarDetailWindow> CenterAsked;
    public System.Action<CarDetailWindow, long> SwitchAsked;     // chain id clicked
    public System.Action<CarDetailWindow, long, bool> HighlightAsked;  // held / released
    public System.Action<CarDetailWindow, long> SeekAsked;       // step clicked
    public System.Action<CarDetailWindow, bool> ShowAllAsked;

    private readonly Label _class;
    private readonly CarSpriteView _sprite;
    private readonly Label _speed;
    private readonly Label _brakeDot;
    private readonly Label _blinkDot;
    private readonly Label _pose;
    private readonly RichTextLabel _chain;
    private readonly VBoxContainer _rows;
    private readonly VBoxContainer _rowBox;
    private readonly ScrollContainer _scroll;
    private readonly VBoxContainer _box;
    private readonly HBoxContainer _statusBox;
    private readonly Label _status;
    private readonly Button _followBtn;
    private readonly Button _centreBtn;
    private string _rowStamp = "";
    private bool _showAllStamp;
    private long _hoverUid;                 // meta the pointer sits on
    private bool _pointerDown;              // a button held OVER that meta
    private string _chainStamp = "";

    /// <summary>For the probes only: the two status-line controls, so a test
    /// can push a real click at them instead of calling a private method
    /// (MapRenderer.ClickInWindow).</summary>
    public Button FollowButton => _followBtn;
    public Button CentreButton => _centreBtn;

    public Texture2D CarTexture
    {
        get => _sprite.Tex;
        set { _sprite.Tex = value; _sprite.QueueRedraw(); }
    }

    public CarDetailWindow()
    {
        Title = "car —";
        Transient = true;               // belongs to the client's window, not the app
        Visible = false;                // nothing is on screen until a car owns it
        MinSize = new Vector2I(DefaultWidthPx, DefaultHeightPx() / 2);
        Size = new Vector2I(DefaultWidthPx, DefaultHeightPx());

        // The panel colour the review surface uses everywhere, as a sibling of
        // the content and added FIRST — NOT in a `CanvasLayer { Layer = 1 }`
        // like the readout's box. A CanvasLayer that sits above the window's
        // own canvas paints that background OVER everything below it: measured
        // with the detail probe, every label had its correct size, the rows
        // answered clicks, and the screenshot showed two black panels with no
        // text in them at all.
        var bg = new ColorRect
        {
            Color = new Color(14 / 255f, 16 / 255f, 22 / 255f, 250f / 255f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var box = new VBoxContainer();
        _box = box;
        box.AddThemeConstantOverride("separation", 6);
        box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        // Offsets, not Size: with full-rect anchors the offsets are what the
        // subwindow's resize moves with, which is the whole point (the
        // operator drags the frame, the list grows, the header does not).
        box.OffsetLeft = MarginPx;
        box.OffsetTop = MarginPx - 4;
        box.OffsetRight = -MarginPx;
        box.OffsetBottom = -MarginPx;
        // Grow flags, not a stamped Size: the OPERATOR resizes this window, and
        // only the event list may change height with it (user decision).
        box.GrowHorizontal = Control.GrowDirection.Both;
        box.GrowVertical = Control.GrowDirection.Both;
        AddChild(box);        // after the background, so it paints on top

        _class = MkLabel(box, BodyFontPx, new Color(0.72f, 0.76f, 0.84f));

        // --- the header row, 1.5 × the body font ---------------------------
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        box.AddChild(head);
        int spriteSide = HeadFontPx * 2;      // wide enough for a car, low enough
        _sprite = new CarSpriteView           // to sit beside the speed
        {
            CustomMinimumSize = new Vector2(spriteSide, spriteSide),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        head.AddChild(_sprite);
        _speed = MkLabel(head, HeadFontPx, new Color(0.96f, 0.96f, 0.98f));
        // TWO DOTS, as the sketch drew them - and two LABELS, not one
        // RichTextLabel: a RichTextLabel inside an HBoxContainer has no
        // natural width, gets squeezed to 1 px, wraps its two glyphs into
        // 124 px of height and reports THAT as its minimum, which the
        // window then grew to (measured: an 827 x 1200 px car detail
        // window). One glyph per label has a width a container can reason
        // about. The words I first put beside the dots cost 275 px and
        // made the header row overflow its own window, which pushed the
        // status buttons to negative x where no press can land.
        _brakeDot = MkLabel(head, HeadFontPx, new Color(0.96f, 0.96f, 0.98f));
        _brakeDot.TooltipText = "brake held";
        _blinkDot = MkLabel(head, HeadFontPx, new Color(0.96f, 0.96f, 0.98f));
        _blinkDot.TooltipText = "blinker lit (either side)";
        head.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _pose = MkLabel(head, BodyFontPx, new Color(0.72f, 0.76f, 0.84f));

        // --- the chain, with a hit area per car id -------------------------
        _chain = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            SelectionEnabled = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _chain.AddThemeFontSizeOverride("normal_font_size", BodyFontPx);
        _chain.MetaClicked += meta => OnChainClicked(meta);
        _chain.MetaHoverStarted += meta => _hoverUid = ParseUid(meta);
        _chain.MetaHoverEnded += _ => { _hoverUid = 0; ReleaseHighlight(); };
        _chain.GuiInput += OnChainGuiInput;
        box.AddChild(_chain);
        box.AddChild(new HSeparator());

        // --- the event list: the ONLY row that grows ------------------------
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill | Control.SizeFlags.ShrinkBegin,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            // Three rows is the floor the default size is built on; below it
            // the LIST gives way, never the header (user decision), and the
            // container needs that stated or it demands its content's height.
            CustomMinimumSize = new Vector2(220, RowHeightPx * DefaultVisibleRows),
            // Horizontal scroll STAYS on (Auto): a decision line is longer
            // than any window the operator keeps open, and a container that
            // cannot scroll demands its content width as its own minimum -
            // which the window then grew to. A long line scrolls instead.
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        box.AddChild(scroll);
        _scroll = scroll;
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 2);
        _rows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_rows);
        _rowBox = _rows;

        // --- the status line: the buttons Godot's title bar cannot host -----
        var status = new HBoxContainer();
        _statusBox = status;
        status.AddThemeConstantOverride("separation", 8);
        box.AddChild(status);
        _followBtn = new Button
        {
            Text = "follow",
            TooltipText = "the camera rides this car - on/off (only one car is followed)",
            FocusMode = Control.FocusModeEnum.None,
        };
        _followBtn.AddThemeFontSizeOverride("font_size", BtnFontPx);
        _followBtn.Pressed += () => FollowAsked?.Invoke(this);
        status.AddChild(_followBtn);
        _centreBtn = new Button
        {
            Text = "centre",
            TooltipText = "put the view on this car (momentary)",
            FocusMode = Control.FocusModeEnum.None,
        };
        _centreBtn.AddThemeFontSizeOverride("font_size", BtnFontPx);
        _centreBtn.Pressed += () => CenterAsked?.Invoke(this);
        status.AddChild(_centreBtn);
        status.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _status = MkLabel(status, BtnFontPx + 3, new Color(0.62f, 0.66f, 0.74f));
    }

    // The static block, in the heights the DETAIL-PROBE measured (not guessed
    // from font sizes: the first estimate missed the container's separations
    // and the chain's wrapped line, asked for 293 px, and the window opened
    // too short for its own header — the header then "grew by 12 px on a
    // resize", which was the overflow settling, not the resize doing anything).
    const int ClassRowPx = BodyFontPx + 10;        // 34
    const int HeadRowPx = HeadFontPx * 2;          // 72 (the sprite is the tall one)
    const int ChainRowPx = BodyFontPx + 10;        // 34
    const int SepPx = 6;                           // the divider
    const int StatusRowPx = BtnFontPx * 2 - 2;     // 32
    const int BoxSeparationPx = 6;

    /// <summary>Height the window asks for when the operator has not sized it:
    /// the static part plus exactly <see cref="DefaultVisibleRows"/> rows
    /// (user decision) — stated so the window never opens smaller than the
    /// header it must always show.</summary>
    public static int DefaultHeightPx() =>
        MarginPx + ClassRowPx + BoxSeparationPx + HeadRowPx + BoxSeparationPx
        + ChainRowPx + SepPx + BoxSeparationPx
        + DefaultVisibleRows * RowHeightPx + BoxSeparationPx
        + StatusRowPx + MarginPx;

    // --- what the probe measures ------------------------------------------
    // Public because a probe that calls a private method proves nothing about
    // what the operator sees; these only READ the layout the container made.

    /// <summary>How far the list starts below the top of the window — i.e. the
    /// height of the static block (class, header, chain, separator). The claim
    /// under test: resizing the frame does NOT change this number.</summary>
    public float HeaderHeightForProbe() => _scroll.GlobalPosition.Y;

    /// <summary>The list's own height: the one that MUST take every pixel the
    /// frame grows by (user decision, docs/CAR_DETAIL_WINDOW.md).</summary>
    public float ListHeightForProbe() => _scroll.Size.Y;

    /// <summary>The whole picture a layout claim is argued from: the window's
    /// client rect, the box's rect and minimum, the list's minimum.</summary>
    public string LayoutForProbe() =>
        $"client={GetVisibleRect().Size} box={_box.Size} boxMin={_box.GetCombinedMinimumSize()} " +
        $"list={_scroll.Size} listMin={_scroll.GetCombinedMinimumSize()} " +
        $"status={_statusBox.Size} · parts class={_class.Size.Y:F0} " +
        $"head={(_speed.GetParent() as Control)?.Size.Y:F0} chain={_chain.Size.Y:F0}";

    /// <summary>The header row as the window actually holds it: text, rect and
    /// visibility of each of its parts — printed by the probe when the picture
    /// says something the sizes do not explain.</summary>
    public string HeaderTextForProbe() =>
        $"speed='{_speed.Text}' rect={_speed.GetGlobalRect()} vis={_speed.Visible} " +
        $"lamps='{_brakeDot.Text}{_blinkDot.Text}' " +
        $"brakeRect={_brakeDot.GetGlobalRect()} blinkRect={_blinkDot.GetGlobalRect()} " +
        $"pose='{_pose.Text}' rect={_pose.GetGlobalRect()} " +
        $"head={(_speed.GetParent() as Control)?.Size}";

    /// <summary>Rows currently in the list (cap applied or not).</summary>
    public int RowCountForProbe() => _rowBox.GetChildCount();

    /// <summary>The newest row's step button — the thing a hand presses to
    /// jump the tape there. Null when there is no row or the client cannot
    /// seek (a live run draws the number as text, not as a link).</summary>
    public Button FirstStepButtonForProbe()
    {
        foreach (var line in _rowBox.GetChildren())
            foreach (var c in line.GetChildren())
                if (c is Button b && b.Text.StartsWith("#")) return b;
        return null;
    }

    static Label MkLabel(Node parent, int px, Color col)
    {
        var l = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.Off,
            // ClipText OFF: measured in the header row it collapsed every
            // label to a 1 px minimum inside the HBox (the text was set, the
            // row said 72 px high, and the window painted a sprite with
            // nothing beside it). The subwindow's own canvas clips what does
            // not fit, which is the effect wanted here anyway.
            ClipText = false,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        l.AddThemeFontSizeOverride("font_size", px);
        l.AddThemeColorOverride("font_color", col);
        parent.AddChild(l);
        return l;
    }

    /// <summary>Bind the window to a car (and reset the per-car state: a new
    /// car is a new cast, and `show all` was an ask about the OLD one).</summary>
    public void ShowCar(long uid)
    {
        if (Uid != uid) ShowAll = false;
        Uid = uid;
        Visible = true;
        _rowStamp = "";                 // force the rows to be rebuilt
        _chainStamp = "";
    }

    public void HideCar()
    {
        Uid = 0;
        ShowAll = false;
        Visible = false;
        ReleaseHighlight();
    }

    /// <summary>Paint one frame of one car. Cheap by design: labels are set,
    /// the chain and the rows re-render only when their stamp changes.</summary>
    public void Update(CarDetailData d)
    {
        if (!Showing || d == null || d.Uid != Uid) return;
        Title = $"#{d.Uid} {(d.Name.Length > 0 ? d.Name : d.VehicleClass)}";
        _class.Text = d.VehicleClass.Length > 0 ? d.VehicleClass : "car";
        _speed.Text = $"{d.SpeedKmh:F0} km/h";
        // The two dots are the LAMPS, not the car's colour: the sprite beside
        // them already carries the colour, so a chip would repeat it.
        var (brakeTxt, brakeCol) = LampGlyph(d.Brake, new Color(1f, 0.33f, 0.28f));
        _brakeDot.Text = brakeTxt;
        _brakeDot.AddThemeColorOverride("font_color", brakeCol);
        var (blinkTxt, blinkCol) = LampGlyph(d.BlinkLeft || d.BlinkRight,
                                            new Color(1f, 0.71f, 0f));
        _blinkDot.Text = blinkTxt;
        _blinkDot.AddThemeColorOverride("font_color", blinkCol);
        _pose.Text = $"x {d.X:F1}  y {d.Y:F1}  h {d.HeadingDeg:F0}°";
        _sprite.AngleDeg = (float)d.HeadingDeg;
        _followBtn.Text = d.Following ? "following" : "follow";
        _followBtn.Modulate = d.Following ? new Color(1f, 0.86f, 0.35f) : Colors.White;
        _status.Text = d.CapKey.Length > 0 ? d.CapKey : "free";

        string cStamp = ChainStamp(d);
        if (cStamp != _chainStamp)
        {
            _chainStamp = cStamp;
            _chain.Text = ChainBbcode(d);
        }
        string stamp = d.RowStamp + (ShowAll ? "|all" : "");
        if (stamp != _rowStamp) { _rowStamp = stamp; BuildRows(d); }

        // NO auto-fitted MinSize taken from the content minimum: measured,
        // that made the window demand 827 x 1200 px for five lines of text,
        // because a container reports what its children WANT and not what
        // the operator asked for. The floor is the constant set at
        // construction; anything longer scrolls inside the list.
    }

    /// <summary>● brake (red when held), ● blinker (amber when a side is
    /// lit); an off dot stays dim, so the pair reads as state, not decoration.
    /// Left before right, matching the `blinkers` bit order.</summary>
    /// <summary>A lamp dot: filled in the lamp's own colour when lit, dim
    /// when not - so the pair reads as state and not as decoration.</summary>
    static (string, Color) LampGlyph(bool on, Color lit) =>
        ("\u25cf", on ? lit : new Color(0.36f, 0.38f, 0.44f));
    string ChainStamp(CarDetailData d) =>
        $"{string.Join(",", d.HoldsUp)}|{string.Join(",", d.HeldUpBy)}|{d.Lockdown}";

    /// <summary>`whom I hold up → me → who holds me up`, one list per side
    /// (docs/CAR_DETAIL_WINDOW.md). Every id is a meta token, so the engine
    /// does the hit testing and the hand can grab one.</summary>
    static string ChainBbcode(CarDetailData d)
    {
        string Left() => d.HoldsUp.Count == 0 ? "[color=#7d8494]—[/color]" : IdList(d.HoldsUp);
        string Right() => d.HeldUpBy.Count == 0 ? "[color=#7d8494]—[/color]" : IdList(d.HeldUpBy);
        string arrow = "[color=#9aa3b2] → [/color]";
        string tail = d.Lockdown ? "  [color=#ff6b6b]LOCKDOWN[/color]" : "";
        return $"{Left()}{arrow}[b]me[/b]{arrow}{Right()}{tail}";
    }

    static string IdList(List<long> uids)
    {
        var parts = new List<string>(uids.Count);
        foreach (var u in uids) parts.Add($"[url=#{u}]#{u}[/url]");
        return string.Join("[color=#7d8494], [/color]", parts);
    }

    void BuildRows(CarDetailData d)
    {
        foreach (var child in _rowBox.GetChildren()) child.QueueFree();
        int shown = ShowAll ? d.Rows.Count : Mathf.Min(d.Rows.Count, RowCap);
        for (int i = 0; i < shown; i++) AddRow(d.Rows[i], d.Seekable, i == 0);
        if (d.Rows.Count > shown)
            AddFooter($"{shown} of {d.RowsTotal} · show all ({d.Rows.Count - shown} more)",
                      () => { ShowAll = true; ShowAllAsked?.Invoke(this, true); BuildRows(d); });
        else if (ShowAll && d.Rows.Count > CarDetailWindow.RowCap)
            AddFooter($"all {d.Rows.Count} rows · back to the newest {RowCap}",
                      () => { ShowAll = false; ShowAllAsked?.Invoke(this, false); BuildRows(d); });
        else if (d.RowsLeft > 0)
            AddFooter($"… {d.RowsLeft} older {d.TruncatedNote}", null);
        else if (d.Rows.Count == 0)
            AddFooter("no decision, no event: this car has never been held", null);
    }

    void AddRow(CarDetailRow r, bool seekable, bool newest)
    {
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 8);
        _rowBox.AddChild(line);
        // Column 1 is the ADDRESS, so it is the thing you press. In the live
        // client there is nothing to jump to, and the number is drawn as text
        // rather than styled as a link and doing nothing (docs: inert).
        if (seekable)
        {
            var b = new Button
            {
                Text = $"#{r.Step}",
                Flat = true,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = $"jump to substep {r.Step} (t={r.SimS:F2} s)",
            };
            long step = r.Step;
            b.Pressed += () => SeekAsked?.Invoke(this, step);
            b.AddThemeFontSizeOverride("font_size", BodyFontPx - 3);
            b.AddThemeColorOverride("font_color", new Color(0.55f, 0.78f, 1f));
            line.AddChild(b);
        }
        else
        {
            var l = MkLabel(line, BodyFontPx - 3, new Color(0.55f, 0.78f, 1f));
            l.Text = $"#{r.Step}";
        }
        MkLabel(line, BodyFontPx - 3, new Color(0.55f, 0.58f, 0.66f)).Text =
            $"{r.SimS:F2} s";
        var msg = MkLabel(line, BodyFontPx, newest ? new Color(0.98f, 0.98f, 1f)
                                                   : new Color(0.80f, 0.82f, 0.86f));
        msg.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        msg.Text = r.Msg;
    }

    void AddFooter(string text, System.Action onClick)
    {
        var line = new HBoxContainer();
        _rowBox.AddChild(line);
        if (onClick == null)
        {
            MkLabel(line, BodyFontPx - 3, new Color(0.55f, 0.58f, 0.66f)).Text = text;
            return;
        }
        var b = new Button { Text = text, Flat = true, FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", BodyFontPx - 3);
        b.AddThemeColorOverride("font_color", new Color(0.55f, 0.78f, 1f));
        b.Pressed += () => onClick();
        line.AddChild(b);
    }

    // --- the chain's hand interactions -------------------------------------
    // Godot 4.7 gives a RichTextLabel meta THREE signals (meta_clicked,
    // meta_hover_started, meta_hover_ended) and no way to ask "which meta is
    // under the pointer" at press time, so the hover signal latches the id and
    // the button state is read against it (docs/CAR_DETAIL_WINDOW.md).
    void OnChainGuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;
        if (mb.Pressed)
        {
            _pointerDown = true;
            if (_hoverUid != 0)
            {
                HighlightUid = _hoverUid;
                HighlightAsked?.Invoke(this, _hoverUid, true);
            }
            return;
        }
        _pointerDown = false;
        ReleaseHighlight();
        // A press that never moved off one id is a click: meta_clicked fires
        // too, and SwitchAsked is asked from there — nothing to do on release
        // but let go of the map highlight.
    }

    void ReleaseHighlight()
    {
        if (HighlightUid == 0) return;
        var was = HighlightUid;
        HighlightUid = 0;
        HighlightAsked?.Invoke(this, was, false);
    }

    void OnChainClicked(Variant meta)
    {
        long uid = ParseUid(meta);
        if (uid != 0) SwitchAsked?.Invoke(this, uid);
    }

    static long ParseUid(Variant meta)
    {
        string s = meta.AsString();
        if (s.StartsWith("#")) s = s.Substring(1);
        return long.TryParse(s, out long v) ? v : 0;
    }
}
