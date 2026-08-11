using System.Reflection;

namespace SuperBiMcp.Tests;

/// <summary>
/// Locates the committed test fixtures (small CSV / XLSX files under <c>fixtures/</c>) and hands out a
/// throwaway working directory per test. Fixtures are copied next to the test binary by the csproj, so the
/// runtime path is the assembly directory; the source path is used as a fallback when running from an IDE
/// that does not copy content.
/// </summary>
internal static class Fixtures
{
    /// <summary>Absolute path to a committed fixture file (e.g. "sales.csv", "multi.xlsx").</summary>
    public static string Path(string name)
    {
        string asmDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string p = System.IO.Path.Combine(asmDir, "fixtures", name);
        if (File.Exists(p)) return p;

        // fallback: walk up to the project's source "fixtures" folder (IDE run without content copy)
        var dir = new DirectoryInfo(asmDir);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string cand = System.IO.Path.Combine(dir.FullName, "fixtures", name);
            if (File.Exists(cand)) return cand;
        }
        throw new FileNotFoundException($"fixture not found: {name}");
    }

    /// <summary>Create and return a fresh, empty working directory under the system temp area. The caller
    /// owns it; <see cref="TempDir"/>'s Dispose cleans it up.</summary>
    public static TempDir NewWorkDir() => new();
}

/// <summary>A disposable temp directory that deletes itself (best effort) on Dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "superbi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best effort - a leaked temp dir is harmless */ }
    }
}
