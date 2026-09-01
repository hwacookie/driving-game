using Godot;
using System;
using System.Collections.Generic;
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
    private Camera2D _cam;
    private Node2D _world;                 // container for all map nodes
    private Node2D _markLayer;             // rebuilt on zoom change (px floor)
    private Node2D _dotLayer;              // rebuilt on zoom change (px floor)
    private Rect2 _bounds;                  // world bounds in metres
    private readonly HashSet<Key> _keys = new();
    private string _screenshotPath;
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

    public override void _Ready()
    {
        _http = GetNode<HttpRequest>("Http");
        _cam = GetNode<Camera2D>("Cam");
        _world = new Node2D { Name = "World" };
        AddChild(_world);
        _markLayer = new Node2D { Name = "Markings" };
        _dotLayer = new Node2D { Name = "JunctionDots" };
        _world.AddChild(_markLayer);
        _world.AddChild(_dotLayer);
        _http.RequestCompleted += OnMapResponse;
        FetchMap();

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
            else if (args[i] == "--center" && i + 3 < args.Length)
            {
                _overrideCenter = W(float.Parse(args[i + 1]), float.Parse(args[i + 2]));
                i += 2;
            }
            else if (args[i] == "--zoom" && i + 1 < args.Length)
                _overrideZoom = float.Parse(args[i + 1]);
            else if (args[i] == "--test-pan")
                _testPan = true;
        }

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
        }
        _minimap.Setup(_mmSegs, _bounds, roadColor, _cam);
        _lastCamPos = _cam.GlobalPosition;
        _lastCamZoom = zoom;
        UpdateZoomLabel();
        _minimap.QueueRedraw();

        GD.Print($"Map ready: bounds {_bounds}, zoom {zoom:F3}");

        if (_screenshotPath != null && !_testPan)
            GetTree().CreateTimer(0.5).Timeout += () => SaveShot(_screenshotPath);
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
            if (k.Pressed && k.Keycode == Key.Escape)
                GetTree().Quit();
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
            _cam.GlobalPosition += dir.Normalized() * 600f / _cam.Zoom.X * (float)delta;

        // Rebuild markings/dots only when the pixel floor changes.
        RebuildDynamic();

        // Apply pygame's low-zoom fade (cheap: a handful of modulates).
        float s = _cam.Zoom.X;
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

    void SaveShot(string path)
    {
        var img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(path);
        GD.Print(err == Error.Ok ? $"Screenshot saved: {path}" : $"SavePng failed: {err}");
        // Screenshot runs are one-shot: quit cleanly (no external kill).
        GetTree().Quit();
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
    const int Size = 360;   // big enough for the whole track to stay readable
    const int Margin = 15;
    static readonly Color Bg = new(20 / 255f, 60 / 255f, 20 / 255f);       // MINIMAP_BG
    static readonly Color Border = new(80 / 255f, 80 / 255f, 80 / 255f);  // MINIMAP_BORDER
    static readonly Color CarColor = new(1f, 0f, 0f);                     // MINIMAP_CAR_COLOR


    List<(Vector2 A, Vector2 B, float W)> _segs;
    Rect2 _bounds;      // sim coords: position = (xmin, ymin), size = extent
    Color _road;
    Camera2D _cam;

    public Vector2 CarPosSim;  // set by M2 (car tracking)
    public bool HasCar;

    public void Setup(List<(Vector2 A, Vector2 B, float W)> segs,
                      Rect2 bounds, Color road, Camera2D cam)
    {
        _segs = segs; _bounds = bounds; _road = road; _cam = cam;
    }

    public override void _Draw()
    {
        if (_segs == null || _cam == null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        float mmx = vp.X - Size - Margin, mmy = Margin;
        var box = new Rect2(mmx, mmy, Size, Size);
        DrawRect(box, Bg);

        float sx = Size / _bounds.Size.X;
        float sy = Size / _bounds.Size.Y;
        Vector2 Mm(Vector2 p) => new(
            mmx + (p.X - _bounds.Position.X) * sx,
            mmy + Size - (p.Y - _bounds.Position.Y) * sy);

        // Every road must stay visible: draw each segment with a 1 px floor
        // (wider roads keep their true width when it exceeds 1 px).
        foreach (var (a, b, w) in _segs)
        {
            float lw = Mathf.Max(1f, w * Math.Min(sx, sy));
            DrawLine(Mm(a), Mm(b), _road, lw);
        }

        if (HasCar)
            DrawCircle(Mm(CarPosSim), 3f, CarColor);

        // Yellow viewport rectangle (2 px outline). No clip API in C#, so
        // clamp it to the box — at low zoom it alone can exceed the minimap.
        float z = _cam.Zoom.X;
        var c = _cam.GlobalPosition;                 // Godot world (y down)
        Vector2 camSim = new(c.X, -c.Y);             // sim coords
        float wpx = vp.X / z * sx, hpx = vp.Y / z * sy;
        float left = mmx + (camSim.X - vp.X / 2f / z - _bounds.Position.X) * sx;
        float top = mmy + Size - (camSim.Y + vp.Y / 2f / z - _bounds.Position.Y) * sy;
        var vpr = new Rect2(left, top, wpx, hpx).Intersection(box);
        if (vpr.Size != Vector2.Zero)
            DrawRect(vpr, new Color(1f, 1f, 0f), false, 2f);

        DrawRect(box, Border, false, 2f);
    }

}
