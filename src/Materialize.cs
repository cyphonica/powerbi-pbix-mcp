using System.Diagnostics;
using System.Text.Json;

namespace SuperBiMcp;

/// <summary>
/// The Solution-starter materialiser - the bake worker's Route A driver (Power BI Desktop on the build host).
/// It turns a Solution's model spec + sample data into the starter .pbix an agent then opens and builds
/// on. Two halves:
///   1. SCAFFOLD (headless, always runs): inject the sample data folder into the model spec and emit a PBIP.
///   2. BAKE (needs Desktop): produce a populated .pbix from that PBIP. Power BI Desktop has no supported
///      headless "refresh + save .pbix", so the actual save is a pluggable command (SUPERBI_PBIX_SAVER) the
///      operator points at their Desktop-automation / pbi-tools step. With no saver configured it stops at
///      the PBIP and prints exactly how to finish it by hand - it never fakes a starter.
///
///   SuperBiMcp materialize &lt;solutionId|all&gt; [dataFolderOverride]
///
/// env SUPERBI_PBIX_SAVER - a command template that turns a refreshed PBIP into a populated .pbix.
///   Tokens {pbip} (the .pbip path) and {out} (the target starter .pbix) are substituted. Examples:
///     pbi-tools.exe compile "{pbip}" -outPath "{out}" -format PBIX -overwrite
///     powershell -File C:\tools\save-pbix.ps1 -Pbip "{pbip}" -Out "{out}"
/// </summary>
public static class Materialize
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            { Console.Error.WriteLine("usage: SuperBiMcp materialize <solutionId|all> [dataFolderOverride]"); return 2; }

            string? dataOverride = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null;
            var targets = string.Equals(args[1], "all", StringComparison.OrdinalIgnoreCase)
                ? SolutionLibrary.All.ToList()
                : new List<SolutionLibrary.SolutionInfo> { SolutionLibrary.Find(args[1]) ?? throw new InvalidOperationException($"unknown solution '{args[1]}'") };
            if (targets.Count == 0) { Console.Error.WriteLine("no solutions found (set SUPERBI_SOLUTIONS_DIR)."); return 2; }

            var results = targets.Select(s => MaterialiseOne(s, dataOverride)).ToList();
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, materialised = results.Count(r => r.done), results }, Cli.Pretty));
            return results.All(r => r.done) ? 0 : 0; // partial materialisation is not a hard failure
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
    }

    public sealed record Outcome(string id, bool done, string? starter, string? pbip, string note);

    public static Outcome MaterialiseOne(SolutionLibrary.SolutionInfo sol, string? dataFolderOverride)
    {
        if (sol.ModelSpecPath == null || !File.Exists(sol.ModelSpecPath))
            return new Outcome(sol.Id, false, null, null, "no modelSpec on this Solution.");

        // 1. SCAFFOLD - inject the data folder (the sample dir, or an override of the customer's data) into the spec
        string dataFolder = dataFolderOverride
            ?? (sol.SampleData.Count > 0 ? Path.GetDirectoryName(sol.SampleData[0])! : Path.Combine(sol.Dir, "sample"));
        string spec = File.ReadAllText(sol.ModelSpecPath).Replace("{{DATA_FOLDER}}", dataFolder.Replace('\\', '/'));

        // stage per invocation: concurrent runs of the SAME solution must never share a work dir, so the
        // safe id carries a fresh suffix and cleanup only ever targets this exact dir
        string work = Path.Combine(Path.GetTempPath(), "daxops-materialise",
            SafeName(sol.Id) + "-" + Guid.NewGuid().ToString("N")[..8]);
        string pbipDir = Path.Combine(work, "project");
        try { Headless.GenerateProject(spec, pbipDir, "Model"); }
        catch { TryWipeOwnStaging(work); throw; }
        string pbip = Path.Combine(pbipDir, "Model.pbip");

        string starter = sol.StarterPath ?? Path.Combine(sol.Dir, "starter.pbix");

        // 2. BAKE - hand the PBIP to the configured saver, or stop honestly at the PBIP
        string? saver = Environment.GetEnvironmentVariable("SUPERBI_PBIX_SAVER");
        if (string.IsNullOrWhiteSpace(saver))
            return new Outcome(sol.Id, false, null, pbip,
                $"PBIP scaffolded with sample data. To finish the starter: open it in Power BI Desktop on the VM, Refresh, and Save As \"{starter}\" (or set SUPERBI_PBIX_SAVER to automate). Then it serves as a Solution starter.");

        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        bool ok = RunSaver(saver, pbip, starter);
        if (ok && File.Exists(starter))
        {
            // the model is baked into the starter now, so this invocation's staging is disposable; a
            // saverless or failed run keeps its PBIP - it is the hand-finish / diagnosis artefact
            TryWipeOwnStaging(work);
            return new Outcome(sol.Id, true, starter, null, "materialised via SUPERBI_PBIX_SAVER.");
        }
        return new Outcome(sol.Id, false, null, pbip, "the saver ran but no starter .pbix was produced - check the saver command + the VM Desktop/AS engine.");
    }

    /// <summary>Best-effort removal of one staging dir. The recursive delete is safe only because the
    /// argument is always the unique per-invocation dir this same call composed and populated - never a
    /// caller-supplied path, and never to be widened to the daxops-materialise parent.</summary>
    private static void TryWipeOwnStaging(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
        catch (Exception ex) { Console.Error.WriteLine("[materialise] staging cleanup skipped: " + ex.Message); }
    }

    private static bool RunSaver(string template, string pbip, string outPbix)
    {
        string cmd = template.Replace("{pbip}", pbip).Replace("{out}", outPbix);
        try
        {
            // run through cmd.exe so the operator's template (pbi-tools / a script) resolves on PATH
            var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            // drain BOTH pipes concurrently so a verbose saver cannot deadlock on a full pipe buffer
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15 * 60 * 1000))   // a Desktop refresh + save can be slow; cap at 15 min
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort - never leave an orphaned Desktop */ }
                Console.Error.WriteLine("[materialise] saver timed out after 15 min - process tree killed");
                return false;
            }
            p.WaitForExit();   // let the async readers finish after the process exits
            string err = stderrTask.GetAwaiter().GetResult();
            _ = stdoutTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0) Console.Error.WriteLine($"[materialise] saver exit {p.ExitCode}: {err}");
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("[materialise] saver failed: " + ex.Message); return false; }
    }

    private static string SafeName(string s)
        => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
