using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

/// <summary>
/// Power BI Service / Fabric tools (Wave F) - the cloud tier over pure REST: publish an already-built
/// .pbix / PBIP into a workspace, trigger and schedule dataset refreshes, mint embed tokens and run DAX
/// against a published dataset. No Power BI Desktop involved, so these work from any OS.
///
/// Every tool needs a short-lived AAD access token carrying the Power BI scopes
/// (https://analysis.windows.net/powerbi/api/...): pass it as accessToken or set the DAXOPS_PBI_TOKEN env
/// var. The token is applied as an Authorization: Bearer header only - never stored, never logged, and
/// never echoed back in a result or an error.
/// </summary>
[McpServerToolType]
public static class ServiceTools
{
    /// <summary>The env var a caller may park the AAD token in instead of passing it per call.</summary>
    internal const string TokenEnvVar = "DAXOPS_PBI_TOKEN";

    /// <summary>Explicit accessToken first, else DAXOPS_PBI_TOKEN. A missing token surfaces as the in-band
    /// {ok:false} J.Try produces - never a throw to the MCP layer, and the token itself is never echoed.</summary>
    internal static string RequireToken(string? accessToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken)) return accessToken;
        string? env = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        throw new InvalidOperationException("no access token - pass accessToken or set DAXOPS_PBI_TOKEN");
    }

    private static string[] SplitIds(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [McpServerTool(Name = "list_workspaces")]
    [Description("List the Power BI Service workspaces the caller can reach (GET /groups). Needs an AAD access token with the Power BI scopes (accessToken param or the DAXOPS_PBI_TOKEN env var); the token is used as a Bearer header only and never echoed.")]
    public static string ListWorkspaces(
        [Description("AAD access token with Power BI scopes; omit to use DAXOPS_PBI_TOKEN")] string? accessToken = null)
        => J.Try(() => FabricRest.ListWorkspacesAsync(RequireToken(accessToken), CancellationToken.None).GetAwaiter().GetResult());

    [McpServerTool(Name = "publish_to_service")]
    [Description("Publish a built artifact to the Power BI Service: a .pbix path goes through the Import API (nameConflict CreateOrOverwrite by default); a PBIP folder (the tree generate_pbip/scaffold emit) is published at definition level via the Fabric items API - SemanticModel first, then Report, each created or updated in place. Needs an AAD access token with the Power BI scopes (accessToken param or DAXOPS_PBI_TOKEN); never echoed.")]
    public static string PublishToService(
        [Description("path to a .pbix file OR a PBIP folder (the project root, or its <name>.SemanticModel)")] string path,
        [Description("target workspace (group) id")] string workspaceId,
        [Description("dataset/item display name; defaults from the file/folder name")] string? datasetDisplayName = null,
        [Description("pbix import conflict policy: CreateOrOverwrite (default), Ignore, Abort or Overwrite")] string nameConflict = "CreateOrOverwrite",
        [Description("AAD access token with Power BI scopes; omit to use DAXOPS_PBI_TOKEN")] string? accessToken = null)
        => J.Try(() =>
        {
            string token = RequireToken(accessToken);
            if (File.Exists(path) && path.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase))
                return FabricRest.ImportPbixAsync(workspaceId, path, datasetDisplayName ?? Path.GetFileNameWithoutExtension(path), nameConflict, token, CancellationToken.None).GetAwaiter().GetResult();
            if (Directory.Exists(path))
                return FabricRest.PublishPbipAsync(workspaceId, path, datasetDisplayName, token, CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException($"path not found (need a .pbix file or a PBIP folder): {path}");
        });

    [McpServerTool(Name = "refresh_dataset")]
    [Description("Trigger a refresh of a published dataset (POST .../refreshes, notifyOption NoNotification). Returns the requestId to correlate in get_refresh_status. Needs an AAD access token with the Power BI scopes (accessToken param or DAXOPS_PBI_TOKEN); never echoed.")]
    public static string RefreshDataset(
        [Description("workspace (group) id")] string workspaceId,
        [Description("dataset id")] string datasetId,
        [Description("refresh type: full (default) or automatic")] string type = "full",
        [Description("AAD access token with Power BI scopes; omit to use DAXOPS_PBI_TOKEN")] string? accessToken = null)
        => J.Try(() => FabricRest.RefreshDatasetAsync(workspaceId, datasetId, type, RequireToken(accessToken), CancellationToken.None).GetAwaiter().GetResult());

    [McpServerTool(Name = "get_refresh_status")]
    [Description("Read the recent refresh history of a published dataset (GET .../refreshes?$top=N) - status, start/end times and any service error. Needs an AAD access token with the Power BI scopes (accessToken param or DAXOPS_PBI_TOKEN); never echoed.")]
    public static string GetRefreshStatus(
        [Description("workspace (group) id")] string workspaceId,
        [Description("dataset id")] string datasetId,
        [Description("how many recent refreshes to return")] int top = 5,
        [Description("AAD access token with Power BI scopes; omit to use DAXOPS_PBI_TOKEN")] string? accessToken = null)
        => J.Try(() => FabricRest.GetRefreshStatusAsync(workspaceId, datasetId, top, RequireToken(accessToken), CancellationToken.None).GetAwaiter().GetResult());

    [McpServerTool(Name = "set_refresh_schedule")]
    [Description("Set a published dataset's scheduled-refresh plan (PATCH .../refreshSchedule): enable/disable, the days, the local times and the time zone. Needs an AAD access token with the Power BI scopes (accessToken param or DAXOPS_PBI_TOKEN); never echoed.")]
    public static string SetRefreshSchedule(
        [Description("workspace (group) id")] string workspaceId,
        [Description("dataset id")] string datasetId,
        [Description("enable (true) or disable (false) the schedule")] bool enabled,
        [Description("comma-separated days, e.g. \"Monday,Wednesday,Friday\"")] string? days = null,
        [Description("comma-separated 24h times, e.g. \"07:00,16:30\"")] string? times = null,
        [Description("Windows time-zone id, e.g. \"New Zealand Standard Time\"")] string? localTimeZoneId = null,
        [Description("AAD access token with Power BI scopes; omit to use DAXOPS_PBI_TOKEN")] string? accessToken = null)
        => J.Try(() => FabricRest.SetRefreshScheduleAsync(workspaceId, datasetId, enabled, SplitIds(days), SplitIds(times), localTimeZoneId, RequireToken(accessToken), CancellationToken.None).GetAwaiter().GetResult());

    [McpServerTool(Name = "generate_embed_token")]
    [Description("Mint a Power BI EMBED token (POST /GenerateToken) for the given datasets / reports / target workspaces - the scoped, short-lived token an embedding app hands to the browser. Needs an AAD access token with the Power BI scopes (accessToken param or DAXOPS_PBI_TOKEN); the AAD token itself is never echoed.")]
    public static string GenerateEmbedToken(
        [Description("comma-separated dataset ids")] string? datasetIds = null,
        [Description("comma-separated report ids")] string? reportIds = null,
        [Description("comma-separated target workspace ids")] string? workspaceIds = null,
        [Description("AAD access token with Power BI scopes; omit to use DAXOPS_PBI_TOKEN")] string? accessToken = null)
        => J.Try(() => FabricRest.GenerateEmbedTokenAsync(SplitIds(datasetIds), SplitIds(reportIds), SplitIds(workspaceIds), RequireToken(accessToken), CancellationToken.None).GetAwaiter().GetResult());

    [McpServerTool(Name = "execute_service_query")]
    [Description("Run a DAX query against a PUBLISHED dataset over REST (POST .../executeQueries) - no Desktop, no live model session. Needs an AAD access token with the Power BI scopes (accessToken param or DAXOPS_PBI_TOKEN); never echoed.")]
    public static string ExecuteServiceQuery(
        [Description("workspace (group) id")] string workspaceId,
        [Description("dataset id")] string datasetId,
        [Description("the DAX query, e.g. EVALUATE TOPN(10, 'Sales')")] string dax,
        [Description("AAD access token with Power BI scopes; omit to use DAXOPS_PBI_TOKEN")] string? accessToken = null)
        => J.Try(() => FabricRest.ExecuteQueriesAsync(workspaceId, datasetId, dax, RequireToken(accessToken), CancellationToken.None).GetAwaiter().GetResult());

    [McpServerTool(Name = "list_service_datasets")]
    [Description("List the datasets and reports in a Power BI Service workspace (GET .../datasets + .../reports) - the ids feed refresh_dataset, execute_service_query and generate_embed_token. Needs an AAD access token with the Power BI scopes (accessToken param or DAXOPS_PBI_TOKEN); never echoed.")]
    public static string ListServiceDatasets(
        [Description("workspace (group) id")] string workspaceId,
        [Description("AAD access token with Power BI scopes; omit to use DAXOPS_PBI_TOKEN")] string? accessToken = null)
        => J.Try(() =>
        {
            string token = RequireToken(accessToken);
            var datasets = FabricRest.ListDatasetsAsync(workspaceId, token, CancellationToken.None).GetAwaiter().GetResult();
            var reports = FabricRest.ListReportsAsync(workspaceId, token, CancellationToken.None).GetAwaiter().GetResult();
            return new { ok = true, datasets, reports };
        });
}
