using System.Text.Json;
using System.Text.Json.Nodes;
using DrivingGame.Sim;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// Phase 2 gate (docs/C_SHARP_PORT_SPEC.md): the C# raceline port must
/// reproduce the Python reference dump (car repo tools/raceline_reference.py)
/// at ~1e-9 tolerance. The strict tests inject the PYTHON paved + eroded-safe
/// polygons so both implementations solve on IDENTICAL geometry; a separate
/// test runs the production path (NTS-computed erosion) with a looser bound.
///
/// The dump is a self-describing artifact: it declares its own scale
/// ("pxPerM", legacy "pppm") and keeps its geometry in that frame. Reading
/// it is therefore a frame crossing into the sim's metre frame - see
/// FixturePxPerM below. Lengths that are NOT frame values (offsets, lo/hi,
/// base_prof, cum, props widths) and frame-independent scalars (K = rad/m,
/// unit normals) are read as they are.
/// </summary>
public class RacelineReferenceTests
{
    const double Tol = 1e-9;

    // ------------------------------------------------------------------
    // reference loading
    // ------------------------------------------------------------------

    public sealed record CaseData(
        string Name,
        string[] Nodes,
        int[] SegIdx,
        double? BaseOffset,
        bool AutoBase,
        double? MergeFromM,
        double MergeS0,
        double MergeS1,
        List<(double X, double Y)> Rounded,
        List<(double X, double Y)> P,
        List<(double X, double Y)> N,
        double[] Offsets,
        double[] Cum,
        double[] K,
        double[] Lo,
        double[] Hi,
        double[] BaseProf,
        (bool Oneway, double Width, int Lanes, double Parking)[] Props,
        (double X, double Y)?[] Junction);

    public sealed class Reference
    {
        public required string Map { get; init; }
        public required int NodeCount { get; init; }
        public required int SegmentCount { get; init; }
        public required Dictionary<string, (double X, double Y)> Nodes { get; init; }
        public required Dictionary<string, int> NodeDegree { get; init; }
        public required Geometry Paved { get; init; }
        public required Geometry Safe { get; init; }
        public required List<CaseData> Cases { get; init; }
    }

    static Reference? _reference;

    static string FindReferenceFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "data", "raceline_reference", "basic.json");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "data/raceline_reference/basic.json not found — run the car repo's " +
            "tools/raceline_reference.py first.");
    }

    static Reference LoadReference()
    {
        if (_reference is not null) return _reference;
        using var doc = JsonDocument.Parse(File.ReadAllText(FindReferenceFile()));
        var root = doc.RootElement;

        double pxPerM = FixturePxPerM(root);
        var nodes = new Dictionary<string, (double X, double Y)>();
        foreach (var kv in root.GetProperty("nodes").EnumerateObject())
            nodes[kv.Name] = (kv.Value[0].GetDouble() / pxPerM, kv.Value[1].GetDouble() / pxPerM);

        var degree = new Dictionary<string, int>();
        foreach (var kv in root.GetProperty("node_degree").EnumerateObject())
            degree[kv.Name] = kv.Value.GetInt32();

        var cases = new List<CaseData>();
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            string Name(string p) => c.GetProperty(p).GetString()!;

            var rounded = FramePts(c.GetProperty("rounded"), pxPerM);
            var p = FramePts(c.GetProperty("P"), pxPerM);
            var n = Pts(c.GetProperty("N"));   // unit normals: no scale
            double[] Arr(string q) => c.GetProperty(q).EnumerateArray().Select(e => e.GetDouble()).ToArray();

            var props = c.GetProperty("props").EnumerateArray()
                .Select(e => (e[0].GetInt32() != 0, e[1].GetDouble(), e[2].GetInt32(), e[3].GetDouble()))
                .ToArray();
            var junction = c.GetProperty("junction").EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Null
                    ? ((double X, double Y)?)null
                    : (e[0].GetDouble() / pxPerM, e[1].GetDouble() / pxPerM))
                .ToArray();

            cases.Add(new CaseData(
                Name("name"),
                c.GetProperty("nodes").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                c.GetProperty("seg_idx").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                KwDouble(c, "base_offset"),
                KwBool(c, "auto_base"),
                KwDouble(c, "merge_from_m"),
                KwDouble(c, "merge_s0") ?? 0.0,
                KwDouble(c, "merge_s1") ?? 0.0,
                rounded, p, n, Arr("offsets"), Arr("cum"),
                Arr("K"), Arr("lo"), Arr("hi"), Arr("base_prof"),
                props, junction));
        }

        var reference = new Reference
        {
            Map = root.GetProperty("map").GetString()!,
            NodeCount = root.GetProperty("node_count").GetInt32(),
            SegmentCount = root.GetProperty("segment_count").GetInt32(),
            Nodes = nodes,
            NodeDegree = degree,
            Paved = BuildGeometry(root.GetProperty("paved"), pxPerM),
            Safe = BuildGeometry(root.GetProperty("safe"), pxPerM),
            Cases = cases,
        };
        _reference = reference;
        return reference;
    }

    static double? KwDouble(JsonElement c, string key)
    {
        if (c.TryGetProperty("kwargs", out var kw) &&
            kw.ValueKind == JsonValueKind.Object &&
            kw.TryGetProperty(key, out var v) &&
            v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    static bool KwBool(JsonElement c, string key)
    {
        return c.TryGetProperty("kwargs", out var kw) &&
               kw.ValueKind == JsonValueKind.Object &&
               kw.TryGetProperty(key, out var v) &&
               v.ValueKind == JsonValueKind.True;
    }

    /// <summary>Fixture points that are NOT lengths (unit normals).</summary>
    static List<(double X, double Y)> Pts(JsonElement arr) =>
        arr.EnumerateArray().Select(e => (e[0].GetDouble(), e[1].GetDouble())).ToList();

    /// <summary>Fixture points that ARE frame coordinates -> sim frame (m).</summary>
    static List<(double X, double Y)> FramePts(JsonElement arr, double pxPerM) =>
        arr.EnumerateArray().Select(e => (e[0].GetDouble() / pxPerM,
                                          e[1].GetDouble() / pxPerM)).ToList();

    /// <summary>The scale the fixture declares for its own geometry frame
    /// (px per metre); 1 means it is already in the sim's metre frame.</summary>
    static double FixturePxPerM(JsonElement root)
    {
        if (root.TryGetProperty("pxPerM", out var now)) return now.GetDouble();
        if (root.TryGetProperty("pppm", out var legacy)) return legacy.GetDouble();
        return 1.0;
    }

    /// <summary>Build an NTS geometry from a shapely __geo_interface__ object.</summary>
    static Geometry BuildGeometry(JsonElement gi, double pxPerM)
    {
        string type = gi.GetProperty("type").GetString()!;
        var coords = gi.GetProperty("coordinates");
        List<Polygon> polys = type switch
        {
            "Polygon" => new() { ToPolygon(coords[0], pxPerM) },
            "MultiPolygon" => coords.EnumerateArray().Select(p => ToPolygon(p, pxPerM)).ToList(),
            _ => throw new NotSupportedException($"geometry type {type}"),
        };
        return polys.Count > 1 ? new MultiPolygon(polys.ToArray()) : polys[0];
    }

    static Polygon ToPolygon(JsonElement ringArr, double pxPerM)
    {
        Coordinate[] Ring(JsonElement r) =>
            r.EnumerateArray().Select(c => new Coordinate(c[0].GetDouble() / pxPerM,
                                                          c[1].GetDouble() / pxPerM)).ToArray();
        var shell = NtsCompat.RingOf(Ring(ringArr[0]));
        var holes = ringArr.GetArrayLength() > 1
            ? ringArr.EnumerateArray().Skip(1).Select(h => (LinearRing)NtsCompat.RingOf(Ring(h))).ToArray()
            : Array.Empty<LinearRing>();
        return new Polygon(shell, holes);
    }

    // ------------------------------------------------------------------
    // (removed 2026-09-11, user decision) NetworkMatchesReference and
    // ErosionAreaMatchesReference pinned the WHOLE test map + eroded area to
    // the frozen Python reference dump. Post-port the C# map deliberately
    // grew (crossing tiles 2,3 / 3,3 -> 277 nodes vs the dump's 183), so
    // both compared apples to oranges and were stale. The per-CASE tests
    // below still pin the raceline math; only these two whole-map snapshots
    // were deleted.
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // (removed 2026-09-11, user decision) RoundedCenterlinesMatchReference
    // rebuilt the rounded centrelines from the CURRENT map and pinned them to
    // the frozen dump, so it was the one reference test coupled to the map
    // LAYOUT - every deliberate map edit (tapers, bent width transitions,
    // moved tracks) broke it and forced a rebaseline. The corner rounding is
    // still covered indirectly by the (map-stable) solver reference tests
    // below + the e2e suite. Same rationale as the deleted Network/Erosion
    // reference tests.
    // ------------------------------------------------------------------
    // THE GATE: solve outputs vs the Python reference, on IDENTICAL
    // geometry (Python's paved + eroded-safe polygons injected).
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllCases))]
    public void SolveMatchesReference(CaseData c)
    {
        var network = TestMaps.BuildBasicTestMap();
        InjectPythonGeometry(network);

        var result = Raceline.SolveLine(
            network, c.Rounded, c.SegIdx.ToList(),
            baseOffset: c.BaseOffset, autoBase: c.AutoBase,
            mergeFromM: c.MergeFromM, mergeS0: c.MergeS0, mergeS1: c.MergeS1);

        Assert.True(result.Points.Count == c.P.Count, $"{c.Name}: point count");
        Assert.True(result.Offsets.Length == c.Offsets.Length, $"{c.Name}: offset count");

        for (int i = 0; i < c.P.Count; i++)
        {
            Assert.True(Math.Abs(result.Points[i].X - c.P[i].X) <= Tol &&
                        Math.Abs(result.Points[i].Y - c.P[i].Y) <= Tol,
                $"{c.Name}: P[{i}] C# ({result.Points[i].X}, {result.Points[i].Y}) " +
                $"vs Python ({c.P[i].X}, {c.P[i].Y})");
            Assert.True(Math.Abs(result.Normals[i].X - c.N[i].X) <= Tol &&
                        Math.Abs(result.Normals[i].Y - c.N[i].Y) <= Tol,
                $"{c.Name}: N[{i}] mismatch");
        }
        for (int i = 0; i < c.Offsets.Length; i++)
        {
            Assert.True(Math.Abs(result.Offsets[i] - c.Offsets[i]) <= Tol,
                $"{c.Name}: offsets[{i}] C# {result.Offsets[i]:R} vs Python {c.Offsets[i]:R}");
            Assert.True(Math.Abs(result.Cum[i] - c.Cum[i]) <= Tol,
                $"{c.Name}: cum[{i}] C# {result.Cum[i]:R} vs Python {c.Cum[i]:R}");
        }
    }

    public static IEnumerable<object[]> AllCases() =>
        LoadReference().Cases.Select(c => new object[] { c });

    /// <summary>Run the solver on IDENTICAL geometry but through the
    /// PRODUCTION path: C# computes its own eroded safe polygon with NTS
    /// (matched buffer parameters) instead of the injected Python one.
    /// GEOS and NTS discretise arcs slightly differently, so this allows a
    /// few cm where the line is pinned to a corridor bound — anything more
    /// means the erosion itself diverged.</summary>
    [Theory]
    [MemberData(nameof(AllCases))]
    public void SolveMatchesReference_ProductionErosion(CaseData c)
    {
        const double TolM = 0.05;   // 5 cm — see summary comment
        var network = TestMaps.BuildBasicTestMap();
        // No injection: RacelineSafe starts null -> built by NTS buffer.

        var result = Raceline.SolveLine(
            network, c.Rounded, c.SegIdx.ToList(),
            baseOffset: c.BaseOffset, autoBase: c.AutoBase,
            mergeFromM: c.MergeFromM, mergeS0: c.MergeS0, mergeS1: c.MergeS1);

        double maxDiff = 0.0; int worstI = -1;
        for (int i = 0; i < c.Offsets.Length; i++)
        {
            double d = Math.Abs(result.Offsets[i] - c.Offsets[i]);
            if (d > maxDiff) { maxDiff = d; worstI = i; }
        }
        string worst = worstI < 0 ? "n/a" : $"station {worstI} (C# {result.Offsets[worstI]:R} vs Python {c.Offsets[worstI]:R})";
        Assert.True(maxDiff <= TolM,
            $"{c.Name}: production-erosion offset diff {maxDiff:F4} m at {worst}");
    }

    // (ErosionAreaMatchesReference removed 2026-09-11, user decision - see
    //  the note above NetworkMatchesReference's former location.)

    // ------------------------------------------------------------------
    // intermediate values: resample/normals/curvature, corridor bounds and
    // the auto-base profile — same inputs as the gate, compared directly.
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllCases))]
    public void IntermediatesMatchReference(CaseData c)
    {
        var network = TestMaps.BuildBasicTestMap();
        InjectPythonGeometry(network);

        var (P, S, total) = Raceline.Resample(c.Rounded, Raceline.SampleM);
        Assert.True(Math.Abs(total - c.Cum[^1]) <= 1e-6, $"{c.Name}: total length");

        var props = Raceline.StationSegments(network, c.SegIdx.ToList(), P);
        for (int i = 0; i < props.Length; i++)
        {
            Assert.True(props[i].Oneway == c.Props[i].Oneway &&
                        Math.Abs(props[i].Width - c.Props[i].Width) <= Tol &&
                        props[i].Lanes == c.Props[i].Lanes &&
                        Math.Abs(props[i].Parking - c.Props[i].Parking) <= Tol,
                $"{c.Name}: props[{i}] C# {props[i]} vs Python {c.Props[i]}");
        }

        var (N, K) = Raceline.NormalsAndCurvature(P, Raceline.SampleM);
        for (int i = 0; i < K.Length; i++)
            Assert.True(Math.Abs(K[i] - c.K[i]) <= Tol,
                $"{c.Name}: K[{i}] C# {K[i]:R} vs Python {c.K[i]:R}");

        var junction = Raceline.JunctionNodePerStation(network, P);
        for (int i = 0; i < junction.Count; i++)
        {
            var cj = c.Junction[i];
            var jj = junction[i];
            if (jj is null)
            {
                Assert.True(cj is null, $"{c.Name}: junction[{i}]");
            }
            else
            {
                Assert.True(cj is not null, $"{c.Name}: junction[{i}] missing");
                var j = cj.Value;
                Assert.True(Math.Abs(jj.Value.X - j.X) <= Tol &&
                            Math.Abs(jj.Value.Y - j.Y) <= Tol,
                    $"{c.Name}: junction[{i}] position");
            }
        }

        var (lo, hi) = Raceline.LegalCorridor(network, P, N, props, junction);
        for (int i = 0; i < lo.Length; i++)
        {
            Assert.True(Math.Abs(lo[i] - c.Lo[i]) <= Tol,
                $"{c.Name}: lo[{i}] C# {lo[i]:R} vs Python {c.Lo[i]:R}");
            Assert.True(Math.Abs(hi[i] - c.Hi[i]) <= Tol,
                $"{c.Name}: hi[{i}] C# {hi[i]:R} vs Python {c.Hi[i]:R}");
        }

        if (c.AutoBase)
        {
            var baseProf = Raceline.AutoBaseProfile(props, S, lo, hi);
            for (int i = 0; i < baseProf.Length; i++)
                Assert.True(Math.Abs(baseProf[i] - c.BaseProf[i]) <= Tol,
                    $"{c.Name}: base_prof[{i}] C# {baseProf[i]:R} vs Python {c.BaseProf[i]:R}");
        }
    }

    // ------------------------------------------------------------------
    // cache semantics: hits/misses, copy-out (callers may mutate), FIFO.
    // ------------------------------------------------------------------

    [Fact]
    public void CacheReturnsCopiesAndCountsHits()
    {
        var reference = LoadReference();
        var c = reference.Cases.First(x => x.Name == "corner_right");
        Raceline.ClearCache();
        var (h0, m0, _) = Raceline.CacheStats();

        var network = TestMaps.BuildBasicTestMap();
        InjectPythonGeometry(network);
        var r1 = Raceline.SolveLine(network, c.Rounded, c.SegIdx.ToList(),
            baseOffset: c.BaseOffset, autoBase: c.AutoBase,
            mergeFromM: c.MergeFromM, mergeS0: c.MergeS0, mergeS1: c.MergeS1);
        var r2 = Raceline.SolveLine(network, c.Rounded, c.SegIdx.ToList(),
            baseOffset: c.BaseOffset, autoBase: c.AutoBase,
            mergeFromM: c.MergeFromM, mergeS0: c.MergeS0, mergeS1: c.MergeS1);

        var (h1, m1, _) = Raceline.CacheStats();
        Assert.Equal(m0 + 1, m1);          // one miss
        Assert.Equal(h0 + 1, h1);          // one hit

        // Mutating the returned copy must not corrupt the cached original.
        r2.Offsets[0] = -999.0;
        r2.Points[0] = (1e6, 1e6);
        var r3 = Raceline.SolveLine(network, c.Rounded, c.SegIdx.ToList(),
            baseOffset: c.BaseOffset, autoBase: c.AutoBase,
            mergeFromM: c.MergeFromM, mergeS0: c.MergeS0, mergeS1: c.MergeS1);
        Assert.Equal(r1.Offsets[0], r3.Offsets[0]);
        Assert.Equal(r1.Points[0], r3.Points[0]);

        Raceline.ClearCache();
        var (_, _, size) = Raceline.CacheStats();
        Assert.Equal(0, size);
    }

    // ------------------------------------------------------------------
    // re-baseline tool
    // ------------------------------------------------------------------

    /// <summary>RE-BASELINE: rewrites data/raceline_reference/basic.json from the
    /// CURRENT C# implementation AND THE CURRENT MAP. The original dump came
    /// from the retired pygame repo (tools/raceline_reference.py); per the user
    /// decision of 2026-09-11 the C# codebase is its own reference, so this
    /// pins current behaviour as a regression guard, not Python parity.
    ///
    /// The per-case tests are coupled to the MAP LAYOUT (see the note above
    /// NetworkMatchesReference): a deliberate map edit - a road split, a tile
    /// deleted - invalidates the recorded routes, so the baseline has to be
    /// rebuilt from the current network, not just re-solved on the old inputs.
    /// Each recorded route is re-resolved by its NODE NAMES on the current map;
    /// a route whose nodes no longer connect is dropped and reported, so the
    /// rebuild never silently solves a route that does not exist.
    ///
    /// Run on purpose after intentional raceline or map changes, then re-run
    /// the suite:
    ///   dotnet test DrivingGame.Sim.Tests/ --filter "FullyQualifiedName~RebaselineReferenceFile"
    /// </summary>
    [Fact(Skip = "manual re-baseline tool - run on purpose, then re-run the reference tests")]
    public void RebaselineReferenceFile()
    {
        var reference = LoadReference();
        var network = TestMaps.BuildBasicTestMap();
        // NO InjectPythonGeometry here: the baseline is regenerated FROM the
        // current map, so its `safe` polygon is the production (NTS) erosion -
        // which is exactly what the gate tests then inject back, making the
        // injected and the production path identical by construction.

        using var origDoc = JsonDocument.Parse(File.ReadAllText(FindReferenceFile()));
        var root = origDoc.RootElement;
        // The dump keeps its geometry in its own frame, so everything
        // frame-valued gets written back in that frame.
        double pxPerM = FixturePxPerM(root);

        JsonArray PtsJson(List<(double X, double Y)> pts) =>
            PtsJsonInto(pts, pxPerM);
        JsonArray PtsJsonInto(List<(double X, double Y)> pts, double pxPerM) =>
            new(pts.Select(p => (JsonNode)new JsonArray(p.X * pxPerM, p.Y * pxPerM)).ToArray());
        JsonArray Arr(double[] a) =>
            new(a.Select(v => (JsonNode)v).ToArray());

        var casesJson = new JsonArray();
        var dropped = new List<string>();
        foreach (var c in reference.Cases)
        {
            // Re-resolve the recorded route on the CURRENT map by node name.
            // Segment INDICES shift with every map edit, so they are never
            // carried over - only the node chain is.
            var segIdx = new List<int>();
            var chain = new List<string>();
            bool resolvable = true;
            for (int i = 0; i + 1 < c.Nodes.Length; i++)
            {
                var hops = HopOf(network, c.Nodes[i], c.Nodes[i + 1]);
                if (hops is null) { resolvable = false; break; }
                var (idxs, mids) = hops.Value;
                segIdx.AddRange(idxs);
                chain.Add(c.Nodes[i]);
                chain.AddRange(mids);
            }
            if (!resolvable || segIdx.Count == 0)
            {
                dropped.Add(c.Name);
                continue;
            }
            chain.Add(c.Nodes[^1]);

            // The recorded route may have been re-drawn on the current map
            // (a road split at a new node), so the node chain is rebuilt too -
            // the rounded polyline must follow the SAME chain the segments
            // describe, or the two halves of the case disagree.
            var rounded = RoadNetworkGeometry.SmoothChain(
                chain.Select(n => network.Nodes[n]).ToList());

            // Same pipeline as the gate tests: solve on the regenerated geometry,
            // intermediates from the resampled rounded centerline.
            var result = Raceline.SolveLine(
                network, rounded, segIdx,
                baseOffset: c.BaseOffset, autoBase: c.AutoBase,
                mergeFromM: c.MergeFromM, mergeS0: c.MergeS0, mergeS1: c.MergeS1);

            var (P, S, _) = Raceline.Resample(rounded, Raceline.SampleM);
            var props = Raceline.StationSegments(network, segIdx, P);
            var (N, K) = Raceline.NormalsAndCurvature(P, Raceline.SampleM);
            var junction = Raceline.JunctionNodePerStation(network, P);
            var (lo, hi) = Raceline.LegalCorridor(network, P, N, props, junction);
            double[] baseProf = c.AutoBase
                ? Raceline.AutoBaseProfile(props, S, lo, hi)
                : Array.Empty<double>();

            var kwargs = new JsonObject();
            if (c.BaseOffset is double bo) kwargs["base_offset"] = bo;
            if (c.AutoBase) kwargs["auto_base"] = true;
            if (c.MergeFromM is double mf) kwargs["merge_from_m"] = mf;
            if (c.MergeS0 != 0.0) kwargs["merge_s0"] = c.MergeS0;
            if (c.MergeS1 != 0.0) kwargs["merge_s1"] = c.MergeS1;

            casesJson.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["nodes"] = new JsonArray(chain.Select(n => (JsonNode)n).ToArray()),
                ["seg_idx"] = new JsonArray(segIdx.Select(i => (JsonNode)i).ToArray()),
                ["kwargs"] = kwargs,
                ["rounded"] = PtsJson(rounded),
                ["P"] = PtsJson(result.Points),
                ["N"] = PtsJsonInto(result.Normals, 1.0),   // unit normals: NO frame scale
                ["offsets"] = Arr(result.Offsets),
                ["cum"] = Arr(result.Cum),
                ["K"] = Arr(K),
                ["lo"] = Arr(lo),
                ["hi"] = Arr(hi),
                ["base_prof"] = Arr(baseProf),
                ["props"] = new JsonArray(props.Select(p =>
                    (JsonNode)new JsonArray(p.Oneway ? 1 : 0, p.Width, p.Lanes, p.Parking)).ToArray()),
                ["junction"] = new JsonArray(junction.Select(j =>
                    j is null ? (JsonNode?)null
                              : new JsonArray(j.Value.X * pxPerM, j.Value.Y * pxPerM)).ToArray()),
            });
        }

        var outDoc = new JsonObject
        {
            ["map"] = root.GetProperty("map").GetString()!,
            ["pxPerM"] = pxPerM,
            ["corner_radius_m"] = Config.ROAD_CORNER_RADIUS_M,
            ["corner_arc_steps"] = 10,
            ["node_count"] = network.Nodes.Count,
            ["segment_count"] = network.Segments.Count,
            ["nodes"] = new JsonObject(network.Nodes.ToDictionary(
                kv => kv.Key, kv => (JsonNode)new JsonArray(kv.Value.X * pxPerM,
                                                            kv.Value.Y * pxPerM))),
            ["node_degree"] = new JsonObject(network.NodeDegree.ToDictionary(
                kv => kv.Key, kv => (JsonNode)kv.Value)),
            // Map geometry regenerated from the CURRENT network (GeoJSON in the
            // fixture's own frame) - the old dump's `paved`/`safe` describe a
            // map that no longer exists.
            ["paved"] = GeoJsonOf(network.GetPavedPolygon(), pxPerM),
            ["safe"] = GeoJsonOf(network.RacelineSafe, pxPerM),
            ["cases"] = casesJson,
        };

        string path = FindReferenceFile();
        using var fs = new FileStream(path, FileMode.Create);
        using (var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            outDoc.WriteTo(writer);
        _reference = null;   // force reload on the next test run
        Console.WriteLine($"re-baseline: {casesJson.Count} cases kept, " +
                          $"{dropped.Count} dropped (route no longer on the map): " +
                          $"{string.Join(", ", dropped)} - {path}");
    }

    /// <summary>The segment run joining two nodes on the CURRENT map: direct
    /// when one segment connects them, otherwise ONE intermediate node - which
    /// is what a deliberate road split introduces (a 300 m segment becomes two
    /// 150 m segments through a new mid node). Returns (segment indices,
    /// intermediate node names), or null when the two nodes are not connected
    /// at all - the route then no longer exists and the case is dropped.
    ///
    /// One level of splitting is enough for the map edits this project makes
    /// (a road is split, not subdivided repeatedly); deeper chains report as
    /// unresolvable rather than guessing a path through the graph.</summary>
    private static (List<int>, List<string>)? HopOf(RoadNetwork net, string a, string b)
    {
        var direct = SegIdxOf(net, a, b);
        if (direct is not null) return (new List<int> { direct.Value }, new List<string>());
        // One intermediate node: what a deliberate road split introduces.
        // Walk the segments that touch `a` (NodeConnections is only filled by
        // the OSM loader, so the adjacency comes from the segment list itself)
        // and see whether any of their far ends connects to `b`.
        for (int i = 0; i < net.Segments.Count; i++)
        {
            var s = net.Segments[i];
            if (s.StartNode != a && s.EndNode != a) continue;
            var m = s.StartNode == a ? s.EndNode : s.StartNode;
            if (m == b || m == a) continue;
            var h2 = SegIdxOf(net, m, b);
            if (h2 is null) continue;
            return (new List<int> { i, h2.Value }, new List<string> { m });
        }
        return null;
    }

    /// <summary>Segment index joining two nodes in either direction, or null
    /// when the current map has no such road.</summary>
    private static int? SegIdxOf(RoadNetwork net, string a, string b)
    {
        for (int i = 0; i < net.Segments.Count; i++)
        {
            var s = net.Segments[i];
            if ((s.StartNode == a && s.EndNode == b) ||
                (s.StartNode == b && s.EndNode == a))
                return i;
        }
        return null;
    }

    /// <summary>NTS geometry -> the shapely __geo_interface__ object the
    /// loader reads, written in the fixture's own frame.</summary>
    private static JsonNode GeoJsonOf(Geometry g, double pxPerM)
    {
        List<Polygon> polys = g switch
        {
            MultiPolygon mp => mp.Geometries.Cast<Polygon>().ToList(),
            Polygon p => new List<Polygon> { p },
            _ => new List<Polygon>(),
        };
        JsonArray Rings(Polygon poly)
        {
            var rings = new List<LinearRing> { (LinearRing)poly.ExteriorRing };
            rings.AddRange(poly.InteriorRings.Select(r => (LinearRing)r));
            return new(rings.Select(r =>
                (JsonNode)new JsonArray(r.Coordinates.Select(c =>
                    (JsonNode)new JsonArray(c.X * pxPerM, c.Y * pxPerM)).ToArray())).ToArray());
        }
        return new JsonObject
        {
            ["type"] = polys.Count == 1 ? "Polygon" : "MultiPolygon",
            ["coordinates"] = polys.Count == 1
                ? Rings(polys[0])
                : new JsonArray(polys.Select(pp => (JsonNode)Rings(pp)).ToArray()),
        };
    }

    static void InjectPythonGeometry(RoadNetwork network)
    {
        var reference = LoadReference();
        network.RacelineSafe = reference.Safe;
        network.RacelineSafePrep = PreparedGeometryFactory.Prepare(reference.Safe);
    }
}
