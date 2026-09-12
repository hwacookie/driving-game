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

        var nodes = new Dictionary<string, (double X, double Y)>();
        foreach (var kv in root.GetProperty("nodes").EnumerateObject())
            nodes[kv.Name] = (kv.Value[0].GetDouble(), kv.Value[1].GetDouble());

        var degree = new Dictionary<string, int>();
        foreach (var kv in root.GetProperty("node_degree").EnumerateObject())
            degree[kv.Name] = kv.Value.GetInt32();

        var cases = new List<CaseData>();
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            string Name(string p) => c.GetProperty(p).GetString()!;

            var rounded = Pts(c.GetProperty("rounded"));
            var p = Pts(c.GetProperty("P"));
            var n = Pts(c.GetProperty("N"));
            double[] Arr(string q) => c.GetProperty(q).EnumerateArray().Select(e => e.GetDouble()).ToArray();

            var props = c.GetProperty("props").EnumerateArray()
                .Select(e => (e[0].GetInt32() != 0, e[1].GetDouble(), e[2].GetInt32(), e[3].GetDouble()))
                .ToArray();
            var junction = c.GetProperty("junction").EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Null
                    ? ((double X, double Y)?)null
                    : (e[0].GetDouble(), e[1].GetDouble()))
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
            Paved = BuildGeometry(root.GetProperty("paved")),
            Safe = BuildGeometry(root.GetProperty("safe")),
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

    static List<(double X, double Y)> Pts(JsonElement arr) =>
        arr.EnumerateArray().Select(e => (e[0].GetDouble(), e[1].GetDouble())).ToList();

    /// <summary>Build an NTS geometry from a shapely __geo_interface__ object.</summary>
    static Geometry BuildGeometry(JsonElement gi)
    {
        string type = gi.GetProperty("type").GetString()!;
        var coords = gi.GetProperty("coordinates");
        List<Polygon> polys = type switch
        {
            "Polygon" => new() { ToPolygon(coords[0]) },
            "MultiPolygon" => coords.EnumerateArray().Select(p => ToPolygon(p)).ToList(),
            _ => throw new NotSupportedException($"geometry type {type}"),
        };
        return polys.Count > 1 ? new MultiPolygon(polys.ToArray()) : polys[0];
    }

    static Polygon ToPolygon(JsonElement ringArr)
    {
        Coordinate[] Ring(JsonElement r) =>
            r.EnumerateArray().Select(c => new Coordinate(c[0].GetDouble(), c[1].GetDouble())).ToArray();
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
    /// CURRENT C# implementation. The original dump came from the retired pygame
    /// repo (tools/raceline_reference.py); per user decision the C# codebase is
    /// now its own reference, so this test pins current behaviour as a regression
    /// guard instead of Python parity. Run on purpose after intentional raceline
    /// changes, then re-run the suite:
    ///   dotnet test DrivingGame.Sim.Tests/ --filter "FullyQualifiedName~RebaselineReferenceFile"
    /// </summary>
    [Fact(Skip = "manual re-baseline tool - run on purpose, then re-run the reference tests")]
    public void RebaselineReferenceFile()
    {
        var reference = LoadReference();
        var network = TestMaps.BuildBasicTestMap();
        InjectPythonGeometry(network);

        using var origDoc = JsonDocument.Parse(File.ReadAllText(FindReferenceFile()));
        var root = origDoc.RootElement;

        JsonArray PtsJson(List<(double X, double Y)> pts) =>
            new(pts.Select(p => (JsonNode)new JsonArray(p.X, p.Y)).ToArray());
        JsonArray Arr(double[] a) =>
            new(a.Select(v => (JsonNode)v).ToArray());

        var casesJson = new JsonArray();
        foreach (var c in reference.Cases)
        {
            // Same pipeline as the gate tests: solve on the injected geometry,
            // intermediates from the resampled rounded centerline.
            var result = Raceline.SolveLine(
                network, c.Rounded, c.SegIdx.ToList(),
                baseOffset: c.BaseOffset, autoBase: c.AutoBase,
                mergeFromM: c.MergeFromM, mergeS0: c.MergeS0, mergeS1: c.MergeS1);

            var (P, S, _) = Raceline.Resample(c.Rounded, Raceline.SampleM);
            var props = Raceline.StationSegments(network, c.SegIdx.ToList(), P);
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
                ["nodes"] = new JsonArray(c.Nodes.Select(n => (JsonNode)n).ToArray()),
                ["seg_idx"] = new JsonArray(c.SegIdx.Select(i => (JsonNode)i).ToArray()),
                ["kwargs"] = kwargs,
                ["rounded"] = PtsJson(c.Rounded),
                ["P"] = PtsJson(result.Points),
                ["N"] = PtsJson(result.Normals),
                ["offsets"] = Arr(result.Offsets),
                ["cum"] = Arr(result.Cum),
                ["K"] = Arr(K),
                ["lo"] = Arr(lo),
                ["hi"] = Arr(hi),
                ["base_prof"] = Arr(baseProf),
                ["props"] = new JsonArray(c.Props.Select(p =>
                    (JsonNode)new JsonArray(p.Oneway ? 1 : 0, p.Width, p.Lanes, p.Parking)).ToArray()),
                ["junction"] = new JsonArray(c.Junction.Select(j =>
                    j is null ? (JsonNode?)null
                              : new JsonArray(j.Value.X, j.Value.Y)).ToArray()),
            });
        }

        var outDoc = new JsonObject
        {
            ["map"] = root.GetProperty("map").GetString()!,
            ["pppm"] = root.GetProperty("pppm").GetDouble(),
            ["corner_radius_m"] = root.GetProperty("corner_radius_m").GetDouble(),
            ["corner_arc_steps"] = root.GetProperty("corner_arc_steps").GetInt32(),
            ["node_count"] = reference.NodeCount,
            ["segment_count"] = reference.SegmentCount,
            ["nodes"] = new JsonObject(reference.Nodes.ToDictionary(
                kv => kv.Key, kv => (JsonNode)new JsonArray(kv.Value.X, kv.Value.Y))),
            ["node_degree"] = new JsonObject(reference.NodeDegree.ToDictionary(
                kv => kv.Key, kv => (JsonNode)kv.Value)),
            ["paved"] = JsonNode.Parse(root.GetProperty("paved").GetRawText())!,
            ["safe"] = JsonNode.Parse(root.GetProperty("safe").GetRawText())!,
            ["cases"] = casesJson,
        };

        string path = FindReferenceFile();
        using var fs = new FileStream(path, FileMode.Create);
        using (var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            outDoc.WriteTo(writer);
        _reference = null;   // force reload on the next test run
    }

    static void InjectPythonGeometry(RoadNetwork network)
    {
        var reference = LoadReference();
        network.RacelineSafe = reference.Safe;
        network.RacelineSafePrep = PreparedGeometryFactory.Prepare(reference.Safe);
    }
}
