using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using SuperBiMcp.Tools;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline coverage of the Power BI Service / Fabric REST client (Wave F) - no live token, no network.
/// A canned transport is injected via <see cref="FabricRest.UseTransportForTests"/> (the GraphClient seam)
/// and the retry/poll delays are zeroed, so the REAL orchestration runs end to end: base selection (myorg vs
/// fabric), the Bearer header (applied to the request, NEVER echoed in an exception), odata paging, the
/// bounded 429 Retry-After retry, the import poll to Succeeded, the Fabric 202 + Location LRO poll, the
/// executeQueries body shape, and the on-disk PBIP tree -> definition-parts mapping. Plus the ServiceTools
/// token-resolution contract (explicit param, DAXOPS_PBI_TOKEN env var, honest {ok:false} without either).
/// </summary>
public sealed class FabricRestTests
{
    private const string Token = "secret-test-token";

    // ---- plumbing -------------------------------------------------------------------------------

    /// <summary>A transport stub with FULL response control (status + headers + body) so Retry-After,
    /// Location and empty-body replies are testable; every request (URL, auth, body) is recorded.</summary>
    private sealed class FullHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;
        public List<string> Urls { get; } = new();
        public List<string?> AuthHeaders { get; } = new();
        public List<string> Bodies { get; } = new();

        public FullHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // AbsoluteUri (not ToString()) so percent-escaping survives for the URL-shape assertions
            Urls.Add(request.RequestUri!.AbsoluteUri);
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null ? "" : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            return Task.FromResult(_respond(request, Urls.Count));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>Zero the retry/poll delays for the duration of a test so the loops run instantly.</summary>
    private static IDisposable ZeroDelays()
    {
        TimeSpan retry = FabricRest.RetryFloor, poll = FabricRest.PollDelay;
        FabricRest.RetryFloor = TimeSpan.Zero;
        FabricRest.PollDelay = TimeSpan.Zero;
        return new Restore(() => { FabricRest.RetryFloor = retry; FabricRest.PollDelay = poll; });
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }

    /// <summary>An anonymous result -> JsonNode so its fields are assertable.</summary>
    private static JsonNode Roundtrip(object o) => JsonNode.Parse(JsonSerializer.Serialize(o))!;

    // scratch root: SUPERBI_TEST_SCRATCH override (e.g. to keep scratch off the system drive), temp fallback.
    private static string NewScratchDir()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "fabricrest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryWipe(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- URL / base selection ---------------------------------------------------------------------

    [Fact]
    public void Url_RelativePath_UsesTheMyorgBase()
        => Assert.Equal(FabricRest.PbiBase + "/groups", FabricRest.Url("/groups"));

    [Fact]
    public void FabricUrl_RelativePath_UsesTheFabricBase()
        => Assert.Equal(FabricRest.FabricBase + "/workspaces/w1/items", FabricRest.FabricUrl("/workspaces/w1/items"));

    [Fact]
    public void Url_AbsoluteUrl_IsReturnedVerbatim()
    {
        // an @odata.nextLink (or an LRO Location) is already absolute; it must NOT be re-based.
        string next = "https://api.powerbi.com/v1.0/myorg/groups?$skip=100";
        Assert.Equal(next, FabricRest.Url(next));
        string op = "https://api.fabric.microsoft.com/v1/operations/op-1";
        Assert.Equal(op, FabricRest.Url(op));
    }

    // ---- Bearer custody -----------------------------------------------------------------------------

    [Fact]
    public void BearerHeader_IsApplied_AndNeverInTheThrownException()
    {
        var handler = new FullHandler((_, _) => Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"Unauthorized\"}}"));
        using var swap = FabricRest.UseTransportForTests(handler);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FabricRest.ListDatasetsAsync("ws1", Token, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("Bearer " + Token, handler.AuthHeaders.Single());
        Assert.DoesNotContain(Token, ex.Message);      // token custody: never echoed
        Assert.Contains("403", ex.Message);
    }

    // ---- odata paging ---------------------------------------------------------------------------------

    [Fact]
    public void GetAllPages_FollowsTheODataNextLink_AndBoundsTheWalk()
    {
        string next = FabricRest.PbiBase + "/groups?$skip=1";
        var handler = new FullHandler((req, _) =>
            req.RequestUri!.ToString().Contains("$skip=1")
                ? Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"g2\"}]}")
                : Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"g1\"}],\"@odata.nextLink\":\"" + next + "\"}"));
        using var swap = FabricRest.UseTransportForTests(handler);

        var all = FabricRest.GetAllPagesAsync(FabricRest.Url("/groups"), Token, maxPages: 10, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal(2, all.Count);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Equal(new[] { "g1", "g2" }, all.Select(n => (string?)n?["id"]).ToArray());
    }

    // ---- 429 Retry-After --------------------------------------------------------------------------------

    [Fact]
    public void Throttled429_WithRetryAfter_IsRetried_ThenSucceeds()
    {
        using var fast = ZeroDelays();
        var handler = new FullHandler((_, n) =>
        {
            if (n == 1)
            {
                var throttled = Json((HttpStatusCode)429, "{}");
                throttled.Headers.Add("Retry-After", "0");
                return throttled;
            }
            return Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"g1\",\"name\":\"Sales\"}]}");
        });
        using var swap = FabricRest.UseTransportForTests(handler);

        var result = Roundtrip(FabricRest.ListWorkspacesAsync(Token, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal(2, handler.Urls.Count);   // 429 then the retried success
        Assert.True((bool?)result["ok"]);
        Assert.Equal(1, (int?)result["count"]);
    }

    [Fact]
    public void Throttled429_Forever_GivesUpAfterTheBoundedAttempts()
    {
        using var fast = ZeroDelays();
        var handler = new FullHandler((_, _) => Json((HttpStatusCode)429, "{}"));
        using var swap = FabricRest.UseTransportForTests(handler);

        Assert.Throws<InvalidOperationException>(() =>
            FabricRest.ListWorkspacesAsync(Token, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(4, handler.Urls.Count);   // one try + three retries, then the honest failure
    }

    // ---- pbix import + poll -------------------------------------------------------------------------------

    [Fact]
    public void ImportPbix_Posts_ThenPollsUntilSucceeded()
    {
        using var fast = ZeroDelays();
        string dir = NewScratchDir();
        try
        {
            string pbix = Path.Combine(dir, "sales.pbix");
            File.WriteAllBytes(pbix, new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 });

            var handler = new FullHandler((req, n) =>
            {
                if (req.Method == HttpMethod.Post)
                    return Json(HttpStatusCode.Accepted, "{\"id\":\"imp-1\",\"importState\":\"Publishing\"}");
                return n < 3
                    ? Json(HttpStatusCode.OK, "{\"id\":\"imp-1\",\"importState\":\"Publishing\"}")
                    : Json(HttpStatusCode.OK, "{\"id\":\"imp-1\",\"importState\":\"Succeeded\",\"reports\":[{\"id\":\"r1\"}],\"datasets\":[{\"id\":\"d1\"}]}");
            });
            using var swap = FabricRest.UseTransportForTests(handler);

            var result = Roundtrip(FabricRest.ImportPbixAsync("ws1", pbix, "Sales Model", "CreateOrOverwrite", Token, CancellationToken.None)
                .GetAwaiter().GetResult());

            Assert.True((bool?)result["ok"]);
            Assert.Equal("Succeeded", (string?)result["state"]);
            Assert.Equal("imp-1", (string?)result["importId"]);
            Assert.Contains("datasetDisplayName=Sales%20Model", handler.Urls[0]);
            Assert.Contains("nameConflict=CreateOrOverwrite", handler.Urls[0]);
            Assert.StartsWith(FabricRest.PbiBase + "/groups/ws1/imports", handler.Urls[0]);
            Assert.EndsWith("/groups/ws1/imports/imp-1", handler.Urls[^1]);
            Assert.Equal(3, handler.Urls.Count);   // POST + two polls
        }
        finally { TryWipe(dir); }
    }

    // ---- Fabric item publish: LRO 202 + Location poll -------------------------------------------------------

    [Fact]
    public void CreateItem_Lro202WithLocation_IsPolledToSucceeded()
    {
        using var fast = ZeroDelays();
        string opUrl = FabricRest.FabricBase + "/operations/op-1";
        int polls = 0;
        var handler = new FullHandler((req, _) =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("/items?type="))
                return Json(HttpStatusCode.OK, "{\"value\":[]}");          // nothing existing -> create
            if (req.Method == HttpMethod.Post)
            {
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                accepted.Headers.Location = new Uri(opUrl);
                return accepted;                                            // empty 202 body is legal
            }
            return url == opUrl && polls++ < 1
                ? Json(HttpStatusCode.OK, "{\"status\":\"Running\"}")
                : Json(HttpStatusCode.OK, "{\"status\":\"Succeeded\"}");
        });
        using var swap = FabricRest.UseTransportForTests(handler);

        var result = FabricRest.CreateOrUpdateItemAsync(
                "ws1", "Sales", "SemanticModel",
                new[] { ("definition.pbism", Convert.ToBase64String("{}"u8.ToArray())) },
                Token, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.True((bool?)result["ok"]);
        Assert.False((bool?)result["updated"]);
        Assert.Equal("Succeeded", (string?)result["operation"]);
        Assert.Contains(handler.Urls, u => u == FabricRest.FabricBase + "/workspaces/ws1/items");
        Assert.Contains(handler.Urls, u => u == opUrl);
        // the create body carried the InlineBase64 definition part
        string createBody = handler.Bodies[handler.Urls.IndexOf(FabricRest.FabricBase + "/workspaces/ws1/items")];
        var body = JsonNode.Parse(createBody)!;
        Assert.Equal("SemanticModel", (string?)body["type"]);
        Assert.Equal("InlineBase64", (string?)body["definition"]!["parts"]![0]!["payloadType"]);
    }

    [Fact]
    public void UpdateItem_WhenTheDisplayNameAlreadyExists_UsesUpdateDefinition()
    {
        using var fast = ZeroDelays();
        var handler = new FullHandler((req, _) =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("/items?type="))
                return Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"itm-9\",\"displayName\":\"Sales\"}]}");
            return Json(HttpStatusCode.OK, "{}");
        });
        using var swap = FabricRest.UseTransportForTests(handler);

        var result = FabricRest.CreateOrUpdateItemAsync(
                "ws1", "Sales", "Report",
                new[] { ("definition.pbir", Convert.ToBase64String("{}"u8.ToArray())) },
                Token, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.True((bool?)result["ok"]);
        Assert.True((bool?)result["updated"]);
        Assert.Equal("itm-9", (string?)result["itemId"]);
        Assert.Contains(handler.Urls, u => u == FabricRest.FabricBase + "/workspaces/ws1/items/itm-9/updateDefinition");
    }

    // ---- refresh --------------------------------------------------------------------------------------------

    [Fact]
    public void RefreshDataset_SendsTheRefreshBody_AndReturnsTheRequestIdFromLocation()
    {
        var handler = new FullHandler((_, _) =>
        {
            var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
            accepted.Headers.Location = new Uri(FabricRest.PbiBase + "/groups/ws1/datasets/ds1/refreshes/req-42");
            return accepted;
        });
        using var swap = FabricRest.UseTransportForTests(handler);

        var result = Roundtrip(FabricRest.RefreshDatasetAsync("ws1", "ds1", "full", Token, CancellationToken.None)
            .GetAwaiter().GetResult());

        Assert.True((bool?)result["ok"]);
        Assert.Equal("req-42", (string?)result["requestId"]);
        Assert.Equal(FabricRest.PbiBase + "/groups/ws1/datasets/ds1/refreshes", handler.Urls.Single());
        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal("full", (string?)body["type"]);
        Assert.Equal("NoNotification", (string?)body["notifyOption"]);
    }

    // ---- executeQueries body shape (the S4 seam) ----------------------------------------------------------------

    [Fact]
    public void ExecuteQueries_BuildsTheDocumentedRequestBody()
    {
        var handler = new FullHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"results\":[{\"tables\":[{\"rows\":[{\"[Total]\":42}]}]}]}"));
        using var swap = FabricRest.UseTransportForTests(handler);

        string dax = "EVALUATE ROW(\"Total\", [Total Sales])";
        var result = Roundtrip(FabricRest.ExecuteQueriesAsync("ws1", "ds1", dax, Token, CancellationToken.None)
            .GetAwaiter().GetResult());

        Assert.True((bool?)result["ok"]);
        Assert.Equal(FabricRest.PbiBase + "/groups/ws1/datasets/ds1/executeQueries", handler.Urls.Single());
        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal(dax, (string?)body["queries"]![0]!["query"]);
        Assert.True((bool?)body["serializerSettings"]!["includeNulls"]);
        Assert.NotNull(result["results"]);
    }

    // ---- PBIP tree -> definition parts -------------------------------------------------------------------------

    [Fact]
    public void PbipFolders_MapToDefinitionParts_ExcludingLocalMetadata()
    {
        string dir = NewScratchDir();
        try
        {
            // the exact tree generate_pbip / scaffold emit
            string sm = Path.Combine(dir, "Model.SemanticModel");
            string rp = Path.Combine(dir, "Model.Report");
            Directory.CreateDirectory(Path.Combine(sm, "definition", "tables"));
            Directory.CreateDirectory(Path.Combine(rp, "StaticResources", "SharedResources", "BaseThemes"));
            Directory.CreateDirectory(Path.Combine(rp, ".pbi"));
            File.WriteAllText(Path.Combine(sm, "definition.pbism"), "{\"version\":\"4.0\"}");
            File.WriteAllText(Path.Combine(sm, "definition", "model.tmdl"), "model Model");
            File.WriteAllText(Path.Combine(sm, "definition", "tables", "Sales.tmdl"), "table Sales");
            File.WriteAllText(Path.Combine(sm, ".platform"), "{}");                       // metadata - excluded
            File.WriteAllText(Path.Combine(rp, "definition.pbir"), "{\"version\":\"1.0\"}");
            File.WriteAllText(Path.Combine(rp, "report.json"), "{}");
            File.WriteAllText(Path.Combine(rp, "StaticResources", "SharedResources", "BaseThemes", "SuperBiBase.json"), "{}");
            File.WriteAllText(Path.Combine(rp, ".platform"), "{}");                        // metadata - excluded
            File.WriteAllText(Path.Combine(rp, ".pbi", "localSettings.json"), "{}");       // local cache - excluded

            var smParts = FabricRest.SemanticModelParts(sm);
            Assert.Equal(
                new[] { "definition.pbism", "definition/model.tmdl", "definition/tables/Sales.tmdl" },
                smParts.Select(p => p.path).ToArray());
            Assert.Equal("model Model", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                smParts.Single(p => p.path == "definition/model.tmdl").payloadB64)));

            var rpParts = FabricRest.ReportParts(rp);
            Assert.Contains("definition.pbir", rpParts.Select(p => p.path));
            Assert.Contains("report.json", rpParts.Select(p => p.path));
            Assert.Contains("StaticResources/SharedResources/BaseThemes/SuperBiBase.json", rpParts.Select(p => p.path));
            Assert.DoesNotContain(".platform", rpParts.Select(p => p.path));
            Assert.DoesNotContain(rpParts, p => p.path.StartsWith(".pbi/", StringComparison.OrdinalIgnoreCase));
        }
        finally { TryWipe(dir); }
    }

    // ---- ServiceTools token resolution ---------------------------------------------------------------------------

    [Fact]
    public void ServiceTools_WithoutAnyToken_ReturnsTheHonestInBandRefusal()
    {
        string? prev = Environment.GetEnvironmentVariable(ServiceTools.TokenEnvVar);
        Environment.SetEnvironmentVariable(ServiceTools.TokenEnvVar, null);
        try
        {
            var result = JsonNode.Parse(ServiceTools.ListWorkspaces(null))!;
            Assert.False((bool?)result["ok"]);
            Assert.Equal("no access token - pass accessToken or set DAXOPS_PBI_TOKEN", (string?)result["error"]);
        }
        finally { Environment.SetEnvironmentVariable(ServiceTools.TokenEnvVar, prev); }
    }

    [Fact]
    public void ServiceTools_PicksUpTheEnvVarToken_AndSendsItAsBearer()
    {
        string? prev = Environment.GetEnvironmentVariable(ServiceTools.TokenEnvVar);
        Environment.SetEnvironmentVariable(ServiceTools.TokenEnvVar, "env-token-1");
        try
        {
            var handler = new FullHandler((_, _) => Json(HttpStatusCode.OK, "{\"value\":[]}"));
            using var swap = FabricRest.UseTransportForTests(handler);

            var result = JsonNode.Parse(ServiceTools.ListWorkspaces(null))!;
            Assert.True((bool?)result["ok"]);
            Assert.Equal("Bearer env-token-1", handler.AuthHeaders.Single());
        }
        finally { Environment.SetEnvironmentVariable(ServiceTools.TokenEnvVar, prev); }
    }

    [Fact]
    public void ServiceTools_ExplicitAccessToken_BeatsTheEnvVar()
    {
        string? prev = Environment.GetEnvironmentVariable(ServiceTools.TokenEnvVar);
        Environment.SetEnvironmentVariable(ServiceTools.TokenEnvVar, "env-token-2");
        try { Assert.Equal("explicit-token", ServiceTools.RequireToken("explicit-token")); }
        finally { Environment.SetEnvironmentVariable(ServiceTools.TokenEnvVar, prev); }
    }
}
