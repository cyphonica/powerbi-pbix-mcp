using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;

namespace SuperBiMcp;

/// <summary>
/// Headless batch mode - the product entry point. One command, one declarative manifest, a finished
/// premium report. No agent, no MCP round-trips, no Power BI Desktop: it patches the report layer of an
/// existing .pbix (which already carries the model) directly, via the shared <see cref="Build"/> pipeline.
///
///   SuperBiMcp build job.json
///
/// job.json:
///   {
///     "source":  "C:\\path\\Client.pbix",          // pbix with the model (CLOSED in Desktop)
///     "output":  "C:\\path\\Client-report.pbix",    // optional; defaults to editing source in place
///     "recipe":  "category",                         // executive | category | crossretailer
///     "config":  { ... recipe config mapping fields to the model ... },
///     "recipes": [ { "recipe": "...", "config": {...} }, ... ]  // optional: multiple sections (clearPages:false)
///   }
///
/// Batch fan-out (see <see cref="BulkOps"/>): replace "source" with "sources" (array of paths/globs) or
/// "glob" (string) plus "outputDir"/"suffix" (or explicit "inPlace": true) to build many files in one run,
/// or skip the manifest entirely:
///
///   SuperBiMcp build --glob "D:\reports\*.pbix" [--outputDir &lt;dir&gt;] [--suffix &lt;sfx&gt;] [--recipe &lt;name&gt;]
/// </summary>
public static class Cli
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.Error.WriteLine("usage: SuperBiMcp build <manifest.json>");
                Console.Error.WriteLine("       SuperBiMcp build --glob \"D:\\reports\\*.pbix\" [--outputDir <dir>] [--suffix <sfx>] [--recipe <name>]");
                return 2;
            }
            JsonObject manifest;
            if (args[1].StartsWith("--", StringComparison.Ordinal))
            {
                // convenience form: synthesize a batch manifest straight from the flags (no job.json on disk)
                if (!BulkOps.TryParseBuildArgs(args, out manifest, out string argError))
                {
                    Console.Error.WriteLine(argError);
                    Console.Error.WriteLine("usage: SuperBiMcp build --glob \"D:\\reports\\*.pbix\" [--outputDir <dir>] [--suffix <sfx>] [--recipe <name>]");
                    return 2;
                }
            }
            else
            {
                if (!File.Exists(args[1])) { Console.Error.WriteLine($"manifest not found: {args[1]}"); return 2; }
                manifest = JsonNode.Parse(File.ReadAllText(args[1])) as JsonObject
                    ?? throw new InvalidOperationException("manifest is not a JSON object.");
            }
            if (BulkOps.IsBatchManifest(manifest))
                return BulkOps.RunBatchBuild(manifest);
            Console.WriteLine(JsonSerializer.Serialize(Build(manifest), Pretty));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Run a manifest end to end and return the result. Shared by the CLI and the HTTP service.</summary>
    public static object Build(JsonObject manifest)
    {
        string source = (string?)manifest["source"] ?? throw new InvalidOperationException("manifest needs a \"source\" .pbix path.");
        if (!File.Exists(source)) throw new FileNotFoundException($"source pbix not found: {source}");
        string output = (string?)manifest["output"] ?? source;

        var jobs = new List<(string recipe, JsonObject config)>();
        if (manifest["recipes"] is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject o) jobs.Add(((string?)o["recipe"] ?? "category", o["config"] as JsonObject ?? new JsonObject()));
        if (jobs.Count == 0)
            jobs.Add(((string?)manifest["recipe"] ?? "category", manifest["config"] as JsonObject ?? new JsonObject()));

        var report = new ReportService(new SessionStore(), NullLogger<ReportService>.Instance);

        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.Copy(source, output, overwrite: true);
        }

        var opened = report.Open(output);
        string sid = (string)opened.GetType().GetProperty("reportSessionId")!.GetValue(opened)!;

        var results = new List<object>();
        foreach (var (recipe, config) in jobs)
        {
            string cfgJson = config.ToJsonString();
            object r = recipe.ToLowerInvariant() switch
            {
                "executive" => report.BuildExecutiveReport(sid, cfgJson),
                "category" => report.BuildCategoryReport(sid, cfgJson),
                "crossretailer" or "cross-retailer" => report.BuildCrossRetailerCompare(sid, cfgJson),
                "grid" or "dashboard" => report.BuildGridReport(sid, cfgJson),
                _ => throw new InvalidOperationException($"unknown recipe '{recipe}' (use executive | category | crossretailer | grid)."),
            };
            results.Add(new { recipe, result = r });
        }
        report.Save(sid);

        bool wantVerify = (bool?)manifest["verify"] ?? false;
        return new
        {
            ok = true,
            output,
            sections = results.Count,
            results,
            verifyNote = wantVerify
                ? "verify requested: open the .pbix in Power BI Desktop and run audit_robustness / find_attribution_gaps against the live model (the reliability gate needs the live engine; it can't run headless yet)."
                : null,
            note = "Report generated headless (no Desktop). Open in Power BI Desktop to view; run the reliability gate against the live model before delivery.",
        };
    }
}
