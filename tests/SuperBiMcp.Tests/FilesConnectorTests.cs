using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline coverage of the REAL <see cref="FilesConnector"/> orchestration - the layer the live smoke only
/// ever skips. The local-file sub-mode is driven against the committed fixtures (no network); the REST / JSON
/// sub-mode is driven against an injected <see cref="StubHttpHandler"/> so the connector's own paging walk,
/// jsonPath resolution, key-union header, nested-cell flattening and the page / row caps run end to end. None
/// of this is a re-implementation: <see cref="FilesConnector.FetchAsync"/> is the system under test.
/// </summary>
public sealed class FilesConnectorTests
{
    private static ConnectorRequest Req(JsonObject @params, string? token = null)
        => new() { AccessToken = token, Params = @params };

    private static IngestResult Fetch(ConnectorRequest req)
    {
        // the work dir must outlive this call so the assertions can read the staged CSVs.
        var work = Fixtures.NewWorkDir();
        return new FilesConnector().FetchAsync(req, work.Path, CancellationToken.None)
                                   .GetAwaiter().GetResult();
    }

    // ---- local files sub-mode (no network) -----------------------------------------------------

    [Fact]
    public void Files_StagesCsvAndExcel_IntoTableCsvMap_WithSynthesisedSchema()
    {
        var p = new JsonObject
        {
            ["files"] = new JsonArray { Fixtures.Path("sales.csv"), Fixtures.Path("multi.xlsx") },
        };

        var result = Fetch(Req(p));

        // the .csv lands as one table; the .xlsx expands one CSV per sheet (People, Money).
        Assert.True(result.Tables.ContainsKey("sales"), string.Join(" | ", result.Notes));
        Assert.True(result.Tables.ContainsKey("People"));
        Assert.True(result.Tables.ContainsKey("Money"));
        foreach (var (_, path) in result.Tables)
            Assert.True(File.Exists(path), path);

        // the synthesised schema has a table per CSV and inferred per-column types off the real CSV reader.
        var sales = result.Schema.Tables.Single(t => t.Name == "sales");
        Assert.Equal("int64", sales.Columns.Single(c => c.Name == "OrderId").DataType);
        Assert.Equal("date", sales.Columns.Single(c => c.Name == "OrderDate").DataType);
        Assert.Equal("boolean", sales.Columns.Single(c => c.Name == "Active").DataType);
        // the quoted-comma row survived as data (RFC-4180 round-trip through staging).
        string[] lines = File.ReadAllLines(result.Tables["sales"]);
        Assert.Equal(4, lines.Length); // header + 3 data rows
    }

    [Fact]
    public void Files_MissingPath_RecordsNoteAndDoesNotCrash()
    {
        var p = new JsonObject { ["files"] = new JsonArray { @"C:\does\not\exist.csv" } };
        var result = Fetch(Req(p));

        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("file not found"));
    }

    [Fact]
    public void NoSource_RecordsAGuidanceNote()
    {
        var result = Fetch(Req(new JsonObject()));
        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("No usable source"));
    }

    // ---- REST / JSON sub-mode (stubbed transport) ----------------------------------------------

    [Fact]
    public void Rest_FlattensRecordsUnderJsonPath_UnionsKeys_AndSerialisesNestedCells()
    {
        // a single page of records under data.items; one record carries an extra key + a nested object.
        string body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["items"] = new JsonArray
                {
                    new JsonObject { ["id"] = 1, ["name"] = "A" },
                    new JsonObject { ["id"] = 2, ["name"] = "B", ["meta"] = new JsonObject { ["k"] = "v" } },
                },
            },
        }.ToJsonString();

        using var handler = new StubHttpHandler(_ => (HttpStatusCode.OK, body));
        using var _ = FilesConnector.UseTransportForTests(handler);

        var p = new JsonObject
        {
            ["rest"] = new JsonObject
            {
                ["url"] = "https://api.test/things",
                ["jsonPath"] = "data.items",
                ["table"] = "things",
            },
        };
        var result = Fetch(Req(p));

        Assert.True(result.Tables.ContainsKey("things"), string.Join(" | ", result.Notes));
        string[] lines = File.ReadAllLines(result.Tables["things"]);
        Assert.Equal("id,name,meta", lines[0]);     // union of keys, in first-seen order
        Assert.Equal("1,A,", lines[1]);             // record 1 has no meta -> blank cell
        Assert.StartsWith("2,B,\"", lines[2]);      // nested object serialised + quoted (contains a comma/colon)
        Assert.Contains("\"\"k\"\":\"\"v\"\"", lines[2]); // doubled quotes from RFC-4180 of the compact JSON
        Assert.Contains(result.Notes, n => n.Contains("ingested 2 record"));
        Assert.Single(handler.Requests);            // mode:none pulls exactly one page
    }

    [Fact]
    public void Rest_BadJsonPath_RecordsNote_AndIngestsNothing()
    {
        string body = new JsonObject { ["data"] = new JsonObject() }.ToJsonString(); // no .items array
        using var handler = new StubHttpHandler(_ => (HttpStatusCode.OK, body));
        using var _ = FilesConnector.UseTransportForTests(handler);

        var p = new JsonObject
        {
            ["rest"] = new JsonObject { ["url"] = "https://api.test/x", ["jsonPath"] = "data.items" },
        };
        var result = Fetch(Req(p));

        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("did not resolve to an array"));
    }

    [Fact]
    public void Rest_Paging_WalksPagesUntilShortPage_AndBuildsPagingQuery()
    {
        // page 1 + page 2 are full (pageSize 2), page 3 is short (1 record) -> walk stops after page 3.
        using var handler = new StubHttpHandler(req =>
        {
            int page = QueryInt(req.RequestUri!.ToString(), "page", 1);
            JsonArray items = page switch
            {
                1 => new JsonArray { Rec(1), Rec(2) },
                2 => new JsonArray { Rec(3), Rec(4) },
                3 => new JsonArray { Rec(5) },          // short page ends pagination
                _ => new JsonArray(),
            };
            return (HttpStatusCode.OK, new JsonObject { ["rows"] = items }.ToJsonString());
        });
        using var _ = FilesConnector.UseTransportForTests(handler);

        var p = new JsonObject
        {
            ["rest"] = new JsonObject
            {
                ["url"] = "https://api.test/list",
                ["jsonPath"] = "rows",
                ["table"] = "list",
                ["paging"] = new JsonObject
                {
                    ["mode"] = "page",
                    ["param"] = "page",
                    ["sizeParam"] = "per_page",
                    ["start"] = 1,
                    ["pageSize"] = 2,
                },
            },
        };
        var result = Fetch(Req(p));

        string[] lines = File.ReadAllLines(result.Tables["list"]);
        Assert.Equal(6, lines.Length);              // header + 5 records across 3 pages
        Assert.Contains(result.Notes, n => n.Contains("across 3 page"));
        Assert.Equal(3, handler.Requests.Count);
        // the connector built page + per_page query params, preserving paging contract.
        Assert.Contains(handler.Requests, u => u.Contains("page=1") && u.Contains("per_page=2"));
        Assert.Contains(handler.Requests, u => u.Contains("page=3"));
        Assert.DoesNotContain(handler.Requests, u => u.Contains("page=4")); // stopped on the short page
    }

    [Fact]
    public void Rest_PageCap_StopsAtMaxPages_AndRecordsCapNote()
    {
        // every page is full, so only the maxPages cap stops the walk - exercising the page-cap branch.
        using var handler = new StubHttpHandler(req =>
        {
            int page = QueryInt(req.RequestUri!.ToString(), "page", 1);
            return (HttpStatusCode.OK,
                new JsonObject { ["rows"] = new JsonArray { Rec(page * 10), Rec(page * 10 + 1) } }.ToJsonString());
        });
        using var _ = FilesConnector.UseTransportForTests(handler);

        var p = new JsonObject
        {
            ["rest"] = new JsonObject
            {
                ["url"] = "https://api.test/list",
                ["jsonPath"] = "rows",
                ["table"] = "capped",
                ["paging"] = new JsonObject
                {
                    ["mode"] = "page",
                    ["pageSize"] = 2,
                    ["maxPages"] = 3,   // bounded; the connector clamps to MaxRestPages too
                },
            },
        };
        var result = Fetch(Req(p));

        Assert.Equal(3, handler.Requests.Count);    // stopped exactly at the page cap
        string[] lines = File.ReadAllLines(result.Tables["capped"]);
        Assert.Equal(7, lines.Length);              // header + 2 records * 3 pages
        Assert.Contains(result.Notes, n => n.Contains("pagination CAP reached") && n.Contains("page cap"));
    }

    [Fact]
    public void Rest_RowCap_StopsAtMaxRestRows_AndRecordsCapNote()
    {
        // a single page that returns more than the row cap (200,000); the connector must stop at the cap.
        const int overCap = 200_010;
        using var handler = new StubHttpHandler(_ =>
        {
            var arr = new JsonArray();
            for (int i = 0; i < overCap; i++) arr.Add(new JsonObject { ["id"] = i });
            return (HttpStatusCode.OK, new JsonObject { ["rows"] = arr }.ToJsonString());
        });
        using var _ = FilesConnector.UseTransportForTests(handler);

        var p = new JsonObject
        {
            ["rest"] = new JsonObject { ["url"] = "https://api.test/big", ["jsonPath"] = "rows", ["table"] = "big" },
        };
        var result = Fetch(Req(p));

        string[] lines = File.ReadAllLines(result.Tables["big"]);
        Assert.Equal(200_000 + 1, lines.Length);    // capped to exactly MaxRestRows data rows (+ header)
        Assert.Contains(result.Notes, n => n.Contains("pagination CAP reached") && n.Contains("row cap"));
    }

    [Fact]
    public void Rest_HttpError_RecordsNote_AndIngestsNothing()
    {
        using var handler = new StubHttpHandler(_ => (HttpStatusCode.InternalServerError, "{}"));
        using var _ = FilesConnector.UseTransportForTests(handler);

        var p = new JsonObject { ["rest"] = new JsonObject { ["url"] = "https://api.test/boom" } };
        var result = Fetch(Req(p));

        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("rest fetch failed"));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static JsonObject Rec(int id) => new() { ["id"] = id, ["v"] = "row" + id };

    private static int QueryInt(string url, string key, int dflt)
    {
        int i = url.IndexOf(key + "=", StringComparison.Ordinal);
        if (i < 0) return dflt;
        int start = i + key.Length + 1;
        int end = start;
        while (end < url.Length && char.IsDigit(url[end])) end++;
        return end > start && int.TryParse(url[start..end], out var v) ? v : dflt;
    }
}
