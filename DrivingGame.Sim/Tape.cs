// Tape.cs — the RECORD of a run: every substep, every car.
//
// Why a tape at all: reviewing a manoeuvre by watching it once at 1x loses the
// moment (the eye has not found the cars while the sim is already ticking), and
// "why did it stop" can only be reconstructed from a decision log after the fact.
// A tape makes both frame-addressable. See docs/RECORD_PLAYBACK.md.
//
// The clock is SUBSTEPS, never the render frame counter: a slow wall-clock frame
// runs 2 substeps in one iteration (SimEngine.cs:119-124), so `Frame` is not a
// safe index. Every record keys on _simStepsTotal, and DtFixed is announced in
// the file header so an offline reader can rebuild seconds.
//
// User decisions 2026-10-21 (binding): record EVERY SUBSTEP, keep the log for
// EVERY TEST RUN, size is not a constraint. The only guard against runaway
// memory is MaxSteps - generous, not a rolling window: a run is kept whole.

using System.IO;

namespace DrivingGame.Sim;

/// <summary>A car's pose at one substep - what the renderer drew.</summary>
public sealed class TapeCar
{
    public int Uid { get; internal set; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double HeadingDeg { get; internal set; }
    public double SpeedMps { get; internal set; }
    /// <summary>The DECK this car drives on (0 = ground, 1 = bridge, …) - the
    /// level of the segment it was on at this substep. The renderer offsets
    /// a car by it (`MapRenderer.cs:3772` reads `CarSample.Level`), so a tape
    /// without it cannot say what the frame under the cursor looked like:
    /// the reviewer painted every bridge car AT GRADE (measured on
    /// it: `TapeCar` had no level and `ReviewFeed` painted `Level = 0` for
    /// every tape car (a code fact, visible in two greps - the field list and
    /// that hard-code), and the A/B settles what it does to the PICTURE: the
    /// SAME tape at the SAME cursor renders the deck car ON the bridge with its
    /// recorded level and hidden UNDER the deck with the column zeroed
    /// `Level = 0`). `/state` had it from the start (`SimEngine.cs:1264`),
    /// the tape was the channel that lacked it — the general rule this fix
    /// instantiates: **every field the client draws is a tape field, or the
    /// HUD must say `live-only`** (binding, 2026-10-25).</summary>
    public int Level { get; internal set; }
    public int Blinkers { get; internal set; }      // bit 0 left, bit 1 right
    public bool Brake { get; internal set; }
    public bool WrongSide { get; internal set; }
    public int CrashEpisodes { get; internal set; }
    public string Color { get; internal set; }      // the sprite -> vehicle class
    public int Lamps { get; internal set; }         // bit0 headlights, bit1 taillights
    /// <summary>The CAR owning this car's cap (0 = nobody: free, or held by
    /// the kerb rather than a driver). Written BEFORE `capKey`, which is the
    /// last field and may itself contain commas (generation 4,
    /// docs/CAR_DETAIL_WINDOW.md).</summary>
    public int HeldByUid { get; internal set; }
    public string CapKey { get; internal set; }     // the rule owning the cap, "" = none
}

/// <summary>A driver decision that happened AT one substep - sparse by
/// construction: recorded only when a new event pops up, so the tape grows by
/// the size of the text, not by one line per car per step.</summary>
public sealed class TapeEvent
{
    public int Uid { get; internal set; }
    public string Msg { get; internal set; } = "";
}

/// <summary>Every car at one substep, plus the fleet's fluidity at that moment
/// - the per-substep value that lets two runs be compared curve against curve
/// (docs/RECORD_PLAYBACK.md).</summary>
public sealed class TapeFrame
{
    public long Step { get; internal set; }
    public List<TapeCar> Cars { get; internal set; } = new();
    public double FluidityMpsPerCar { get; internal set; }   // rolling-window m/s per car
    public double MovingShare { get; internal set; }         // 0..1, cars going anywhere
    public List<TapeEvent> Events { get; internal set; } = new();   // decisions made here
}

/// <summary>One camera-focus declaration, stamped with the substep it was
/// made at. What a scenario is ABOUT ("watch this car" / "watch this place")
/// is authored by the suite, announced over POST /focus, exported live in
/// /state.focus for the viewer and recorded here so the reviewer can replay
/// the SAME framing from the file — the run's camera intent is part of the
/// run, not a property of whatever window happens to show it.</summary>
public readonly record struct FocusNote(long Step, string Kind, double X, double Y,
                                        double ZoomPxM, string Name, long Uid, string Label,
                                        string Scenario, string Desc, long NameUid = 0)
{
    /// <summary>The tape-file form (one `# focus …` line per note).</summary>
    public string ToHeaderLine() =>
        $"# focus step={Step} kind={Kind}" +
        (Kind == "loc" ? $" x={X:F2} y={Y:F2}" : "") +
        (ZoomPxM > 0 ? $" zoom={ZoomPxM:F2}" : "") +
        (Name.Length > 0 ? $" name={Name}" : "") +
        (Uid > 0 ? $" uid={Uid}" : "") +
        // WHICH car the name belongs to. A name is NOT a key: the suite reuses
        // "parked_car"/"slow_car" in scenario after scenario, and the tape's
        // uid→name header keeps only the LAST holder of each — so resolving a
        // named focus by scanning names picked a car from a later scenario and
        // the review camera followed nothing ("uid=27 not on this frame",
        // measured 2026-10-24). The uid the scenario actually staged is a fact.
        (NameUid > 0 ? $" name_uid={NameUid}" : "") +
        (Label.Length > 0 ? $" label={TestResultNote.Esc(Label)}" : "") +
        (Scenario.Length > 0 ? $" scenario={TestResultNote.Esc(Scenario)}" : "") +
        (Desc.Length > 0 ? $" desc={TestResultNote.Esc(Desc)}" : "");
}

/// <summary>One scenario's VERDICT, recorded when the suite files it. The
/// reviewer shows "how did this test end" next to the frame it is scrubbing,
/// and it must answer that from the file alone — the run is over, the host may
/// be gone, and re-deriving a verdict from the text log would make the tape
/// depend on a second format. So the suite POSTs what it printed, once per
/// scenario, and the tape keeps it keyed by the same label the focus notes
/// carry ("17/27").</summary>
public readonly record struct TestResultNote(long Step, int Number, string Label,
                                             string Scenario, bool Passed, string Reason,
                                             double SimS, double WallS, int Crossings, int OffRoad)
{
    public string ToHeaderLine() =>
        $"# result step={Step} number={Number} label={Esc(Label)} scenario={Esc(Scenario)} " +
        $"passed={(Passed ? 1 : 0)} reason={Esc(Reason)} sim={SimS:F1} wall={WallS:F1} " +
        $"xng={Crossings} off={OffRoad}";

    /// <summary>Free text in a space-separated line needs to stay ONE token and
    /// must survive the round trip — so it goes through percent-encoding rather
    /// than a hand-rolled character swap.</summary>
    internal static string Esc(string v) => Uri.EscapeDataString(v ?? "");
    internal static string Unesc(string v) =>
        string.IsNullOrEmpty(v) ? "" : Uri.UnescapeDataString(v);
}

/// <summary>The run's tape: per-substep poses + the rule owning each car's
/// speed cap. Best-effort: a disk error never breaks the sim.</summary>
public static class RunTape
{
    // The clock unit this tape was recorded against - announced in the header.
    public static double DtFixedS = 1.0 / 60.0;

    // A generous ceiling, not a rolling window: 200 000 substeps = ~55 min at
    // 60 Hz. Beyond it the tape stops RECORDING (the sim keeps running) and
    // says so, rather than evicting the beginning of a run someone may scrub.
    public static readonly int MaxSteps = 200_000;

    private static readonly object Lock = new();
    private static bool _recording;
    private static string _mapName = "";
    private static string _mapHash = "";   // the map's GEOMETRY, not just its name

    /// <summary>The car-field grammar, declared ONCE: `Save` writes it as a
    /// header line, a tip hop answers with it, and every reader decides the
    /// field generation from it (docs/RECORD_PLAYBACK.md's rule: a token count
    /// cannot tell a generation-4 cap key with a comma in it from a generation-5
    /// record). Three copies of this string is how a file and a feed could
    /// disagree about one byte.</summary>
    private static readonly string FrameLegend =
        "# step;fluidity_mps_per_car;moving_share;uid,x,y,heading_deg,speed_mps," +
        "blinkers,brake,wrong,crashes,color,lamps,heldBy,level,capKey|...;~uid=decision text~…";
    private static readonly List<TapeFrame> _frames = new();
    // Substeps at which something worth scrubbing to happened (a decision on
    // any car, a fresh contact). Appended in step order, so it stays sorted.
    private static readonly List<long> _events = new();
    /// <summary>uid -> the NAME the scenario gave the car ("slow_car",
    /// "oncoming_car", "parked_car"). A run has a cast, the cast has roles,
    /// and the roles were authored at staging - so they belong in the TAPE
    /// HEADER, once, not repeated on all 800+ frames. A renamed car (a
    /// scenario that re-labels mid-run) keeps the first name: the tape
    /// describes who drove, not who said so.
    private static readonly Dictionary<long, string> _names = new();
    /// <summary>uid -> the KIND of actor the scenario staged ("car" is the only
    /// kind in use today; absent means car). Like the cast roles, an actor's
    /// kind is authored ONCE at staging and never changes per substep, so it
    /// lives in the HEADER next to `names=` and the frames stay a per-actor
    /// pose list — which is the ACTOR channel the class decision of 2026-10-25
    /// asks for (docs/RECORD_PLAYBACK.md → "The channels"): a policeman
    /// directing traffic moves every substep, so he is a FRAME, and the only
    /// thing the frame list cannot say today is that an entry is not a car.
    /// The car-shaped fields (`color`, `capKey`, `heldBy`, `lamps`, `blinkers`)
    /// simply stay empty for another kind: they describe a car, not an actor.
    /// No producer exists yet — nothing but cars can be staged — so this is
    /// read/write format support, deliberately not a hook that pretters unused.</summary>
    private static readonly Dictionary<long, string> _kinds = new();

    /// <summary>The per-car VIEW options a scenario declared at staging
    /// (issue #9): a detail window for the car, and its own tyre lines. Only
    /// DECLARED cars are in here — absent means "as before" (the followed car
    /// draws its lines, no window opens), which is what keeps an unauthored cast
    /// looking exactly like yesterday. Header token per car:
    /// `caropts=2:detail=on+tracks=on+livery=c62828,7:tracks=off`.</summary>
    private static readonly Dictionary<long, (bool? Detail, bool? Tracks, string? Livery)> _carOpts = new();

    /// <summary>A per-car option that changed DURING the run (issue #10) — a
    /// repaint at the kerb, tyre lines switched on to look at a wobble. The
    /// staged header (`caropts=`) can only say what the cast was BORN with, so a
    /// change needs a fact of its own, keyed on a substep like a focus note:
    /// a review then shows the value the run had AT THE CURSOR, not the value it
    /// happened to end with. Only the fields that changed carry a token, so the
    /// line never invents a value nobody set.</summary>
    public readonly record struct CarOptionEvent(long Step, long Uid,
                                                 bool? Detail, bool? Tracks,
                                                 string? Livery)
    {
        public string Line =>
            $"# caropts step={Step} uid={Uid}" +
            (!string.IsNullOrEmpty(Livery) ? $" livery={Livery!.Replace("#", "").ToLowerInvariant()}" : "") +
            (Tracks.HasValue ? $" tracks={(Tracks.Value ? "on" : "off")}" : "") +
            (Detail.HasValue ? $" detail={(Detail.Value ? "on" : "off")}" : "");
    }

    private static readonly List<CarOptionEvent> _carOptEvents = new();

    /// <summary>Option changes in tape order (ascending substep).</summary>
    public static IReadOnlyList<CarOptionEvent> CarOptionEvents
    {
        get { lock (Lock) return _carOptEvents; }
    }

    /// <summary>Record one per-car option change at this substep. A call that
    /// says nothing (no field set) records nothing.</summary>
    public static void NoteCarOptions(long step, long uid, bool? detail,
                                      bool? tracks, string? livery)
    {
        if (!detail.HasValue && !tracks.HasValue && string.IsNullOrEmpty(livery)) return;
        // Same rule as Record: a sim that is not recording writes no tape, so it
        // must not accumulate events either - they would surface on the next,
        // unrelated Save.
        if (!_recording) return;
        lock (Lock) _carOptEvents.Add(new CarOptionEvent(step, uid, detail, tracks, livery));
    }
    private static readonly List<FocusNote> _focus = new();
    private static readonly List<TestResultNote> _results = new();

    /// <summary>Verdicts filed by the suite, in the order they arrived.</summary>
    public static IReadOnlyList<TestResultNote> ResultNotes
    {
        get { lock (Lock) return _results; }
    }

    /// <summary>Record one scenario verdict at the current tape head.</summary>
    public static void NoteTestResult(TestResultNote r)
    {
        lock (Lock)
        {
            if (!_recording) return;
            for (int i = _results.Count - 1; i >= 0; i--)
                if (_results[i].Label == r.Label && r.Label.Length > 0)
                { _results[i] = r; return; }     // a re-run of the same label wins
            _results.Add(r);
        }
    }

    /// <summary>The actor kinds the header carries (uid -> kind). Absent means
    /// "car". Written by `SetKind` at staging, read back by `Load` — the kind
    /// never changes per substep, so it is a header map and the frames stay a
    /// per-actor pose list (docs/RECORD_PLAYBACK.md → "The cast list … and the
    /// actor kinds beside it"). Copied out so a caller cannot mutate the tape's
    /// own map.</summary>
    public static Dictionary<long, string> Kinds
    {
        get { lock (Lock) return new Dictionary<long, string>(_kinds); }
    }

    /// <summary>Name an actor's KIND at staging (default is "car", so naming
    /// "car" is a no-op). Nothing calls this today — only cars can be staged —
    /// and the FIRST non-car actor (a policeman directing the traffic) will.
    /// First name wins: a kind is authored at staging, like a role
    /// (docs/RECORD_PLAYBACK.md's rule for re-labelled cars).</summary>
    public static void SetKind(long uid, string kind)
    {
        lock (Lock)
        {
            if (kind == null || kind == "" || kind == "car") return;
            if (!_kinds.ContainsKey(uid)) _kinds[uid] = kind;
        }
    }

    /// <summary>The declared per-car view options, copied out so a caller cannot
    /// mutate the tape's own map (same rule as <see cref="Kinds"/>).</summary>
    public static Dictionary<long, (bool? Detail, bool? Tracks, string? Livery)> CarOptions
    {
        get { lock (Lock) return new Dictionary<long, (bool?, bool?, string?)>(_carOpts); }
    }

    /// <summary>Record what the scenario asked of THIS car (issues #9 and #10).
    /// A null side means "not said" and is not stored, so the header carries real
    /// decisions only; a later call replaces an earlier one for the same uid —
    /// a re-staged cast IS a new decision, unlike a role, where first-wins keeps
    /// the names of a run stable. `livery` is the `#rrggbb` the API resolved; the
    /// `#` is not written (the token is space- and comma-separated, and a bare
    /// hex reads unambiguously).</summary>
    public static void SetCarOptions(long uid, bool? detail, bool? tracks,
                                     string? livery = null)
    {
        lock (Lock)
        {
            if (detail is null && tracks is null && string.IsNullOrEmpty(livery)) return;
            _carOpts[uid] = (detail, tracks, string.IsNullOrEmpty(livery) ? null : livery);
        }
    }

    /// <summary>The scenario's camera focus, in the order they were announced.
    /// A client scrubbing the tape asks: which note is the newest one at or
    /// before my cursor — that is the framing THAT test asked for.</summary>
    public static IReadOnlyList<FocusNote> FocusNotes
    {
        get { lock (Lock) return _focus; }
    }

    /// <summary>Record one focus declaration at the current tape head.</summary>
    public static void NoteFocus(FocusNote note)
    {
        lock (Lock)
        {
            if (!_recording) return;
            // Same scenario re-announcing (a re-spawn, a retry) replaces its
            // note instead of stacking a second one at a later step.
            for (int i = _focus.Count - 1; i >= 0; i--)
                if (_focus[i].Label == note.Label && note.Label.Length > 0)
                { _focus[i] = note; return; }
            _focus.Add(note);
        }
    }

    /// <summary>The run-log directory: <repo root>/tests, found the same way
    /// CrashDumper finds <repo root>/logs (walk up to project.godot). Tapes
    /// live next to the suite's text logs so a report and its frames stay a
    /// pair.</summary>
    public static string DefaultDir()
    {
        string root = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot")))
            {
                root = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }
        return Path.Combine(root, "tests");
    }

    /// <summary>The recorded runs on disk - what a review client needs to pick
    /// one. Newest first. Metadata comes from the file's own header, so a tape
    /// written by an older build still lists honestly.</summary>
    public static List<Dictionary<string, object?>> List(string dir = null)
    {
        var runs = new List<Dictionary<string, object?>>();
        var d = dir ?? DefaultDir();
        try
        {
            foreach (var f in new DirectoryInfo(d).GetFiles("tape_*.txt"))
            {
                string head = File.ReadAllText(f.FullName).Split("\n")[0];
                var meta = new Dictionary<string, object?>
                {
                    ["path"] = f.FullName,
                    // the file NAME carries the run stamp (tape_T_<nnn>_<sel>_<stamp>),
                    // so the name is both the label and the sort key - no
                    // filesystem mtime round-trip needed.
                    ["name"] = f.FullName.Split("/")[^1],
                };
                // "# DGTAP1 map=basic dt_fixed=0.016667 steps=828"
                foreach (var tok in head.Split(" "))
                {
                    int eq = tok.IndexOf('=');
                    if (eq > 0) meta[tok.Substring(0, eq)] = tok.Substring(eq + 1);
                }
                runs.Add(meta);
            }
        }
        catch (Exception)
        {
            return runs;
        }
        // Directory walking is alphabetical, and the file NAME carries the run
        // stamp (tape_T_<nnn>_<sel>_<stamp>), so reversing gives newest first.
        runs.Reverse();
        return runs;
    }

    /// <summary>Load a recorded run back into memory so it can be scrubbed
    /// through /tape/frame. Recording stays OFF - a loaded tape is a tape under
    /// review, not a live one; the running sim keeps its own tape via /tape.
    /// Returns false if the file is missing or unreadable.</summary>
    public static bool Load(string path)
    {
        var frames = new List<TapeFrame>();
        string mapName = "";
        double dt = DtFixedS;
        var namesLoaded = new Dictionary<long, string>();
        var kindsLoaded = new Dictionary<long, string>();
        var carOptsLoaded = new Dictionary<long, (bool? Detail, bool? Tracks, string? Livery)>();
        var carOptEventsLoaded = new List<CarOptionEvent>();
        bool headerDeclaresLevel = false, hasLegend = false;
        try
        {
            string text = File.ReadAllText(path);
            foreach (var line in text.Split("\n"))
            {
                if (line.StartsWith("#"))
                {
                    // The file's OWN format line declares the car-field
                    // grammar. Field COUNTING alone cannot: a generation-4
                    // record whose cap key carries the delimiter (`r32#12,passage#4`,
                    // docs/CAR_DETAIL_WINDOW.md) has 14 tokens, exactly what a
                    // generation-5 record has - counted, the old key would be
                    // read as a level plus a shorter key, silently. The legend
                    // is authoritative when present; counting stays the fallback
                    // for the pre-legend files that never wrote one.
                    if (line.StartsWith("# step;"))
                    {
                        hasLegend = true;
                        headerDeclaresLevel = line.Contains("heldBy,level,capKey");
                    }
                    if (line.Contains("map="))
                        mapName = line.Split("map=")[1].Split(" ")[0];
                    if (line.Contains("map_hash="))
                    {
                        // The whole stamp, one token: the wide fingerprint is
                        // colon-joined so this read cannot truncate it. Stamps
                        // written before that change contained spaces and DO
                        // get cut here (`segs=256`) - history stays as it was
                        // recorded, and the client says "unverifiable" rather
                        // than pretending the tail matched.
                        _mapHash = line.Split("map_hash=")[1].Split(" ")[0];
                    }
                    if (line.Contains("names="))
                    {
                        foreach (var pair in line.Split("names=")[1].Split(" ")[0].Split(","))
                        {
                            var kv2 = pair.Split(":");
                            if (kv2.Length == 2)
                                namesLoaded[ParseL(kv2[0], -1L)] = kv2[1];
                        }
                    }
                    if (line.Contains("caropts=") && line.StartsWith("# DGTAP1"))
                    {
                        // uid:detail=on+tracks=off+livery=c62828 - the per-car
                        // view options the test declared (issues #9 and #10). An
                        // absent uid is UNdeclared, which is not the same fact
                        // as "off": the first keeps the standing behaviour.
                        foreach (var pair in line.Split("caropts=")[1].Split(" ")[0].Split(","))
                        {
                            int col = pair.IndexOf(':');
                            if (col <= 0 || !long.TryParse(pair.Substring(0, col), out var optUid)) continue;
                            bool? det = null, trk = null;
                            string? liv = null;
                            foreach (var bit in pair.Substring(col + 1).Split('+'))
                            {
                                int eq = bit.IndexOf('=');
                                if (eq <= 0) continue;
                                string valS = bit.Substring(eq + 1);
                                if (bit.Substring(0, eq) == "detail") det = valS == "on";
                                else if (bit.Substring(0, eq) == "tracks") trk = valS == "on";
                                else if (bit.Substring(0, eq) == "livery" && valS.Length == 6)
                                {
                                    bool hexOk = true;
                                    foreach (var chx in valS)
                                        if (!Uri.IsHexDigit(chx)) hexOk = false;
                                    if (hexOk) liv = "#" + valS.ToLowerInvariant();
                                }
                            }
                            if (det is not null || trk is not null || liv is not null)
                                carOptsLoaded[optUid] = (det, trk, liv);
                        }
                    }
                    if (line.Contains("kinds="))
                    {
                        foreach (var pair in line.Split("kinds=")[1].Split(" ")[0].Split(","))
                        {
                            var kvk = pair.Split(":");
                            if (kvk.Length == 2)
                                kindsLoaded[ParseL(kvk[0], -1L)] = kvk[1];
                        }
                    }
                    if (line.StartsWith("# caropts "))
                    {
                        // One option CHANGE during the run (#10): the substep, the
                        // car, and only the fields that were actually set. They
                        // fold over the staged header at the cursor, which is what
                        // lets a review show the paint a car wore at 12 s instead
                        // of the one it finished with.
                        long ceStep = 0, ceUid = 0;
                        bool? ceDet = null, ceTrk = null;
                        string? ceLiv = null;
                        foreach (var tok in line.Substring(10).Split(' '))
                        {
                            int eq = tok.IndexOf('=');
                            if (eq <= 0) continue;
                            string ck = tok.Substring(0, eq), cv = tok.Substring(eq + 1);
                            switch (ck)
                            {
                                case "step": ceStep = ParseL(cv, 0); break;
                                case "uid": ceUid = ParseL(cv, 0); break;
                                case "livery": ceLiv = "#" + cv; break;
                                case "detail": ceDet = cv == "on"; break;
                                case "tracks": ceTrk = cv == "on"; break;
                            }
                        }
                        if (ceUid != 0)
                            carOptEventsLoaded.Add(new CarOptionEvent(ceStep, ceUid, ceDet, ceTrk, ceLiv));
                    }
                    if (line.StartsWith("# focus "))
                    {
                        var kv3 = line.Substring(8).Split(' ');
                        long fStep = 0, fUid = 0, fNameUid = 0;
                        double fx = 0, fy = 0, fz = 0;
                        string fk = "car", fnm = "", flab = "", fscen = "", fdesc = "";
                        foreach (var tok in kv3)
                        {
                            var eq = tok.IndexOf('=');
                            if (eq <= 0) continue;
                            string k = tok.Substring(0, eq), v = tok.Substring(eq + 1);
                            switch (k)
                            {
                                case "step": fStep = ParseL(v, 0); break;
                                case "kind": fk = v; break;
                                case "x": fx = ParseD(v, 0); break;
                                case "y": fy = ParseD(v, 0); break;
                                case "zoom": fz = ParseD(v, 0); break;
                                case "name": fnm = v; break;
                                case "uid": fUid = ParseL(v, 0); break;
                                case "name_uid": fNameUid = ParseL(v, 0); break;
                                case "label": flab = TestResultNote.Unesc(v); break;
                                case "scenario": fscen = TestResultNote.Unesc(v); break;
                                case "desc": fdesc = TestResultNote.Unesc(v); break;
                            }
                        }
                        _focus.Add(new FocusNote(fStep, fk, fx, fy, fz, fnm, fUid,
                                                 flab, fscen, fdesc, fNameUid));
                        continue;
                    }
                    if (line.StartsWith("# result "))
                    {
                        long rStep = 0; int rNum = 0, rXng = 0, rOff = 0; bool rPass = false;
                        double rSim = 0, rWall = 0;
                        string rLab = "", rScen = "", rWhy = "";
                        foreach (var tok in line.Substring(9).Split(' '))
                        {
                            var eq = tok.IndexOf('=');
                            if (eq <= 0) continue;
                            string k = tok.Substring(0, eq), v = tok.Substring(eq + 1);
                            switch (k)
                            {
                                case "step": rStep = ParseL(v, 0); break;
                                case "number": rNum = (int)ParseL(v, 0); break;
                                case "label": rLab = TestResultNote.Unesc(v); break;
                                case "scenario": rScen = TestResultNote.Unesc(v); break;
                                case "passed": rPass = v == "1"; break;
                                case "reason": rWhy = TestResultNote.Unesc(v); break;
                                case "sim": rSim = ParseD(v, 0); break;
                                case "wall": rWall = ParseD(v, 0); break;
                                case "xng": rXng = (int)ParseL(v, 0); break;
                                case "off": rOff = (int)ParseL(v, 0); break;
                            }
                        }
                        _results.Add(new TestResultNote(rStep, rNum, rLab, rScen, rPass,
                                                       rWhy, rSim, rWall, rXng, rOff));
                        continue;
                    }
                    if (line.Contains("dt_fixed="))
                    {
                        double parsed = ParseD(line.Split("dt_fixed=")[1].Split(" ")[0], 0.0);
                        if (parsed > 0.0) dt = parsed;
                    }
                    continue;
                }
                var semi = line.Split(";");
                if (semi.Length < 4) continue;
                var evs = semi.Length > 4 ? semi[4] : "";
                long step = ParseL(semi[0], -1L);
                if (step < 0) continue;
                var f = new TapeFrame { Step = step, Cars = new List<TapeCar>(),
                                        FluidityMpsPerCar = ParseD(semi[1], 0.0),
                                        MovingShare = ParseD(semi[2], 0.0) };
                foreach (var part in semi[3].Split("|"))
                {
                    var fld = part.Split(",");
                    if (fld.Length < 10) continue;
                    f.Cars.Add(new TapeCar {
                        Uid = ParseI(fld[0], -1), X = ParseD(fld[1], 0.0),
                        Y = ParseD(fld[2], 0.0), HeadingDeg = ParseD(fld[3], 0.0),
                        SpeedMps = ParseD(fld[4], 0.0), Blinkers = ParseI(fld[5], 0),
                        Brake = fld[6] == "1", WrongSide = fld[7] == "1",
                        CrashEpisodes = ParseI(fld[8], 0),
                        Color = fld.Length >= 11 ? fld[9] : "",
                        // FIVE field generations: ...,color,capKey (lamps
                        // absent), ...,color,lamps,capKey, ...,color,lamps,
                        // heldBy,capKey and ...,color,lamps,heldBy,level,
                        // capKey. Accept all of them rather than refusing
                        // the run — the reviewer reads tapes that were
                        // recorded before the field existed. capKey stays
                        // LAST in every generation, because the record is
                        // comma-separated and a cap key can carry that
                        // delimiter (an added reason is joined with ',')
                        // — which is exactly why level and heldBy went
                        // before it instead of after.
                        Lamps = fld.Length >= 12 ? ParseI(fld[10], 0) : 0,
                        HeldByUid = fld.Length >= 13 ? ParseI(fld[11], 0) : 0,
                        Level = hasLegend ? (headerDeclaresLevel && fld.Length >= 14 ? ParseI(fld[12], 0) : 0)
                                        : fld.Length >= 14 ? ParseI(fld[12], 0) : 0,
                        CapKey = (!hasLegend || headerDeclaresLevel)
                            ? (fld.Length >= 14 ? string.Join(",", fld, 13, fld.Length - 13)
                              : fld.Length >= 13 ? string.Join(",", fld, 12, fld.Length - 12)
                              : fld.Length >= 12 ? string.Join(",", fld, 11, fld.Length - 11)
                              : fld.Length >= 11 ? fld[10] : "")
                            : fld.Length >= 13 ? string.Join(",", fld, 12, fld.Length - 12)
                              : fld.Length >= 12 ? string.Join(",", fld, 11, fld.Length - 11)
                              : fld.Length >= 11 ? fld[10] : "",
                    });
                }
                if (evs.StartsWith("~"))
                    foreach (var ev in evs.Substring(1).Split("~"))
                    {
                        int eq = ev.IndexOf('=');
                        if (eq <= 0) continue;
                        f.Events.Add(new TapeEvent { Uid = ParseI(ev.Substring(0, eq), -1),
                                                     Msg = ev.Substring(eq + 1) });
                    }
                if (f.Cars.Count > 0) frames.Add(f);
            }
        }
        catch (Exception)
        {
            return false;
        }
        if (frames.Count == 0) return false;
        // A loaded tape has no live event markers, so derive the ones that
        // matter for scrubbing: a step where some car is held by a rule, or
        // where a crash episode count exists.
        var loadedEvents = new List<long>();
        foreach (var f in frames)
        {
            bool interesting = false;
            foreach (var cf in f.Cars)
                if (cf.CapKey != "" || cf.CrashEpisodes > 0) interesting = true;
            if (interesting) loadedEvents.Add(f.Step);
        }
        lock (Lock)
        {
            _recording = false;
            _mapName = mapName;
            DtFixedS = dt;
            _frames.Clear();
            foreach (var f in frames) _frames.Add(f);
            _events.Clear();
            foreach (var e in loadedEvents) _events.Add(e);
            _names.Clear();
            foreach (var kv in namesLoaded) _names[kv.Key] = kv.Value;
            _kinds.Clear();
            foreach (var kv in kindsLoaded) _kinds[kv.Key] = kv.Value;
            _carOpts.Clear();
            foreach (var kv in carOptsLoaded) _carOpts[kv.Key] = kv.Value;
            _carOptEvents.Clear();
            foreach (var ce in carOptEventsLoaded) _carOptEvents.Add(ce);
        }
        return true;
    }

    private static int ParseI(string s, int fallback)
    {
        var v = int.TryParse(s, out int r) ? r : fallback;
        return v;
    }

    private static double ParseD(string s, double fallback)
    {
        var v = double.TryParse(s, out double r) ? r : fallback;
        return v;
    }

    private static long ParseL(string s, long fallback)
    {
        var v = long.TryParse(s, out long r) ? r : fallback;
        return v;
    }

    public static bool Recording => _recording;
    public static int Count => _frames.Count;

    /// <summary>Start (or keep) recording. Idempotent: a scenario that stages
    /// several cars calls it several times and the tape stays one tape.</summary>
    public static void Start(string mapName, double dtFixedS, string mapHash = "")
    {
        lock (Lock)
        {
            _recording = true;
            _names.Clear();          // a new run is a new cast
            _carOpts.Clear();        // ...and a new set of view options
            _carOptEvents.Clear();   // ...and no changes yet
            _focus.Clear();          // ...and a new camera intent per scenario
            _results.Clear();
            _mapName = SanitiseMapName(mapName);
            _mapHash = mapHash ?? "";
            DtFixedS = dtFixedS > 0.0 ? dtFixedS : 1.0 / 60.0;
        }
    }

    /// <summary>A map name that can survive the header line. The header is
    /// space-separated `key=value` tokens and the reader takes the first token
    /// after `map=` - so a name holding a space does not fail, it LIES:
    /// issue #11 recorded `map=engine boot`, which read back as a map called
    /// 'engine', and the reviewer then refused to paint cars on a ground it
    /// could not build. An unusable name is therefore replaced - and said out
    /// loud - instead of being written and mis-read.</summary>
    static string SanitiseMapName(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return s;
        bool bad = false;
        foreach (char c in s)
            if (char.IsWhiteSpace(c) || c == '=' || c == ',') { bad = true; break; }
        if (!bad) return s;
        var buf = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            buf[i] = (char.IsWhiteSpace(c) || c == '=' || c == ',') ? '_' : c;
        }
        var clean = new string(buf);
        System.Console.WriteLine(
            $"TAPE-MAP-NAME '{s}' cannot stand in the header (space, ',' and '=' " +
            $"separate tokens) → '{clean}'");
        return clean;
    }

    public static void Stop()
    {
        lock (Lock) _recording = false;
    }

    public static void Clear()
    {
        lock (Lock)
        {
            _frames.Clear();
            _events.Clear();
            // The option changes belong to the run they were noted in. Leaving
            // them behind would give the NEXT tape another run's repaints - and
            // Clear() is what a fresh run calls, so the leak would be silent.
            _carOptEvents.Clear();
        }
    }

    /// <summary>Record one substep of every car, called at the substep END so
    /// the pose is the pose the car has AFTER the step - what the renderer drew.
    /// The cap key comes from `Car.ActiveCapKey`, the rule that owns this car's
    /// speed cap right now; carrying it is what makes a standstill explainable
    /// AT a frame instead of only after the fact.</summary>
    public static void Record(long step, List<Car> cars, Dictionary<int, bool> wrongSide,
                              Dictionary<int, string> names,
                              double fluidityMpsPerCar, double movingShare,
                              RoadNetwork network)
    {
        if (!_recording) return;
        var f = new TapeFrame { Step = step, Cars = new List<TapeCar>(),
                                FluidityMpsPerCar = fluidityMpsPerCar,
                                MovingShare = movingShare };
        foreach (var c in cars)
        {
            bool blinkL = false, blinkR = false;
            if (c.Driver is BicycleDriver drv)
            {
                blinkL = drv.BlinkerLeft; blinkR = drv.BlinkerRight;
            }
            int blinkers = (blinkL ? 1 : 0) | (blinkR ? 2 : 0);
            f.Cars.Add(new TapeCar {
                Uid = c.Uid, X = c.X, Y = c.Y, HeadingDeg = c.Heading,
                SpeedMps = c.Speed,
                // The deck, resolved the way /state resolves it
                // (`SimEngine.cs:1264`) - ONE source for the number, so the
                // two channels cannot drift about which bridge a car is on.
                Level = network is not null && c.SegIdx >= 0
                        && c.SegIdx < network.Segments.Count
                        ? network.Segments[c.SegIdx].Level : 0,
                Blinkers = blinkers, Brake = c.IsBraking,
                WrongSide = wrongSide.GetValueOrDefault(c.Uid, false),
                CrashEpisodes = c.CrashEpisodes,
                Color = c.Color ?? "",
                // The lamps are part of the picture: the renderer draws
                // white head dots and red tail dots, and a tape that does
                // not carry them cannot say what the lamps of the frame
                // under the cursor looked like.
                Lamps = (c.HeadlightsOn ? 1 : 0) | (c.TaillightsOn ? 2 : 0),
                // Who holds me, as a VALUE. The cap key says WHICH rule; a
                // reader that wants the actor has to be told, because
                // `r32#passive` names nobody and `sign#C#7` names the node
                // before the car (docs/CAR_DETAIL_WINDOW.md).
                HeldByUid = c.HeldByUid,
                CapKey = c.ActiveCapKey ?? "",
            });
            // A decision made ON this substep is recorded WITH its text - the
            // driver's own reasoning at that frame, sparse by construction.
            if (c.LastDecisionStep == step)
            {
                f.Events.Add(new TapeEvent { Uid = c.Uid, Msg = c.LastDecisionMsg });
                _events.Add(step);
            }
            // The cast's roles, collected as the tape grows and written once
            // into the header (see the field's doc).
            var nm = names.GetValueOrDefault(c.Uid, "");
            if (nm != "" && !_names.ContainsKey(c.Uid)) _names[c.Uid] = nm;
        }
        lock (Lock)
        {
            if (_frames.Count >= MaxSteps)
            {
                if (_frames.Count == MaxSteps)
                    Console.WriteLine($"⚠️  TAPE FULL at {MaxSteps} substeps - " +
                        "recording stops, the sim keeps running");
                return;
            }
            _frames.Add(f);
        }
    }

    /// <summary>The frame at a substep (-1 = the last one). Null if the tape
    /// does not reach that far - a caller must not invent one. Frames are
    /// appended in step order, so the index IS the offset.</summary>
    public static TapeFrame FrameAt(long step)
    {
        lock (Lock)
        {
            if (_frames.Count == 0) return null;
            if (step == -1) return _frames[^1];
            int i = (int)(step - _frames[0].Step);
            return i >= 0 && i < _frames.Count ? _frames[i] : null;
        }
    }

    /// <summary>One substep as ONE text line - the format the FILE writes and
    /// every reader accepts. Factored out of `Save` so the live endpoint
    /// (`POST /tape/tip`) ships the SAME bytes a tape file contains: one frame,
    /// one grammar, no second parser to drift (`docs/RECORD_PLAYBACK.md`).</summary>
    public static string FrameLine(TapeFrame f)
    {
        var parts = new List<string>(f.Cars.Count);
        foreach (var cf in f.Cars)
            parts.Add($"{cf.Uid},{cf.X:F3},{cf.Y:F3},{cf.HeadingDeg:F2}," +
                      $"{cf.SpeedMps:F3},{cf.Blinkers},{(cf.Brake ? 1 : 0)}," +
                      $"{(cf.WrongSide ? 1 : 0)},{cf.CrashEpisodes},{cf.Color}," +
                      $"{cf.Lamps},{cf.HeldByUid},{cf.Level},{cf.CapKey}");
        return $"{f.Step};{f.FluidityMpsPerCar:F4};{f.MovingShare:F3};" +
               string.Join("|", parts) +
               (f.Events.Count > 0
                   ? ";~" + string.Join("~",
                       f.Events.Select(e => e.Uid + "=" + e.Msg).ToList())
                   : "");
    }

    /// <summary>The tip a client reads: the next lines, where the source has got
    /// to, and the header facts a reader needs (dt, the legend, the map stamp).
    /// A C# shape so an EMBEDDED client reads it in-process; the http host
    /// serialises the SAME values (GameApi.cs: `POST /tape/tip`). The lines are
    /// the tape's own grammar - the client's one reader parses them either way
    /// (docs/RECORD_PLAYBACK.md -> "one frame, one grammar, one reader").</summary>
    public sealed class TipData
    {
        public long Head = -1;
        public long First = -1;        // absolute substep of the first line
        public List<string> Lines = new();
        public bool Recording;
        public double DtFixedS;
        public string Legend = "";
        public string MapName = "";
        public string MapStamp = "";
    }

    public static TipData GetTip(long since, int limit)
    {
        var d = new TipData { Head = -1, First = -1, Recording = _recording,
                              DtFixedS = DtFixedS, Legend = FrameLegend,
                              MapName = _mapName, MapStamp = _mapHash };
        lock (Lock)
        {
            long first = _frames.Count > 0 ? _frames[0].Step : -1;
            d.Head = _frames.Count > 0 ? _frames[^1].Step : -1;
            long from = since < 0 ? first : (since + 1 > first ? since + 1 : first);
            d.First = from;
            for (long st = from; st <= d.Head && d.Lines.Count < limit; st++)
            {
                int i = (int)(st - first);
                if (i >= 0 && i < _frames.Count)
                    d.Lines.Add(FrameLine(_frames[i]));
            }
        }
        return d;
    }

    /// <summary>The tip as JSON for the http host: the SAME values `GetTip`
    /// answers, so the wire and the in-process read cannot disagree.</summary>
    public static Dictionary<string, object?> TipPayload(long since, int limit)
    {
        var d = GetTip(since, limit);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["recording"] = d.Recording,
            ["first_step"] = d.First,
            ["head_step"] = d.Head,
            ["dt_fixed_s"] = d.DtFixedS,
            ["map"] = d.MapName,
            ["map_hash"] = d.MapStamp,
            ["legend"] = d.Legend,
            ["lines"] = d.Lines,
        };
    }

    /// <summary>Write the whole tape, one line per substep. Text on purpose:
    /// greppable, and tests/ already keeps per-run text logs beside it.
    /// Returns the path on success, null on a disk error.</summary>
    public static string Save(string path)
    {
        lock (Lock)
        {
            try
            {
                var lines = new List<string>(_frames.Count + 2);
                var nameParts = new List<string>();
                foreach (var kv in _names) nameParts.Add($"{kv.Key}:{kv.Value}");
                var kindParts = new List<string>();
                foreach (var kv in _kinds) kindParts.Add($"{kv.Key}:{kv.Value}");
                var carOptParts = new List<string>();
                foreach (var kv in _carOpts)
                {
                    var bits = new List<string>();
                    if (kv.Value.Detail is not null)
                        bits.Add($"detail={(kv.Value.Detail == true ? "on" : "off")}");
                    if (kv.Value.Tracks is not null)
                        bits.Add($"tracks={(kv.Value.Tracks == true ? "on" : "off")}");
                    if (!string.IsNullOrEmpty(kv.Value.Livery))
                        bits.Add($"livery={kv.Value.Livery!.Replace("#", "").ToLowerInvariant()}");
                    if (bits.Count > 0) carOptParts.Add($"{kv.Key}:{string.Join("+", bits)}");
                }
                lines.Add($"# DGTAP1 map={_mapName} map_hash={_mapHash} dt_fixed={DtFixedS:F6} steps={_frames.Count}" +
                          (nameParts.Count > 0 ? $" names={string.Join(",", nameParts)}" : "") +
                          (kindParts.Count > 0 ? $" kinds={string.Join(",", kindParts)}" : "") +
                          (carOptParts.Count > 0 ? $" caropts={string.Join(",", carOptParts)}" : ""));
                lines.Add(FrameLegend);
                foreach (var fn in _focus) lines.Add(fn.ToHeaderLine());
                foreach (var rn in _results) lines.Add(rn.ToHeaderLine());
                // Option CHANGES, after the staged declarations: the header says
                // how the cast was born, these say what it became (#10).
                foreach (var ce in _carOptEvents) lines.Add(ce.Line);
                foreach (var f in _frames)
                    lines.Add(FrameLine(f));
                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                return path;
            }
            catch (Exception e)
            {
                // Named, not swallowed: the path is sent by a CLIENT whose
                // working directory is not the server's, so a relative "path"
                // resolves somewhere the caller never meant (measured: a
                // tape save answered a bare ok:false and the run's tape was
                // simply gone, with nothing to grep). The API echoes the
                // absolute target; this says why it could not be written.
                Console.Error.WriteLine($"TAPE-SAVE failed for '{path}' " +
                                        $"(cwd {Directory.GetCurrentDirectory()}): " +
                                        $"{e.GetType().Name}: {e.Message}");
                return null;
            }
        }
    }

    public static Dictionary<string, object?> StatusPayload()
    {
        lock (Lock)
            return new Dictionary<string, object?>
            {
                ["recording"] = _recording,
                ["frames"] = _frames.Count,
                ["first_step"] = _frames.Count > 0 ? _frames[0].Step : -1,
                ["last_step"] = _frames.Count > 0 ? _frames[^1].Step : -1,
                ["dt_fixed_s"] = DtFixedS,
                ["map"] = _mapName,
                ["map_hash"] = _mapHash,
                ["events"] = _events.Count,
            };
    }

    public static List<long> EventSteps()
    {
        lock (Lock) return new List<long>(_events);
    }
}
