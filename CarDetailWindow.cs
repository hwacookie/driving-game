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
    public double SpeedKmh;
    public bool Brake;
    public bool BlinkLeft, BlinkRight;
    public double X, Y, HeadingDeg;
    public string CapKey = "";                 // the rule owning the cap, "" = free
    public List<long> HoldsUp = new();         // left of the chain: their holder is me
    public List<long> HeldUpBy = new();        // right: me → …, the walk away from me
    public bool Lockdown;                      // R28's own verdict, not "cycle seen"
    /// <summary>The car's events in TIME ORDER, oldest first — the whole
    /// history, so the window can show what SURROUNDS the frame on screen
    /// rather than a list that always starts at the newest (user decision
    /// 2026-10-24: do not show all of them, give the chance to see the events
    /// around the currently selected one).</summary>
    public List<CarDetailRow> Rows = new();
    /// <summary>Index in <see cref="Rows"/> of the event this car is ACTING
    /// under at the frame on screen — the newest one at or before the cursor.
    /// The list is centred on it and that row is painted as current; -1 when
    /// the car has no event yet.</summary>
    public int AnchorIndex = -1;
    public bool Following;
    public bool Seekable;                      // a tape can seek, a live run cannot
    /// <summary>What the row list is keyed on: anything that would change the
    /// rows. Recomputing 100 rows every frame is how the review window once
    /// ran at 2 fps — stamp it and rebuild on change only.</summary>
    public string RowStamp =>
        $"{Uid}|{Rows.Count}|{AnchorIndex}|" +
        $"{(AnchorIndex >= 0 && AnchorIndex < Rows.Count ? Rows[AnchorIndex].Step : 0)}";
}

/// <summary>A small rotated sprite: the car's own PNG, nose along its heading.
/// <c>TextureRect</c> cannot rotate, and re-minting a <c>Sprite2D</c> per frame
/// inside a Control tree is the wrong tool — one <c>DrawSetTransform</c> is.</summary>
/// <summary>The car's own PNG in the window's header, at the car's OWN
/// proportions and rotated to its heading.</summary>
public partial class CarSpriteView : Control
{
    public Texture2D Tex;
    public float AngleDeg;
    /// <summary>The vehicle's physical footprint in metres. Drawing the texture
    /// into the square box (the first build) stretched a 4.4 × 1.8 m car to
    /// 1:1 — the icon read as a bus, and the truck as a squashed van
    /// (reported 2026-10-24: "the icons of the cars are stretched").</summary>
    public double WidthM = 1.8;
    public double LengthM = 4.4;

    public override void _Draw()
    {
        if (Tex == null) return;
        var sz = GetSize();
        double len = LengthM > 0.01 ? LengthM : 4.4;
        double wid = WidthM > 0.01 ? WidthM : 1.8;
        // Fit the FOOTPRINT into the box (the long side along the box height:
        // the sprite's nose is its top edge), so every class keeps its shape
        // and a tractor still looks longer than a sedan.
        float scale = (float)(sz.Y / len);
        float maxW = (float)(sz.X / wid);
        if (maxW < scale) scale = maxW;
        var box = new Vector2((float)wid * scale, (float)len * scale);
        DrawSetTransform(sz / 2.0f, Mathf.DegToRad(AngleDeg), Vector2.One);
        DrawTextureRect(Tex, new Rect2(-box / 2.0f, box), false,
                        new Color(1, 1, 1, 0.95f));
    }
}

public partial class CarDetailWindow : Window
{
    // --- the one place the window's sizes are written (see the header) -----
    public const int BodyFontPx = 24;      // pose, status, footers
    public const int HeadFontPx = 36;      // the speed: 1.5 × body
    /// <summary>The chain and the event list are what an operator reads while
    /// deciding something, so they are NOT the small print of the window: at
    /// the 24 px the first build used, the review of a real run read as
    /// footnotes and the complaint was "too small fonts" (2026-10-24).</summary>
    public const int ChainFontPx = 32;
    public const int EventFontPx = 30;
    public const int StepFontPx = 26;      // the address column, a step smaller
    public const int TimeFontPx = 24;
    public const int BtnFontPx = 17;
    /// <summary>The status glyphs are the button: at 22 px they were the
    /// smallest thing in the window and the complaint was "too small"
    /// (2026-10-24). Text comes off too — the tooltip says what the glyph is,
    /// and the two shapes are different enough to learn.</summary>
    public const int IconPx = 34;
    public const int StatusBtnPx = 52;
    /// <summary>Rows shown on EACH SIDE of the current event. The list is a
    /// window onto the car's story around the frame you are looking at, not a
    /// dump of it (user decision 2026-10-24: "do NOT show all events — give the
    /// user the chance to see the events AROUND the currently selected frame").
    /// Everything further away stays reachable by paging.</summary>
    public const int AroundEvents = 10;
    public const int MarginPx = 12;
    /// <summary>Width a window asks for when the operator has not sized it.
    /// The header row of a real car (sprite, speed, two dots, x/y/h) measures
    /// ~570 px at these fonts; the probe's first 560 px default overflowed it.</summary>
    public const int DefaultWidthPx = 620;
    public const int RowHeightPx = EventFontPx + 10;
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
    /// <summary>How far the visible slice has been paged away from the current
    /// event (0 = centred on it). Reset when the window takes a different car,
    /// and by "back to the current event".</summary>
    public int Page;
    /// <summary>The event the list is centred on, in <see cref="CarDetailData.Rows"/>.</summary>
    public int AnchorIndex { get; private set; } = -1;
    /// <summary>A row was RIGHT-clicked: the client puts it on the clipboard.
    /// The window cannot do that itself — it does not know which tape the row
    /// came from, and an address without it is not reproducible
    /// (docs/CAR_DETAIL_WINDOW.md, "Copying an event out for analysis").</summary>
    public System.Action<CarDetailWindow, string> CopyAsked;
    private ulong _copyFlashUntilMs;
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
    /// <summary>The list was paged (how far from the current event it sits
    /// now) — the client only logs it, the window owns the view.</summary>
    public System.Action<CarDetailWindow, int> Paged;

    private readonly CarSpriteView _sprite;
    private readonly Label _speed;
    private readonly Label _brakeDot;
    private readonly Label _blinkDot;
    private readonly Label _pose;
    private readonly RichTextLabel _chain;
    /// <summary>How a car is ADDRESSED in the chain: the client answers the
    /// role the scenario gave it, or `#uid` when it has none (2026-10-24:
    /// "prefer the cars' names over their number").</summary>
    public System.Func<long, string> ChainName;
    private readonly VBoxContainer _rows;
    private readonly VBoxContainer _rowBox;
    private readonly ScrollContainer _scroll;
    private readonly VBoxContainer _box;
    private readonly HBoxContainer _statusBox;
    private readonly Label _status;
    private readonly Button _followBtn;
    private readonly Button _centreBtn;
    private readonly Button _earlierBtn;
    private readonly Button _laterBtn;
    private readonly Button _nowBtn;
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

    /// <summary>The car's footprint, so the icon keeps the vehicle's shape
    /// instead of the sprite's pixel box.</summary>
    public void SetCarSize(double widthM, double lengthM)
    {
        _sprite.WidthM = widthM;
        _sprite.LengthM = lengthM;
        _sprite.QueueRedraw();
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

        // NO class line (user decision 2026-10-24): the sprite IS the class —
        // a tractor, a mixer, a pickup — and a word under it repeated what the
        // picture already said.
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
        _chain.AddThemeFontSizeOverride("normal_font_size", ChainFontPx);
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
            Icon = LoadIcon("res://assets/ic_follow.svg"),
            TooltipText = "the camera rides this car - on/off (only one car is followed)",
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(StatusBtnPx, StatusBtnPx),
        };
        _followBtn.Pressed += () => FollowAsked?.Invoke(this);
        status.AddChild(_followBtn);
        _centreBtn = new Button
        {
            Icon = LoadIcon("res://assets/ic_centre.svg"),
            TooltipText = "put the view on this car (momentary)",
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(StatusBtnPx, StatusBtnPx),
        };
        _centreBtn.Pressed += () => CenterAsked?.Invoke(this);
        status.AddChild(_centreBtn);
        status.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        // Paging lives in the STATUS LINE, not at the ends of the list. A
        // footer button inside the scrolled list is off-screen whenever the
        // list is centred on the current event — measured: the probe's press
        // for "earlier events" landed at local y = -192, above the window, and
        // paging never happened. Controls that must always be reachable do not
        // live inside the thing they scroll.
        _earlierBtn = MkIconBtn(status, "res://assets/ic_earlier.svg",
                                "earlier events of this car");
        _earlierBtn.Pressed += () => PageBy(-(CarDetailWindow.AroundEvents * 2 + 1));
        _laterBtn = MkIconBtn(status, "res://assets/ic_later.svg",
                              "later events of this car");
        _laterBtn.Pressed += () => PageBy(AroundEvents * 2 + 1);
        _nowBtn = MkIconBtn(status, "res://assets/ic_now.svg",
                            "back to the event this car is acting under at this frame");
        _nowBtn.Pressed += () => PageBy(0, absolute: true);
        status.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _status = MkLabel(status, BtnFontPx + 3, new Color(0.62f, 0.66f, 0.74f));
    }

    // The static block, in the heights the DETAIL-PROBE measured (not guessed
    // from font sizes: the first estimate missed the container's separations
    // and the chain's wrapped line, asked for 293 px, and the window opened
    // too short for its own header — the header then "grew by 12 px on a
    // resize", which was the overflow settling, not the resize doing anything).
    const int HeadRowPx = HeadFontPx * 2;          // 72 (the sprite is the tall one)
    const int ChainRowPx = ChainFontPx + 10;       // 42
    const int SepPx = 6;                           // the divider
    const int StatusRowPx = StatusBtnPx;           // 52 (the icon buttons)
    const int BoxSeparationPx = 6;

    /// <summary>Height the window asks for when the operator has not sized it:
    /// the static part plus exactly <see cref="DefaultVisibleRows"/> rows
    /// (user decision) — stated so the window never opens smaller than the
    /// header it must always show.</summary>
    public static int DefaultHeightPx() =>
        MarginPx + HeadRowPx + BoxSeparationPx
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
        $"status={_statusBox.Size} · parts " +
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

    /// <summary>Rows currently in the list (the page plus its footers).</summary>
    public int RowCountForProbe() => _rowBox.GetChildCount();

    /// <summary>The row the list is centred on, for the probe to right-click.</summary>
    public Control CurrentRowForProbe()
    {
        foreach (var child in _rowBox.GetChildren())
            if (child is PanelContainer band && band.GetChildCount() > 0)
                return band;          // the anchored row is the one wrapped in a band
        return null;
    }

    /// <summary>The paging controls, for the probe to press as a hand does.</summary>
    public Button PagingButtonForProbe(string which) => which switch
    {
        "earlier" => _earlierBtn,
        "later" => _laterBtn,
        "current" => _nowBtn,
        _ => null,
    };

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

    /// <summary>The status-line glyphs, sized here rather than baked: two
    /// 24 px SVGs in `assets/`, client-owned because Godot's own icon set is an
    /// editor resource (`GD.GetEditorIcons()`) and a run from the command line
    /// is not the editor. They take the button's modulate, so a followed
    /// window lights icon and word together.</summary>
    static Texture2D LoadIcon(string path)
    {
        var t = GD.Load<Texture2D>(path);
        if (t == null)
        {
            // An icon that failed to import must not take the button with it:
            // the word is still there, and the console says why.
            GD.PrintErr($"detail window: no icon at {path} (import it with " +
                        "`Godot --headless --path . --import`)");
            return null;
        }
        // AtlasTexture is the non-destructive way to resize a texture: the SVG
        // is rasterised at the size the button needs, not stretched at draw.
        var sz = t.GetSize();
        float k = CarDetailWindow.IconPx / Mathf.Max(1f, Mathf.Max(sz.X, sz.Y));
        return new AtlasTexture
        {
            Atlas = t,
            Region = new Rect2(Vector2.Zero, sz * k),
            FilterClip = false,
        };
    }

    static Button MkIconBtn(Node parent, string icon, string tooltip)
    {
        var b = new Button
        {
            Icon = LoadIcon(icon),
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(StatusBtnPx, StatusBtnPx),
        };
        parent.AddChild(b);
        return b;
    }

    /// <summary>Move the visible slice through the car's history. 0 (and
    /// absolute) recentres it on the current event.</summary>
    void PageBy(int delta, bool absolute = false)
    {
        Page = absolute ? 0 : Page + delta;
        Paged?.Invoke(this, Page);
        if (_last != null) BuildRows(_last);
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
        if (Uid != uid) Page = 0;      // a new car is a new story
        Uid = uid;
        Visible = true;
        _rowStamp = "";                 // force the rows to be rebuilt
        _chainStamp = "";
    }

    public void HideCar()
    {
        Uid = 0;
        Page = 0;
        Visible = false;
        ReleaseHighlight();
    }

    /// <summary>Paint one frame of one car. Cheap by design: labels are set,
    /// the chain and the rows re-render only when their stamp changes.</summary>
    public void Update(CarDetailData d)
    {
        if (!Showing || d == null || d.Uid != Uid) return;
        // Identity in the title bar, name first: "#27 slow_car", and the
        // number alone when the cast was never named.
        Title = d.Name.Length > 0 ? $"#{d.Uid} {d.Name}" : $"#{d.Uid}";
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
        // A button with no word wears its state entirely in the glyph's
        // colour: amber = this window is the one the camera rides.
        // No word on these two, so the tint carries the state: amber = this
        // window is the one the camera rides. (Modulate, not the theme's
        // icon_modulate — it is a theme PROPERTY in Godot 4 and C# reaches it
        // through AddThemeColorOverride; tinting the button is the same look.)
        _followBtn.Modulate = d.Following ? new Color(1f, 0.86f, 0.35f)
                                          : new Color(0.80f, 0.82f, 0.88f);
        // The paint and the state are one decision: the followed window's
        // button (icon included) wears the highlight colour.
        _centreBtn.Modulate = new Color(0.80f, 0.82f, 0.88f);
        // The same corner carries the copy confirmation for a moment: the
        // hand that right-clicked needs to know it worked, and the tape is
        // already on the clipboard, not on the screen.
        _status.Text = Time.GetTicksMsec() < _copyFlashUntilMs
            ? "copied → clipboard"
            : (d.CapKey.Length > 0 ? d.CapKey : "free");

        string cStamp = ChainStamp(d);
        if (cStamp != _chainStamp)
        {
            _chainStamp = cStamp;
            _chain.Text = ChainBbcode(d);
        }
        string stamp = d.RowStamp + "|page" + Page;
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
    /// (docs/CAR_DETAIL_WINDOW.md). Every entry is a meta token carrying the
    /// uid, so the engine does the hit testing and the hand can grab one —
    /// while what is READ is the car's name ("the slow car", not "#27").</summary>
    string ChainBbcode(CarDetailData d)
    {
        string Left() => d.HoldsUp.Count == 0 ? "[color=#7d8494]—[/color]" : IdList(d.HoldsUp);
        string Right() => d.HeldUpBy.Count == 0 ? "[color=#7d8494]—[/color]" : IdList(d.HeldUpBy);
        string arrow = "[color=#9aa3b2] → [/color]";
        // `me` is the car whose window this is — the only static word on the
        // line, and the reference the two sides are read from.
        string tail = d.Lockdown ? "  [color=#ff6b6b]LOCKDOWN[/color]" : "";
        return $"{Left()}{arrow}[b]me[/b]{arrow}{Right()}{tail}";
    }

    string IdList(List<long> uids)
    {
        var parts = new List<string>(uids.Count);
        foreach (var u in uids)
        {
            string label = ChainName?.Invoke(u);
            if (string.IsNullOrEmpty(label)) label = $"#{u}";
            parts.Add($"[url=#{u}][color=#8cc8ff]{label}[/color][/url]");
        }
        return string.Join("[color=#7d8494], [/color]", parts);
    }

    /// <summary>The list around the CURRENT event: a page before it, a page
    /// after it, and the row itself painted as the one this car is acting
    /// under. Paging walks the whole history without ever dumping it, and a
    /// jump back to the anchor is one button — because once the operator has
    /// paged away, "where was I?" is the question they ask next.</summary>
    CarDetailData _last;

    void BuildRows(CarDetailData d)
    {
        _last = d;
        foreach (var child in _rowBox.GetChildren()) child.QueueFree();
        AnchorIndex = d.AnchorIndex;
        _nowBtn.Modulate = Page == 0 ? new Color(0.36f, 0.38f, 0.44f)
                                    : new Color(1f, 0.86f, 0.35f);
        int n = d.Rows.Count;
        if (n == 0)
        {
            AddFooter("no decision, no event: this car has never been held");
            return;
        }
        int anchor = Mathf.Clamp(d.AnchorIndex < 0 ? n - 1 : d.AnchorIndex, 0, n - 1);
        int centre = Mathf.Clamp(anchor + Page, 0, n - 1);
        int lo = Mathf.Max(0, centre - AroundEvents);
        int hi = Mathf.Min(n - 1, centre + AroundEvents);
        if (lo > 0) AddFooter($"↑ {lo} earlier events (the up arrow below)");
        for (int i = lo; i <= hi; i++) AddRow(d.Rows[i], d.Seekable, i == anchor, i, lo);
        if (hi < n - 1) AddFooter($"↓ {n - 1 - hi} later events (the down arrow below)");
        if (Page != 0) AddFooter($"· not centred any more ·");
        // The current row, not the top of the list: the window has to open on
        // what the car is doing NOW at this frame.
        _scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical,
                            Mathf.Max(0, anchor - lo - 1) * RowHeightPx);
    }



    void AddRow(CarDetailRow r, bool seekable, bool current, int index, int lo)
    {
        // The line as it is DISPLAYED — the clipboard copy and the picture must
        // not be two formats that can drift.
        string line = RowLine(r);

        // The anchor row is a PANEL, so it can carry a band of colour behind
        // the text: "this is the reason the car behaves as it does at this
        // frame" has to be seen, not inferred from position.
        Control parent = _rowBox;
        if (current)
        {
            var band = new PanelContainer();
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(0.30f, 0.44f, 0.72f, 0.42f),
                ContentMarginLeft = 4, ContentMarginRight = 4,
                ContentMarginTop = 2, ContentMarginBottom = 2,
            };
            band.AddThemeStyleboxOverride("panel", sb);
            _rowBox.AddChild(band);
            parent = band;
        }
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        // A right-click anywhere on the line is the gesture, so the row takes
        // the event itself: PASS would let it through to whatever is behind.
        row.MouseFilter = Control.MouseFilterEnum.Pass;
        string copied = line;
        row.GuiInput += e => OnRowGuiInput(e, copied);
        parent.AddChild(row);
        if (current)
            MkLabel(row, EventFontPx, new Color(1f, 0.86f, 0.35f)).Text = "▸";
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
            b.AddThemeFontSizeOverride("font_size", StepFontPx);
            b.AddThemeColorOverride("font_color", new Color(0.55f, 0.78f, 1f));
            row.AddChild(b);
        }
        else
        {
            var l = MkLabel(row, StepFontPx, new Color(0.55f, 0.78f, 1f));
            l.Text = $"#{r.Step}";
        }
        MkLabel(row, TimeFontPx, new Color(0.55f, 0.58f, 0.66f)).Text =
            $"{r.SimS:F2} s";
        // Past the anchor, later events are the FUTURE of the frame on screen:
        // shown (that is what one scrubs towards), but dimmed so "ahead of
        // here" reads without a label saying it.
        // Past the anchor the lines are the FUTURE of this frame — dim grey,
        // deliberately not the blue of the address column, or "later" would
        // look like a link the way the step numbers do (seen in the shot).
        Color msgCol = current ? new Color(1f, 1f, 1f)
                 : index < AnchorIndex ? new Color(0.78f, 0.80f, 0.86f)
                                       : new Color(0.55f, 0.57f, 0.63f);
        var msg = MkLabel(row, EventFontPx, msgCol);
        msg.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        msg.Text = r.Msg;
    }

    /// <summary>One row of the list as text, exactly as the window paints it:
    /// address, seconds, then the event itself.</summary>
    static string RowLine(CarDetailRow r) => $"#{r.Step}  {r.SimS:F2} s  {r.Msg}";

    /// <summary>Right-click on a row = "I am about to ask what this event
    /// MEANT", so hand the whole address to the client for the clipboard.</summary>
    void OnRowGuiInput(InputEvent e, string line)
    {
        if (e is not InputEventMouseButton mb || !mb.Pressed ||
            mb.ButtonIndex != MouseButton.Right) return;
        CopyAsked?.Invoke(this, line);
        _copyFlashUntilMs = Time.GetTicksMsec() + 1500;
    }

    /// <summary>A line of the list that is not an event: how much of the
    /// history is off-screen, or that there is none. Paging is a control in the
    /// status line, so these stay plain text (a footer button inside a scrolled
    /// list is unreachable exactly when the list is centred where it should be).</summary>
    void AddFooter(string text)
    {
        var line = new HBoxContainer();
        _rowBox.AddChild(line);
        MkLabel(line, TimeFontPx, new Color(0.55f, 0.58f, 0.66f)).Text = text;
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
