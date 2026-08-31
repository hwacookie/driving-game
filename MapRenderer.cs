using Godot;
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
    private readonly List<(CanvasItem ci, float refM)> _faded = new();

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
        _faded.Clear();
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
                        _faded.Add((p, kv.Item2));

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
        GD.Print($"Map ready: bounds {_bounds}, zoom {zoom:F3}");

        if (_screenshotPath != null && !_testPan)
            GetTree().CreateTimer(0.5).Timeout += () => SaveShot(_screenshotPath);
    }

    // ------------------------------------------- zoom-dependent (px floor)

    /// <summary>pygame floors marking line widths and junction-dot radii to
    /// whole screen pixels (max(1, ...) px). The world-space meshes cannot
    /// express that directly, so whenever the pixel size changes we rebuild
    /// the marking + dot layers at width = max(physical, 1/zoom) metres.</summary>
    private void RebuildDynamic()
    {
        float s = _cam.Zoom.X;                       // px per metre
        int linePx = Mathf.Max(1, (int)(0.15f * s)); // pygame: max(1, int(0.15*pppm*zoom))
        int dotPx = Mathf.Max(1, (int)Mathf.Round(_dotRadiusM * s));
        if (linePx == _lastLinePx && dotPx == _lastDotPx) return;
        _lastLinePx = linePx;
        _lastDotPx = dotPx;

        foreach (var child in _markLayer.GetChildren())
            child.QueueFree();
        foreach (var child in _dotLayer.GetChildren())
            child.QueueFree();

        float wDashM = linePx / s;
        // Keyed by (width, fade ref) so each pattern group gets its own
        // material + pygame-fade reference.
        var verts = new Dictionary<(float w, float refM), List<Vector3>>();
        void Quad(Color c, float w, float refM, Vector2 a, Vector2 b)
        {
            if (!verts.TryGetValue((w, refM), out var list))
            {
                list = new List<Vector3>();
                verts[(w, refM)] = list;
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
                    Quad(line.Color, w, line.FadeRefM, line.Pts[i], line.Pts[i + 1]);
            }
            else if (line.DashM > 0f)
                AddDashes(Quad, line.Color, wDashM, line.FadeRefM,
                          line.Pts, line.DashM, line.GapM);
        }

        foreach (var kv in verts)
        {
            var mesh = new ImmediateMesh();
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            foreach (var v in kv.Value)
                mesh.SurfaceAddVertex(v);
            mesh.SurfaceEnd();
            var mi = new MeshInstance2D { Mesh = mesh, ZIndex = 2 };
            _markLayer.AddChild(mi);
            _faded.Add((mi, kv.Key.refM));
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
        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp) ZoomBy(1.1f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) ZoomBy(1f / 1.1f);
        }
    }

    void ZoomBy(float f)
    {
        var z = _cam.Zoom * f;
        _cam.Zoom = z.Clamp(new Vector2(0.05f, 0.05f), new Vector2(10f, 10f));
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
        foreach (var kv in _faded)
            kv.ci.Modulate = new Color(1, 1, 1, DashAlpha(kv.refM, s));
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
