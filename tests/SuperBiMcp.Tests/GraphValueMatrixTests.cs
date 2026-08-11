using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline end-to-end coverage of the REAL Excel Online connector shaping: a canned Microsoft Graph workbook
/// transport (no token, no network) is injected via <see cref="GraphClient.UseTransportForTests"/> and the
/// connector's own <see cref="ExcelOnlineConnector.FetchAsync"/> is driven against it. This exercises the
/// production value-matrix -&gt; CSV path inside <c>WriteMatrix</c> and <c>FetchTableAsync</c> - the first row
/// promoted to header, ragged rows padded / truncated by the sink, nested cells flattened, nulls blanked,
/// blank header cells renamed to Column{n}, the per-column type inference, the empty / header-only skip
/// branches, the MaxCellsPerSheet / MaxTableRows caps with their capHit notes, and the table-rows paging walk.
/// None of this is a hand-rolled copy: if any of those branches broke, these tests would fail.
/// </summary>
public sealed class GraphValueMatrixTests
{
    private const string DriveId = "drv1";
    private const string ItemId = "itm1";

    // Build the Excel Online request the connector reads driveId/itemId (+ optional worksheet/table) from.
    private static ConnectorRequest Req(string? worksheet = null, string? table = null)
    {
        var p = new JsonObject { ["driveId"] = DriveId, ["itemId"] = ItemId };
        if (worksheet != null) p["worksheet"] = worksheet;
        if (table != null) p["table"] = table;
        return new ConnectorRequest { AccessToken = "test-token", Params = p };
    }

    // A Graph transport that serves a fixed set of worksheets, each with a used-range value matrix; and any
    // named tables with their header + paged rows. Unknown URLs 404 so a wrong path surfaces as a failure.
    private sealed class WorkbookStub
    {
        public Dictionary<string, JsonArray> Worksheets { get; } = new();         // sheet name -> value matrix
        public Dictionary<string, JsonArray> TableHeaders { get; } = new();       // table name -> [[header...]]
        public Dictionary<string, List<JsonArray>> TableRows { get; } = new();    // table name -> row "values" matrices

        public StubHttpHandler Build() => new(req =>
        {
            string url = req.RequestUri!.ToString();

            if (url.Contains("/workbook/worksheets?$select=name"))
            {
                var arr = new JsonArray();
                foreach (var name in Worksheets.Keys) arr.Add(new JsonObject { ["name"] = name });
                return (HttpStatusCode.OK, new JsonObject { ["value"] = arr }.ToJsonString());
            }

            foreach (var (name, matrix) in Worksheets)
                if (url.Contains("/workbook/worksheets/" + Uri.EscapeDataString(name) + "/usedRange"))
                    return (HttpStatusCode.OK, new JsonObject { ["values"] = matrix.DeepClone() }.ToJsonString());

            foreach (var (name, header) in TableHeaders)
                if (url.Contains("/workbook/tables/" + Uri.EscapeDataString(name) + "/headerRowRange"))
                    return (HttpStatusCode.OK, new JsonObject { ["values"] = header.DeepClone() }.ToJsonString());

            foreach (var (name, allRows) in TableRows)
                if (url.Contains("/workbook/tables/" + Uri.EscapeDataString(name) + "/rows"))
                {
                    int skip = QueryInt(url, "$skip", 0);
                    int top = QueryInt(url, "$top", 5000);
                    var page = new JsonArray();
                    for (int i = skip; i < Math.Min(skip + top, allRows.Count); i++)
                        page.Add(new JsonObject { ["values"] = allRows[i].DeepClone() });
                    return (HttpStatusCode.OK, new JsonObject { ["value"] = page }.ToJsonString());
                }

            return (HttpStatusCode.NotFound, "{}");
        });

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

    private static IngestResult Fetch(WorkbookStub stub, ConnectorRequest req)
    {
        using var handler = stub.Build();
        using var swap = GraphClient.UseTransportForTests(handler);
        // the work dir must outlive this call so the assertions can read the staged CSVs; the OS temp area
        // is reclaimed by CI / the box, so we deliberately do not dispose it here.
        var work = Fixtures.NewWorkDir();
        return new ExcelOnlineConnector()
            .FetchAsync(req, work.Path, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    // ---- worksheet used-range path -------------------------------------------------------------

    [Fact]
    public void Worksheet_FirstRowBecomesHeader_DataRowsFollow_AndTypesInferred()
    {
        var stub = new WorkbookStub();
        stub.Worksheets["Sheet1"] = new JsonArray
        {
            new JsonArray { "Name", "Age", "Active" },
            new JsonArray { "Alice", 30, true },
            new JsonArray { "Bob", 41, false },
        };

        var result = Fetch(stub, Req());

        Assert.True(result.Tables.ContainsKey("Sheet1"), string.Join(" | ", result.Notes));
        string[] lines = DataLines(result.Tables["Sheet1"], out string header);
        Assert.Equal("Name,Age,Active", header);
        Assert.Equal("Alice,30,true", lines[0]);
        Assert.Equal("Bob,41,false", lines[1]);

        // the connector inferred a type per column off the sampled cells.
        var ts = result.Schema.Tables.Single(t => t.Name == "Sheet1");
        Assert.Equal("int64", ts.Columns.Single(c => c.Name == "Age").DataType);
        Assert.Equal("boolean", ts.Columns.Single(c => c.Name == "Active").DataType);
        Assert.Equal("string", ts.Columns.Single(c => c.Name == "Name").DataType);
    }

    [Fact]
    public void Worksheet_RaggedRows_ArePaddedAndTruncatedToHeaderWidth_AndNestedCellsFlattened()
    {
        var stub = new WorkbookStub();
        stub.Worksheets["WS"] = new JsonArray
        {
            new JsonArray { "Id", "B", "Lookup" },
            new JsonArray { 1, "2" },                                                   // short: missing Lookup -> padded
            new JsonArray { 9, "8", new JsonObject { ["LookupId"] = 7, ["LookupValue"] = "Jane" }, "extra" }, // long + nested
            new JsonArray { 3, "x", null },                                             // null cell -> empty
        };

        var result = Fetch(stub, Req(worksheet: "WS"));
        string[] lines = DataLines(result.Tables["WS"], out _);

        Assert.Equal("1,2,", lines[0]);                  // padded to 3 columns
        Assert.StartsWith("9,8,\"", lines[1]);           // nested object flattened into one quoted cell, extra truncated
        Assert.Contains("LookupId", lines[1]);
        Assert.Contains("Jane", lines[1]);
        Assert.Equal(3, CountTopLevelFields(lines[1]));   // exactly header-width, never the trailing "extra"
        Assert.Equal("3,x,", lines[2]);                  // null -> empty trailing field
    }

    [Fact]
    public void Worksheet_BlankHeaderCells_AreRenamedToColumnN()
    {
        var stub = new WorkbookStub();
        stub.Worksheets["S"] = new JsonArray
        {
            new JsonArray { "Keep", "", "  " },  // 2nd + 3rd headers blank
            new JsonArray { "a", "b", "c" },
        };

        var result = Fetch(stub, Req(worksheet: "S"));
        DataLines(result.Tables["S"], out string header);

        Assert.Equal("Keep,Column2,Column3", header);
        var ts = result.Schema.Tables.Single(t => t.Name == "S");
        Assert.Equal(new[] { "Keep", "Column2", "Column3" }, ts.Columns.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Worksheet_Empty_YieldsNoteAndNoCsv()
    {
        var stub = new WorkbookStub();
        stub.Worksheets["Blank"] = new JsonArray(); // empty used range

        var result = Fetch(stub, Req(worksheet: "Blank"));

        Assert.False(result.Tables.ContainsKey("Blank"));
        Assert.Contains(result.Notes, n => n.Contains("worksheet 'Blank'") && n.Contains("empty"));
    }

    [Fact]
    public void Worksheet_CellCap_StopsEarly_AndRecordsCapNote()
    {
        // a wide, deep matrix that blows past MaxCellsPerSheet (5,000,000 cells). 500 cols * ~10,001 rows.
        const int cols = 500;
        const int rows = 10_050; // 500 * 10050 = 5,025,000 cells > cap
        var matrix = new JsonArray();
        var head = new JsonArray();
        for (int c = 0; c < cols; c++) head.Add("C" + c);
        matrix.Add(head);
        for (int r = 0; r < rows; r++)
        {
            var row = new JsonArray();
            for (int c = 0; c < cols; c++) row.Add(r);
            matrix.Add(row);
        }
        var stub = new WorkbookStub();
        stub.Worksheets["Big"] = matrix;

        var result = Fetch(stub, Req(worksheet: "Big"));

        Assert.True(result.Tables.ContainsKey("Big"));
        Assert.Contains(result.Notes, n => n.Contains("cell CAP reached"));
        // the cap bit before all rows were written: fewer data lines than the source had.
        string[] lines = DataLines(result.Tables["Big"], out _);
        Assert.True(lines.Length < rows, $"expected truncation below {rows}, got {lines.Length}");
    }

    // ---- named-table path (paging + row cap) ---------------------------------------------------

    [Fact]
    public void Table_PagesRows_InfersTypes_AndBlankHeaderRenamed()
    {
        var stub = new WorkbookStub();
        stub.TableHeaders["Orders"] = new JsonArray { new JsonArray { "OrderId", "" } }; // 2nd header blank
        var allRows = new List<JsonArray>();
        for (int i = 1; i <= 3; i++)
            allRows.Add(new JsonArray { new JsonArray { i, "2024-06-2" + i } }); // each row's "values" is a 1-row matrix
        stub.TableRows["Orders"] = allRows;

        var result = Fetch(stub, Req(table: "Orders"));

        Assert.True(result.Tables.ContainsKey("Orders"), string.Join(" | ", result.Notes));
        string[] lines = DataLines(result.Tables["Orders"], out string header);
        Assert.Equal("OrderId,Column2", header);     // blank header renamed
        Assert.Equal(3, lines.Length);               // all 3 rows paged in
        Assert.Equal("1,2024-06-21", lines[0]);

        var ts = result.Schema.Tables.Single(t => t.Name == "Orders");
        Assert.Equal("int64", ts.Columns[0].DataType);
        Assert.Equal("date", ts.Columns[1].DataType);
        Assert.Contains(result.Notes, n => n.Contains("table 'Orders'") && n.Contains("ingested 3"));
    }

    [Fact]
    public void Table_NoHeaderRow_IsSkippedWithNote()
    {
        var stub = new WorkbookStub();
        stub.TableHeaders["Empty"] = new JsonArray(); // headerRowRange returns no values
        stub.TableRows["Empty"] = new List<JsonArray>();

        var result = Fetch(stub, Req(table: "Empty"));

        Assert.False(result.Tables.ContainsKey("Empty"));
        Assert.Contains(result.Notes, n => n.Contains("table 'Empty'") && n.Contains("no header row"));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string[] DataLines(string path, out string headerLine)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        var all = text.Split("\r\n", StringSplitOptions.None).Where(l => l.Length > 0).ToArray();
        headerLine = all[0];
        return all.Skip(1).ToArray();
    }

    private static int CountTopLevelFields(string line)
    {
        int fields = 1; bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQ = !inQ;
            else if (c == ',' && !inQ) fields++;
        }
        return fields;
    }
}
