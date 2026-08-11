using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Agent;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

[McpServerToolType]
public static class ConnectionTools
{
    [McpServerTool(Name = "list_open_models")]
    [UnsafeForPipeline]
    [Description("UNSAFE-FOR-PIPELINE (interactive attach only): lists the Power BI Desktop models a human currently has open on this machine, opening a probe connection into every one of them. Open the .pbix in Power BI Desktop first. Never call this from an unattended job.")]
    public static string ListOpenModels(ModelService model)
        => J.Try(model.ListOpenModels);

    [McpServerTool(Name = "connect_model")]
    [UnsafeForPipeline]
    [Description("UNSAFE-FOR-PIPELINE (interactive attach only): connects to a Power BI Desktop model a human already has open and returns a sessionId used by all model tools. Omitting port attaches to the most recently written workspace port, which is a guess, not a guarantee. Unattended jobs must launch their own Desktop instead of attaching.")]
    public static string ConnectModel(
        ModelService model,
        [Description("Loopback port from list_open_models. Omit to attach to the newest live workspace port.")] int? port = null)
        => J.Try(() => model.Connect(port));

    [McpServerTool(Name = "get_model_summary")]
    [Description("Return the model schema: tables, columns, measures, relationships and named M expressions. Use this to get exact names before binding visuals.")]
    public static string GetModelSummary(
        ModelService model,
        [Description("sessionId from connect_model")] string sessionId)
        => J.Try(() => model.Summary(sessionId));
}
