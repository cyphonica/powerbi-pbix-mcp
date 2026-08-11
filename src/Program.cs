using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuperBiMcp.Services;

// Super-BI-MCP : an MCP server that builds and edits Power BI .pbix files end to
// end - model (DAX measures, M / Power Query, tables, columns, relationships) via
// the live Tabular Object Model, and the report (pages, visuals, slicers, tables,
// matrices, charts, cards) via direct Report/Layout JSON editing.
//
// IMPORTANT: stdout is the JSON-RPC channel. Everything else must go to stderr.

// Headless batch mode (the product entry point): `SuperBiMcp build <manifest.json>` runs the
// report-generation pipeline in one command - no agent, no MCP, no Power BI Desktop.
if (args.Length > 0 && string.Equals(args[0], "build", StringComparison.OrdinalIgnoreCase))
    return SuperBiMcp.Cli.Run(args);

// Bulk refresh (the live Desktop loop): `SuperBiMcp refresh <path-or-glob> [...]` opens each .pbix in
// Power BI Desktop, refreshes the model through Desktop's own engine via TOM, saves with Ctrl+S and
// closes - one file at a time.
if (args.Length > 0 && string.Equals(args[0], "refresh", StringComparison.OrdinalIgnoreCase))
    return SuperBiMcp.BulkOps.RunRefresh(args);

// Headless model authoring: `SuperBiMcp scaffold <spec.json> <out> [name]` builds a Tabular model
// in memory and emits a complete PBIP project (TMDL) with NO Power BI Desktop / engine.
if (args.Length > 0 && string.Equals(args[0], "scaffold", StringComparison.OrdinalIgnoreCase))
    return SuperBiMcp.Headless.Run(args);

// Sentinel CI gate: `SuperBiMcp sentinel-diff before.json after.json` compares two integrity
// snapshots headless and exits non-zero on a critical regression.
if (args.Length > 0 && string.Equals(args[0], "sentinel-diff", StringComparison.OrdinalIgnoreCase))
    return SuperBiMcp.Sentinel.Run(args);

// Docs drift gate: `SuperBiMcp capability-map [out.md]` regenerates the authoritative tool index
// (docs/automation-capabilities/GENERATED-TOOL-INDEX.md) from the [McpServerTool] attributes;
// `capability-map --check` exits non-zero when the committed index has drifted from the code.
if (args.Length > 0 && string.Equals(args[0], "capability-map", StringComparison.OrdinalIgnoreCase))
    return SuperBiMcp.Agent.CapabilityMap.Run(args);

// Bake worker (the one step needing a real AS engine, i.e. a machine with Power BI Desktop installed):
// `SuperBiMcp bake <pbip> <out>` deploys the scaffolded model into the bundled Power BI engine, refreshes
// it to load the data, and ImageSaves the populated db to the binary DataModel the .pbix compiler folds in.
if (args.Length > 0 && string.Equals(args[0], "bake", StringComparison.OrdinalIgnoreCase))
    return SuperBiMcp.Bake.Run(args);

// Solution-starter materialiser: `SuperBiMcp materialize <solutionId|all>` scaffolds a
// Solution's model with its sample data and (via SUPERBI_PBIX_SAVER) bakes its starter .pbix.
if (args.Length > 0 && string.Equals(args[0], "materialize", StringComparison.OrdinalIgnoreCase))
    return SuperBiMcp.Materialize.Run(args);

// jobs - inspect and maintain the durable job queue (queue.db under the resolved job root) that serialises
// heavy Power BI Desktop work. Read-only by default; `reap` kills only PIDs the queue itself recorded.
//   usage: SuperBiMcp jobs list [--lane cheap|heavy] [--state QUEUED|RUNNING|...] [--limit N]
//          SuperBiMcp jobs show <jobId> | requeue <jobId> | reconcile | reap [--dry-run] | health
if (args.Length > 0 && string.Equals(args[0], "jobs", StringComparison.OrdinalIgnoreCase))
    return SuperBiMcp.Jobs.JobsCli.Run(args);

// `SuperBiMcp templates` dumps the 300 starting-point templates (pipe to a file for a front end).
if (args.Length > 0 && string.Equals(args[0], "templates", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        new { ok = true, count = SuperBiMcp.TemplateLibrary.All.Count, templates = SuperBiMcp.TemplateLibrary.All },
        SuperBiMcp.Cli.Pretty));
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Long-lived services: the model connections must survive across tool calls.
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<PortDiscovery>();
builder.Services.AddSingleton<ModelService>();
builder.Services.AddSingleton<ModelPersistService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<PbirService>();
builder.Services.AddSingleton<ExcelService>();
builder.Services.AddSingleton<PropertyCatalog>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
