// Test-run tag for forensic files (crash dumps, per-run suite logs).
//
// Every POST /run_test gets a fresh tag (T_001, T_002, ...) and the tag is
// reset to the idle value when the run ends. All artifacts of one run -
// the suite log (tests/run_test_T_042_...) and every crash dump written
// while the run is active (logs/T_042_crash_...) - share the prefix, so a
// crash dump can be attributed to its run without file-timestamp
// archaeology (the 2026-09-10 mix-up: unit-test dumps vs e2e dumps).
//
// The counter is persisted in logs/run_counter instead of living in
// process memory because the e2e runner (scripts/run_e2e.sh) kills and
// restarts the host on EVERY run - an in-process counter would always
// read T_001. In-process runs that have no server (unit tests) and manual
// drives stay on the idle tag unless they set one themselves.

namespace DrivingGame.Sim;

public static class RunTag
{
    /// <summary>Tag while no test run is active (unit tests, manual drives).</summary>
    public const string Idle = "T_000";

    /// <summary>Current run tag. Set/cleared by the server's /run_test
    /// lifecycle (under the test lock).</summary>
    public static string Current = Idle;

    /// <summary>Next run number, persisted in <c>logsDir/run_counter</code>
    /// so it survives host restarts. Called under the test lock - at most
    /// one run starts at a time, so no concurrent increments.</summary>
    public static int NextRunNumber(string logsDir)
    {
        string path = Path.Combine(logsDir, "run_counter");
        int n = 0;
        try
        {
            if (File.Exists(path) &&
                int.TryParse(File.ReadAllText(path).Trim(), out int old))
                n = old;
        }
        catch { /* corrupted/missing counter: start over */ }
        n++;
        try { File.WriteAllText(path, n.ToString()); }
        catch { /* best effort - the tag still works for this process */ }
        return n;
    }
}
