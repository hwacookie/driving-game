// Workaround for godotengine/godot#112701: Godot's managed host does NOT
// probe the .NET shared framework directories (<dotnet>/shared/...), so
// assemblies like Microsoft.AspNetCore.* fail to load even though the app's
// runtimeconfig declares the framework. (Observed here 2026-09-05, Phase 7:
// embedded REST API in the Godot app -> FileNotFoundException for
// Microsoft.AspNetCore.) This resolver probes the app output directory plus
// the installed shared frameworks and is injected at module init, before any
// ASP.NET type is touched. Source: workaround from godotengine/godot#112701,
// adapted (macOS roots + CoreLib-relative root discovery).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal static class SharedFrameworkAssemblyLoader
{
    private static readonly ConcurrentDictionary<string, string?> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ProbeDirs = BuildProbeDirs();

    [ModuleInitializer]
    internal static void InjectGodotAssemblyResolverFix()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var simple = name.Name;
            if (string.IsNullOrEmpty(simple)) return null;
            var file = simple + ".dll";

            // 1. App output directory (deps Godot's build didn't copy next
            //    to the game DLL).
            var local = Path.Combine(AppContext.BaseDirectory, file);
            if (File.Exists(local))
            {
                try { return AssemblyLoadContext.Default.LoadFromAssemblyPath(local); }
                catch (FileLoadException) { }
            }

            // 2. Installed shared frameworks (cached; negatives too).
            if (Cache.TryGetValue(simple, out var cached))
                return cached is null ? null : TryLoad(cached);
            var found = Locate(file);
            Cache[simple] = found;
            return found is null ? null : TryLoad(found);
        };
    }

    private static Assembly? TryLoad(string path)
    {
        try { return AssemblyLoadContext.Default.LoadFromAssemblyPath(path); }
        catch (FileLoadException) { return null; }
    }

    private static string? Locate(string file)
    {
        foreach (var dir in ProbeDirs)
            if (File.Exists(Path.Combine(dir, file)))
                return Path.Combine(dir, file);
        return null;
    }

    private static string[] BuildProbeDirs()
    {
        var roots = new List<string>();

        // Most reliable root: where the runtime itself was loaded from -
        // CoreLib lives in <shared>/Microsoft.NETCore.App/<version>/, so two
        // levels up is the shared root. Works for any install location.
        try
        {
            var corelibDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (corelibDir != null)
                roots.Add(Directory.GetParent(
                    Directory.GetParent(corelibDir)!.FullName)!.FullName);
        }
        catch { /* fall through to the standard locations */ }

        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
            roots.Add(Path.Combine(dotnetRoot, "shared"));

        foreach (var candidate in new[]
        {
            "/usr/local/share/dotnet/shared",           // macOS (this machine)
            "/opt/homebrew/opt/dotnet/libexec/shared",  // Homebrew dotnet
            "/usr/lib/dotnet/shared",                   // Linux distro
        })
            roots.Add(candidate);

        // Shared frameworks live at <root>/<FrameworkName>/<Version>/, so
        // expand each root into its concrete framework/version directories.
        var dirs = new List<string>();
        foreach (var root in roots.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var fw in SafeDirs(root))
                foreach (var ver in SafeDirs(fw))
                    dirs.Add(ver);
        }
        return dirs.ToArray();
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }
}
