using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp;

/// <summary>
/// Bulk fan-out: apply one build/lint/refresh across many .pbix files with a per-file result table.
///
/// Two surfaces:
///   - batch build: `SuperBiMcp build` with a batch manifest ("sources" array and/or "glob" string plus
///     "outputDir"/"suffix"/"inPlace"), or the convenience form
///     `SuperBiMcp build --glob "D:\reports\*.pbix" [--outputDir &lt;dir&gt;] [--suffix &lt;sfx&gt;] [--recipe &lt;name&gt;]`.
///     Pure-file: each file goes through <see cref="Cli.Build"/> with its own ReportService, so files run
///     up to four in parallel. A PBIR pbix or a Desktop-locked file is a per-file failure, never an abort.
///   - bulk refresh: `SuperBiMcp refresh &lt;path-or-glob&gt; [more...] [--timeout &lt;sec&gt;] [--save-retries &lt;n&gt;]
///     [--csv &lt;log.csv&gt;] [--bpa] [--pbix-exe &lt;exe&gt;]` - opens each .pbix in Power BI
///     Desktop, refreshes the model through Desktop's own engine via TOM, saves with Ctrl+S and closes.
///     One file at a time (the save step owns the foreground). This absorbs Refresh-PbixReports.ps1.
///
/// The small helpers (glob resolution, output derivation, manifest synthesis, aggregation) are pure and
/// unit-tested offline; the refresh loop's process/window interop is Windows-only.
/// </summary>
public static class BulkOps
{
    // ============================ pure helpers (unit-tested offline) ============================

    /// <summary>A manifest is a batch when it fans out over "sources"/"glob" instead of one "source".</summary>
    public static bool IsBatchManifest(JsonObject manifest) =>
        manifest["sources"] is JsonArray || manifest["glob"] is JsonValue;

    /// <summary>
    /// Expand a mix of literal paths, directories and simple globs into a deduped, sorted list.
    /// Globs are non-recursive (a directory means every *.pbix directly inside, like the ps1's -Folder);
    /// a glob over a missing directory contributes nothing; literal paths pass through unchecked so a
    /// missing file surfaces later as a per-file failure, not a batch abort.
    /// </summary>
    public static List<string> ResolveInputs(IEnumerable<string> patterns)
    {
        var files = new List<string>();
        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string p = raw.Trim();
            if (p.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                string dir = Path.GetDirectoryName(p) is { Length: > 0 } d ? d : ".";
                string mask = Path.GetFileName(p);
                if (Directory.Exists(dir))
                    files.AddRange(Directory.EnumerateFiles(dir, mask, SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase)));
            }
            else if (Directory.Exists(p))
            {
                files.AddRange(Directory.EnumerateFiles(p, "*.pbix", SearchOption.TopDirectoryOnly));
            }
            else
            {
                files.Add(p);
            }
        }
        return files
            .Select(f => Path.GetFullPath(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Derive the per-file output path: outputDir -> same filename inside it; suffix -> beside the source
    /// as &lt;name&gt;&lt;suffix&gt;.pbix (both -> suffixed name inside outputDir). Neither present is a hard error
    /// unless inPlace is explicitly true - the single-file default of output=source would silently clobber
    /// every source in a batch, so batch in-place editing is opt-in only.
    /// </summary>
    public static string DeriveOutput(string source, string? outputDir, string? suffix, bool inPlace)
    {
        bool hasDir = !string.IsNullOrWhiteSpace(outputDir);
        bool hasSuffix = !string.IsNullOrWhiteSpace(suffix);
        if (!hasDir && !hasSuffix)
        {
            if (!inPlace)
                throw new InvalidOperationException(
                    "batch build needs \"outputDir\" or \"suffix\" to derive per-file outputs (or explicit \"inPlace\": true to edit the sources in place).");
            return source;
        }
        string name = Path.GetFileNameWithoutExtension(source) + (hasSuffix ? suffix : "");
        string dir = hasDir ? outputDir! : Path.GetDirectoryName(Path.GetFullPath(source))!;
        return Path.Combine(dir, name + ".pbix");
    }

    /// <summary>Synthesize the single-file manifest <see cref="Cli.Build"/> understands, carrying the
    /// shared recipe fields verbatim (deep-cloned - JsonNodes cannot be re-parented).</summary>
    public static JsonObject SynthesizeFileManifest(JsonObject batch, string source, string output)
    {
        var m = new JsonObject { ["source"] = source, ["output"] = output };
        foreach (var key in new[] { "recipe", "config", "recipes", "verify" })
            if (batch[key] is JsonNode n)
                m[key] = n.DeepClone();
        return m;
    }

    /// <summary>Synthesize a batch manifest from the `build --glob ...` convenience flags.</summary>
    public static bool TryParseBuildArgs(string[] args, out JsonObject manifest, out string error)
    {
        manifest = new JsonObject();
        error = "";
        var globs = new JsonArray();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a.ToLowerInvariant())
            {
                case "--glob":
                    if (string.IsNullOrWhiteSpace(next)) { error = "--glob needs a path or glob pattern"; return false; }
                    globs.Add((JsonNode)next!); i++; break;
                case "--outputdir":
                    if (string.IsNullOrWhiteSpace(next)) { error = "--outputDir needs a directory"; return false; }
                    manifest["outputDir"] = next; i++; break;
                case "--suffix":
                    if (string.IsNullOrWhiteSpace(next)) { error = "--suffix needs a value"; return false; }
                    manifest["suffix"] = next; i++; break;
                case "--recipe":
                    if (string.IsNullOrWhiteSpace(next)) { error = "--recipe needs a name"; return false; }
                    manifest["recipe"] = next; i++; break;
                default:
                    error = $"unknown option '{a}'";
                    return false;
            }
        }
        if (globs.Count == 0) { error = "--glob is required"; return false; }
        manifest["sources"] = globs;
        return true;
    }

    public sealed record BuildFileResult(string File, bool Ok, string? Output, int? Sections, string? Error);

    /// <summary>Aggregate per-file build results into the stdout JSON shape.</summary>
    public static JsonObject SummarizeBuild(IReadOnlyList<BuildFileResult> results)
    {
        int ok = results.Count(r => r.Ok);
        var arr = new JsonArray();
        foreach (var r in results)
        {
            var o = new JsonObject { ["file"] = r.File, ["ok"] = r.Ok };
            if (r.Output is not null) o["output"] = r.Output;
            if (r.Sections is not null) o["sections"] = r.Sections;
            if (r.Error is not null) o["error"] = r.Error;
            arr.Add(o);
        }
        return new JsonObject
        {
            ["ok"] = ok == results.Count,
            ["total"] = results.Count,
            ["succeeded"] = ok,
            ["failed"] = results.Count - ok,
            ["results"] = arr,
        };
    }

    /// <summary>0 = all files built, 1 = any per-file failure. Never the failure count - that would
    /// collide with the reserved usage (2) and licence (3) exit codes.</summary>
    public static int BuildExitCode(IReadOnlyList<BuildFileResult> results) =>
        results.All(r => r.Ok) ? 0 : 1;

    /// <summary>Fixed-width per-file table (the stderr companion to the JSON on stdout).</summary>
    public static string FormatBuildTable(IReadOnlyList<BuildFileResult> results)
    {
        var rows = new List<string[]> { new[] { "File", "Ok", "Sections", "Output / Error" } };
        foreach (var r in results)
            rows.Add(new[]
            {
                r.File,
                r.Ok ? "ok" : "FAIL",
                r.Sections?.ToString() ?? "-",
                r.Ok ? r.Output ?? "" : r.Error ?? "",
            });
        return FormatTable(rows);
    }

    private static string FormatTable(List<string[]> rows)
    {
        int cols = rows[0].Length;
        var w = new int[cols];
        foreach (var row in rows)
            for (int c = 0; c < cols; c++)
                w[c] = Math.Max(w[c], row[c].Length);
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            for (int c = 0; c < cols; c++)
                sb.Append(c < cols - 1 ? rows[i][c].PadRight(w[c] + 2) : rows[i][c]);
            sb.AppendLine();
            if (i == 0)
            {
                for (int c = 0; c < cols; c++)
                    sb.Append(c < cols - 1 ? new string('-', w[c]).PadRight(w[c] + 2) : new string('-', w[c]));
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ============================ batch build (pure-file, parallel) ============================

    /// <summary>
    /// Run a batch manifest: fan <see cref="Cli.Build"/> out over the resolved files, up to four in
    /// parallel (each call owns its own ReportService, so files are independent). The licence was already
    /// checked once in <see cref="Cli.Run"/>; Build itself stays ungated. Prints the per-file table to
    /// stderr and the JSON summary to stdout; returns the process exit code.
    /// </summary>
    public static int RunBatchBuild(JsonObject manifest)
    {
        var patterns = new List<string>();
        if ((string?)manifest["glob"] is { Length: > 0 } g) patterns.Add(g);
        if (manifest["sources"] is JsonArray srcs)
            foreach (var n in srcs)
                if ((string?)n is { Length: > 0 } s)
                    patterns.Add(s);

        var files = ResolveInputs(patterns);
        if (files.Count == 0) throw new InvalidOperationException("no .pbix files matched \"sources\"/\"glob\".");

        string? outputDir = (string?)manifest["outputDir"];
        string? suffix = (string?)manifest["suffix"];
        bool inPlace = (bool?)manifest["inPlace"] ?? false;
        DeriveOutput(files[0], outputDir, suffix, inPlace); // fail fast on a manifest with no output rule

        var results = new BuildFileResult[files.Count];
        Parallel.ForEach(
            files.Select((file, idx) => (file, idx)),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
            item =>
            {
                try
                {
                    string output = DeriveOutput(item.file, outputDir, suffix, inPlace);
                    var built = JsonSerializer.SerializeToNode(Cli.Build(SynthesizeFileManifest(manifest, item.file, output))) as JsonObject;
                    results[item.idx] = new BuildFileResult(item.file, true, (string?)built?["output"] ?? output, (int?)built?["sections"], null);
                }
                catch (Exception ex)
                {
                    // one bad file (PBIR format, locked in Desktop, missing) never aborts the batch
                    results[item.idx] = new BuildFileResult(item.file, false, null, null, ex.Message);
                }
            });

        Console.Error.WriteLine(FormatBuildTable(results));
        Console.WriteLine(SummarizeBuild(results).ToJsonString(Cli.Pretty));
        return BuildExitCode(results);
    }

    // ============================ bulk refresh (live Desktop loop, serialised) ============================

    public sealed class RefreshResult
    {
        public string File = "";
        public string Status = "?"; // OK | Skipped | RefreshedButSaveUnconfirmed | Failed
        public double RefreshSec;
        public bool SaveDispatched;
        public int Tables;
        public string Error = "";
        public int? BpaFindings;
        public string? BpaTopSeverities;
    }

    /// <summary>`SuperBiMcp refresh` entry point (dispatched from Program.cs before the MCP host).</summary>
    public static int RunRefresh(string[] args)
    {
        try
        {
            var paths = new List<string>();
            int timeoutSec = 180, saveRetries = 3;
            string? csv = null, pbixExe = null;
            bool bpa = false;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                string? next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a.ToLowerInvariant())
                {
                    case "--timeout":
                        if (!int.TryParse(next, out timeoutSec) || timeoutSec <= 0) return RefreshUsage("--timeout needs a positive number of seconds");
                        i++; break;
                    case "--save-retries":
                        if (!int.TryParse(next, out saveRetries) || saveRetries <= 0) return RefreshUsage("--save-retries needs a positive count");
                        i++; break;
                    case "--csv":
                        if (string.IsNullOrWhiteSpace(next)) return RefreshUsage("--csv needs a file path");
                        csv = next; i++; break;
                    case "--bpa":
                        bpa = true; break;
                    case "--pbix-exe":
                        if (string.IsNullOrWhiteSpace(next)) return RefreshUsage("--pbix-exe needs a path to PBIDesktop.exe");
                        pbixExe = next; i++; break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) return RefreshUsage($"unknown option '{a}'");
                        paths.Add(a); break;
                }
            }
            if (paths.Count == 0) return RefreshUsage(null);

            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("ERROR: refresh drives Power BI Desktop and only runs on Windows.");
                return 1;
            }

            string exe = DesktopInterop.ResolvePbixExe(pbixExe);
            if (!File.Exists(exe)) { Console.Error.WriteLine($"ERROR: PBIDesktop.exe not found: {exe}"); return 1; }

            var files = ResolveInputs(paths);
            if (files.Count == 0) { Console.Error.WriteLine("ERROR: no .pbix files matched."); return 1; }

            Log($"Refreshing {files.Count} report(s), one Desktop instance at a time.");
            DesktopInterop.Log = Log;   // the shared interop's progress lines join this run's stderr trail
            var results = new List<RefreshResult>();
            foreach (var f in files) // strictly serial: the Ctrl+S save step must own the foreground
                results.Add(RefreshOne(f, exe, timeoutSec, saveRetries, bpa));

            Console.Error.WriteLine(FormatRefreshTable(results));
            if (csv is { Length: > 0 }) WriteRefreshCsv(csv, results);
            Console.WriteLine(SummarizeRefresh(results).ToJsonString(Cli.Pretty));
            return results.All(r => r.Status is "OK" or "Skipped") ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int RefreshUsage(string? error)
    {
        if (error is not null) Console.Error.WriteLine("ERROR: " + error);
        Console.Error.WriteLine("usage: SuperBiMcp refresh <path-or-glob> [more paths...] [--timeout <sec>] [--save-retries <n>] [--csv <log.csv>] [--bpa] [--pbix-exe <PBIDesktop.exe>]");
        return 2;
    }

    /// <summary>
    /// Refresh one .pbix through its own Power BI Desktop instance - the Refresh-PbixReports.ps1 worker:
    /// DesktopSession launches Desktop and resolves its private engine, TOM RequestRefresh(Full)+SaveChanges,
    /// then release the engine, dispatch File&gt;Save (foreground + Ctrl+S) and close.
    /// </summary>
    private static RefreshResult RefreshOne(string pbix, string exe, int timeoutSec, int saveRetries, bool bpa)
    {
        var r = new RefreshResult { File = pbix };
        if (!File.Exists(pbix)) { r.Status = "Failed"; r.Error = "file not found"; return r; }
        DesktopSession? d = null;
        TOM.Server? srv = null;
        try
        {
            Log("OPEN  " + pbix);
            d = DesktopSession.Launch(pbix, exe, timeoutSec, Log);
            srv = d.Connect();
            var db = d.AwaitModel(srv, d.DeadlineUtc);

            // short grace for table metadata to populate; a model with 0 tables = nothing to refresh (skip, don't hang)
            for (int g = 0; g < 15 && db.Model.Tables.Count == 0; g++)
            {
                Thread.Sleep(1000);
                try { srv.Refresh(); } catch { }
                db = srv.Databases[0];
            }
            r.Tables = db.Model.Tables.Count;
            if (r.Tables == 0)
            {
                r.Status = "Skipped";
                r.Error = "no tables / nothing to refresh";
                Log("  SKIPPED (no local tables to refresh)");
                return r;   // the finally below still tears the Desktop down
            }

            // engine refresh - real pass/fail surfaces as a TOM exception (bad source path, creds, etc.)
            Log($"REFRESH ({r.Tables} tables) ...");
            var sw = Stopwatch.StartNew();
            db.Model.RequestRefresh(TOM.RefreshType.Full);
            db.Model.SaveChanges(); // blocks until the refresh actually completes
            sw.Stop();
            r.RefreshSec = Math.Round(sw.Elapsed.TotalSeconds, 1);
            Log($"  refreshed in {r.RefreshSec}s");

            if (bpa)
            {
                // "lint a folder": run the BPA catalogue over the live, freshly refreshed model
                var findings = Services.BpaRules.Run(db.Model);
                r.BpaFindings = findings.Count;
                r.BpaTopSeverities = string.Join(" ", findings
                    .GroupBy(f => f.Severity)
                    .OrderByDescending(gr => gr.Key)
                    .Take(3)
                    .Select(gr => $"sev{gr.Key}:{gr.Count()}"));
                Log($"  BPA: {r.BpaFindings} finding(s) {r.BpaTopSeverities}");
            }

            // release the engine BEFORE the save - Desktop will not write the .pbix while TOM holds it
            d.DisconnectEngine();
            srv = null;

            // persist into the .pbix (the external refresh dirties the document; Ctrl+S saves it)
            r.SaveDispatched = d.SaveDispatch(saveRetries);
            r.Status = r.SaveDispatched ? "OK" : "RefreshedButSaveUnconfirmed";
            Log("  " + r.Status);
        }
        catch (SplashHangException ex)
        {
            r.Status = "Failed";
            r.Error = $"splash-hang ({ex.Kind}): {ex.Message}";
            Log("  FAILED: " + r.Error);
        }
        catch (Exception ex)
        {
            r.Status = "Failed";
            r.Error = ex.Message + (ex.InnerException is not null ? " || " + ex.InnerException.Message : "");
            Log("  FAILED: " + r.Error);
        }
        finally
        {
            try { srv?.Disconnect(); } catch { }
            d?.CloseDesktop();
        }
        return r;
    }

    /// <summary>Aggregate refresh results into the stdout JSON shape.</summary>
    public static JsonObject SummarizeRefresh(IReadOnlyList<RefreshResult> results)
    {
        int ok = results.Count(r => r.Status == "OK");
        int skipped = results.Count(r => r.Status == "Skipped");
        var arr = new JsonArray();
        foreach (var r in results)
        {
            var o = new JsonObject
            {
                ["file"] = r.File,
                ["status"] = r.Status,
                ["refreshSec"] = r.RefreshSec,
                ["saveDispatched"] = r.SaveDispatched,
                ["tables"] = r.Tables,
                ["error"] = r.Error.Length > 0 ? r.Error : null,
            };
            if (r.BpaFindings is int n)
            {
                o["bpaFindings"] = n;
                o["bpaTopSeverities"] = r.BpaTopSeverities;
            }
            arr.Add(o);
        }
        return new JsonObject
        {
            ["ok"] = ok + skipped == results.Count,
            ["total"] = results.Count,
            ["succeeded"] = ok,
            ["skipped"] = skipped,
            ["failed"] = results.Count - ok - skipped,
            ["results"] = arr,
        };
    }

    private static string FormatRefreshTable(IReadOnlyList<RefreshResult> results)
    {
        var rows = new List<string[]> { new[] { "File", "Status", "RefreshSec", "SaveDispatched", "Tables", "Bpa", "Error" } };
        foreach (var r in results)
            rows.Add(new[]
            {
                r.File,
                r.Status,
                r.RefreshSec.ToString(CultureInfo.InvariantCulture),
                r.SaveDispatched ? "yes" : "no",
                r.Tables.ToString(),
                r.BpaFindings is int n ? $"{n} {r.BpaTopSeverities}".TrimEnd() : "-",
                r.Error,
            });
        return FormatTable(rows);
    }

    private static void WriteRefreshCsv(string path, IReadOnlyList<RefreshResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("File,Status,RefreshSec,SaveDispatched,Tables,BpaFindings,BpaTopSeverities,Error");
        foreach (var r in results)
            sb.AppendLine(string.Join(",",
                CsvField(r.File),
                r.Status,
                r.RefreshSec.ToString(CultureInfo.InvariantCulture),
                r.SaveDispatched.ToString(),
                r.Tables.ToString(),
                r.BpaFindings?.ToString() ?? "",
                CsvField(r.BpaTopSeverities ?? ""),
                CsvField(r.Error)));
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Console.Error.WriteLine("Log: " + path);
    }

    private static string CsvField(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static void Log(string m) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
}
