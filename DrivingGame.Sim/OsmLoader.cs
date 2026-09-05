// OSM data loader — cache-file path of car/src/osm_loader.py.
// The JSON cache (data/osm_cache/) is the primary source: after the first
// DB fetch the game runs without any database, so a stopped Postgres does
// not block it. The Npgsql refresh path is optional/later (see spec).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DrivingGame.Sim;

public static class OsmLoader
{
    /// <summary>Cache key: SHA1("{north:.7f}_{south:.7f}_{west:.7f}_{east:.7f}")[:16]
    /// (verified: matches the existing filename).</summary>
    public static string CacheKey(double north, double south, double west, double east)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string key = $"{north.ToString("F7", inv)}_{south.ToString("F7", inv)}" +
                     $"_{west.ToString("F7", inv)}_{east.ToString("F7", inv)}";
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();   // first 16 hex chars
    }

    /// <summary>Default cache directory: &lt;repo root&gt;/data/osm_cache (walk up
    /// from the app base dir to the project.godot marker, like ObstacleManager).</summary>
    public static string DefaultCacheDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot")))
                return Path.Combine(dir.FullName, "data", "osm_cache");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "data", "osm_cache");
    }

    /// <summary>Load cached OSM data for the bbox, or null if absent/empty.
    /// Honors OSM_FORCE_REFRESH=1 (skip the cache, like the Python loader).</summary>
    public static OsmData? LoadFromCache(double north, double south, double west, double east,
                                         string? cacheDir = null)
    {
        if (Environment.GetEnvironmentVariable("OSM_FORCE_REFRESH") == "1")
            return null;
        string dir = cacheDir ?? DefaultCacheDir();
        string path = Path.Combine(dir, $"osm_{CacheKey(north, south, west, east)}.json");
        if (!File.Exists(path)) return null;

        JsonElement root;
        try
        {
            using var fs = File.OpenRead(path);
            root = JsonSerializer.Deserialize<JsonElement>(fs);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  (cache unreadable, ignoring: {e.Message})");
            return null;
        }

        var data = new OsmData();
        if (root.TryGetProperty("nodes", out var nodesEl) && nodesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in nodesEl.EnumerateObject())
            {
                double lat = kv.Value.GetProperty("lat").GetDouble();
                double lon = kv.Value.GetProperty("lon").GetDouble();
                data.Nodes[kv.Name] = (lat, lon);
            }
        }
        if (root.TryGetProperty("ways", out var waysEl) && waysEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in waysEl.EnumerateArray())
            {
                var nodeIds = new List<string>();
                foreach (var n in w.GetProperty("nodes").EnumerateArray())
                    nodeIds.Add(n.GetString()!);
                data.Ways.Add(new OsmWay(
                    w.GetProperty("id").GetInt32(),
                    nodeIds,
                    w.GetProperty("highway").GetString() ?? "",
                    w.TryGetProperty("oneway", out var ow) && ow.GetBoolean()));
            }
        }

        if (data.Ways.Count == 0) return null;
        Console.WriteLine($"  Using cached OSM data: {path} ({data.Ways.Count} segments, no database needed)");
        return data;
    }
}
