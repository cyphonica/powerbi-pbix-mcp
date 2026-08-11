using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL Wave S engine: the hardened literal type-suffix encoder, the filter/query AST
/// completeness pieces (between / does-not-contain / fixed-anchor relative date / visual aggregation /
/// visual Top N / filter restatement), the Deneb / Vega-Lite visual writer (jsonSpec round-trip), and the
/// DataMashup / Power Query M extract + edit (against a SYNTHESISED in-test QDEFF package - no real .pbix).
/// Fault-sensitive: each test asserts the exact JSON / byte shape the engine writes.
/// </summary>
public sealed class ReportWaveSTests
{
    private const string Page = "Page1";

    // ---- in-memory report scaffolding (mirrors Open()'s JsonObject root) ----

    private static (ReportService svc, string sid, SessionStore store) NewReport(params (string id, string type)[] visuals)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var containers = new JsonArray();
        foreach (var (id, type) in visuals)
        {
            var sv = new JsonObject { ["visualType"] = type };
            var config = new JsonObject
            {
                ["name"] = id,
                ["layouts"] = new JsonArray { new JsonObject { ["id"] = 0,
                    ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["width"] = 100, ["height"] = 80, ["tabOrder"] = 0 } } },
                ["singleVisual"] = sv,
            };
            containers.Add(new JsonObject
            {
                ["x"] = 0, ["y"] = 0, ["z"] = 0, ["width"] = 100, ["height"] = 80,
                ["config"] = config.ToJsonString(), ["filters"] = "[]",
            });
        }

        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 32),
            ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = containers,
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720, ["displayOption"] = 1,
        };
        var root = new JsonObject { ["sections"] = new JsonArray { section }, ["config"] = "{}", ["filters"] = "[]" };

        var session = new ReportSession
        {
            Id = store.NewId("report"),
            PbixPath = "in-memory.pbix",
            Layout = new ReportLayout { Root = root, LayoutPartName = "Report/Layout" },
        };
        store.AddReport(session);
        return (svc, session.Id, store);
    }

    private static JsonObject Root(SessionStore store, string sid) => store.GetReport(sid).Layout.Root;
    private static JsonObject Section(SessionStore store, string sid) =>
        ((JsonArray)Root(store, sid)["sections"]!).OfType<JsonObject>().First();
    private static JsonObject ContainerOf(SessionStore store, string sid, string id) =>
        ((JsonArray)Section(store, sid)["visualContainers"]!).OfType<JsonObject>()
            .First(vc => (string?)(JsonNode.Parse((string)vc["config"]!) as JsonObject)!["name"] == id);
    private static JsonObject SingleVisual(SessionStore store, string sid, string id) =>
        (JsonObject)(JsonNode.Parse((string)ContainerOf(store, sid, id)["config"]!) as JsonObject)!["singleVisual"]!;
    private static JsonObject FirstPageFilter(SessionStore store, string sid) =>
        (JsonObject)(JsonNode.Parse((string)Section(store, sid)["filters"]!) as JsonArray)![0]!;

    // give an existing visual a prototypeQuery + projections so aggregation / topN edits have something to bite.
    private static void SeedQuery(SessionStore store, string sid, string id, JsonObject pq, JsonObject? proj = null)
    {
        var container = ContainerOf(store, sid, id);
        var co = JsonNode.Parse((string)container["config"]!) as JsonObject;
        var sv = (JsonObject)co!["singleVisual"]!;
        sv["prototypeQuery"] = pq;
        if (proj != null) sv["projections"] = proj;
        container["config"] = co.ToJsonString();
    }

    // ====================================================================== PART 1a: literal encoder matrix

    // The encoder is private; we observe its output through the filter builders that route through it.

    [Theory]
    [InlineData("double", "0", "0D")]
    [InlineData("double", "3.5", "3.5D")]
    [InlineData("int", "10", "10L")]
    [InlineData("long", "42", "42L")]
    [InlineData("decimal", "12.34", "12.34M")]
    [InlineData("string", "EUR", "'EUR'")]
    [InlineData("bool", "true", "true")]
    [InlineData("bool", "false", "false")]
    [InlineData("datetime", "2024-01-01", "datetime'2024-01-01T00:00:00'")]
    [InlineData("color", "#FF0000", "'#FF0000'")]
    public void LiteralEncoder_Matrix_EmitsCorrectTypeSuffix(string valueType, string value, string expected)
    {
        var (svc, sid, store) = NewReport();
        // route through the report-scope between filter; both lo+hi use the same literal.
        svc.AddBetweenFilter(sid, "report", null, null, "T", "F", "column", value, value, valueType);
        var arr = JsonNode.Parse((string)Root(store, sid)["filters"]!) as JsonArray;
        var f = (JsonObject)arr![arr.Count - 1]!;
        string got = (string?)f["filter"]!["Where"]![0]!["Condition"]!["And"]!["Left"]!["Comparison"]!["Right"]!["Literal"]!["Value"]!;
        Assert.Equal(expected, got);
    }

    [Fact]
    public void LiteralEncoder_FaultSensitive_WrongSuffixDiffers()
    {
        // prove the matrix is fault-sensitive: an int literal is NOT the double encoding.
        var (svc, sid, store) = NewReport();
        svc.AddBetweenFilter(sid, "report", null, null, "T", "F", "column", "10", "10", "int");
        var arr = JsonNode.Parse((string)Root(store, sid)["filters"]!) as JsonArray;
        var f = (JsonObject)arr![arr.Count - 1]!;
        string got = (string?)f["filter"]!["Where"]![0]!["Condition"]!["And"]!["Left"]!["Comparison"]!["Right"]!["Literal"]!["Value"]!;
        Assert.Equal("10L", got);
        Assert.NotEqual("10D", got);   // the classic silent-blank-visual bug if it were a double
    }

    [Fact]
    public void LiteralEncoder_StringEscapesInternalQuote()
    {
        var (svc, sid, store) = NewReport();
        svc.AddDoesNotContainFilter(sid, "report", null, null, "T", "F", "O'Brien");
        var arr = JsonNode.Parse((string)Root(store, sid)["filters"]!) as JsonArray;
        var f = (JsonObject)arr![arr.Count - 1]!;
        string got = (string?)f["filter"]!["Where"]![0]!["Condition"]!["Not"]!["Expression"]!["Contains"]!["Right"]!["Literal"]!["Value"]!;
        Assert.Equal("'O''Brien'", got);   // internal ' doubled
    }

    // ====================================================================== PART 1b: between / does-not-contain

    [Fact]
    public void AddBetweenFilter_WritesGteAndLte_UnderAnd()
    {
        var (svc, sid, store) = NewReport();
        svc.AddBetweenFilter(sid, "page", Page, null, "Fact", "Sales", "measure", "100", "500", "decimal");

        var f = FirstPageFilter(store, sid);
        Assert.Equal("Advanced", (string?)f["type"]);
        var and = (JsonObject)f["filter"]!["Where"]![0]!["Condition"]!["And"]!;
        Assert.Equal(2, and["Left"]!["Comparison"]!["ComparisonKind"]!.GetValue<int>());  // GTE
        Assert.Equal(4, and["Right"]!["Comparison"]!["ComparisonKind"]!.GetValue<int>()); // LTE
        Assert.Equal("100M", (string?)and["Left"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);
        Assert.Equal("500M", (string?)and["Right"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);
        Assert.NotNull(and["Left"]!["Comparison"]!["Left"]!["Measure"]);   // fieldKind=measure honoured
    }

    [Fact]
    public void AddDoesNotContainFilter_WrapsContainsInNot()
    {
        var (svc, sid, store) = NewReport();
        svc.AddDoesNotContainFilter(sid, "page", Page, null, "Dim", "Name", "test");

        var where = (JsonObject)FirstPageFilter(store, sid)["filter"]!["Where"]![0]!["Condition"]!;
        Assert.NotNull(where["Not"]);
        Assert.NotNull(where["Not"]!["Expression"]!["Contains"]);
        Assert.Equal("'test'", (string?)where["Not"]!["Expression"]!["Contains"]!["Right"]!["Literal"]!["Value"]);
    }

    // ====================================================================== PART 1c: fixed-anchor relative date

    [Fact]
    public void FixedAnchorRelativeDate_Last3Months_BoundsAroundAnchor()
    {
        var (svc, sid, store) = NewReport();
        svc.AddFixedAnchorRelativeDateFilter(sid, "page", Page, null, "Dim_Date", "Date", "2024-06-30", "Last", 3, "Months");

        var and = (JsonObject)FirstPageFilter(store, sid)["filter"]!["Where"]![0]!["Condition"]!["And"]!;
        // Last 3 Months ending at the anchor: [2024-03-30, 2024-06-30)
        Assert.Equal(2, and["Left"]!["Comparison"]!["ComparisonKind"]!.GetValue<int>());   // GTE lower
        Assert.Equal(3, and["Right"]!["Comparison"]!["ComparisonKind"]!.GetValue<int>());  // LT upper (exclusive)
        Assert.Equal("datetime'2024-03-30T00:00:00'", (string?)and["Left"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);
        Assert.Equal("datetime'2024-06-30T00:00:00'", (string?)and["Right"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void FixedAnchorRelativeDate_Next2Years_BoundsForward()
    {
        var (svc, sid, store) = NewReport();
        svc.AddFixedAnchorRelativeDateFilter(sid, "page", Page, null, "D", "Date", "2024-01-01", "Next", 2, "Years");
        var and = (JsonObject)FirstPageFilter(store, sid)["filter"]!["Where"]![0]!["Condition"]!["And"]!;
        Assert.Equal("datetime'2024-01-01T00:00:00'", (string?)and["Left"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);
        Assert.Equal("datetime'2026-01-01T00:00:00'", (string?)and["Right"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);
    }

    // ====================================================================== PART 1d: visual aggregation

    [Theory]
    [InlineData("Sum", 0)]
    [InlineData("Avg", 1)]
    [InlineData("Min", 2)]
    [InlineData("Max", 3)]
    [InlineData("Count", 4)]
    [InlineData("CountNonNull", 5)]
    [InlineData("Median", 6)]
    [InlineData("StdDev", 7)]
    [InlineData("Var", 8)]
    public void EditVisualAggregation_SetsFunctionIndex(string agg, int code)
    {
        var (svc, sid, store) = NewReport(("v1", "tableEx"));
        var pq = new JsonObject
        {
            ["Version"] = 2,
            ["From"] = new JsonArray { new JsonObject { ["Name"] = "f", ["Entity"] = "Fact", ["Type"] = 0 } },
            ["Select"] = new JsonArray
            {
                new JsonObject
                {
                    ["Column"] = new JsonObject {
                        ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = "f" } }, ["Property"] = "Amount" },
                    ["Name"] = "Fact.Amount",
                },
            },
        };
        SeedQuery(store, sid, "v1", pq);

        svc.EditVisualAggregation(sid, Page, "v1", "Fact.Amount", agg);

        var sel = (JsonObject)SingleVisual(store, sid, "v1")["prototypeQuery"]!["Select"]![0]!;
        Assert.Equal(code, sel["Aggregation"]!["Function"]!.GetValue<int>());
        Assert.Equal("Fact.Amount", (string?)sel["Name"]);
        Assert.NotNull(sel["Aggregation"]!["Expression"]!["Column"]);   // inner expr preserved
        Assert.Null(sel["Column"]);                                     // raw Column unwrapped
    }

    [Fact]
    public void EditVisualAggregation_ScopedEvalBaseline_WrapsInAllRolesRef()
    {
        var (svc, sid, store) = NewReport(("v1", "tableEx"));
        var pq = new JsonObject
        {
            ["Version"] = 2,
            ["From"] = new JsonArray { new JsonObject { ["Name"] = "f", ["Entity"] = "Fact", ["Type"] = 0 } },
            ["Select"] = new JsonArray
            {
                new JsonObject
                {
                    ["Column"] = new JsonObject {
                        ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = "f" } }, ["Property"] = "Amount" },
                    ["Name"] = "Fact.Amount",
                },
            },
        };
        SeedQuery(store, sid, "v1", pq);

        svc.EditVisualAggregation(sid, Page, "v1", "Fact.Amount", "Sum", scopedEvalBaseline: true);

        var sel = (JsonObject)SingleVisual(store, sid, "v1")["prototypeQuery"]!["Select"]![0]!;
        Assert.NotNull(sel["ScopedEval"]);
        Assert.Equal(0, sel["ScopedEval"]!["Expression"]!["Aggregation"]!["Function"]!.GetValue<int>());
        Assert.NotNull(((JsonObject)sel["ScopedEval"]!["Scope"]![0]!)["AllRolesRef"]);
    }

    [Fact]
    public void EditVisualAggregation_UnknownField_Throws()
    {
        var (svc, sid, store) = NewReport(("v1", "tableEx"));
        SeedQuery(store, sid, "v1", new JsonObject { ["Version"] = 2, ["Select"] = new JsonArray() });
        Assert.Throws<InvalidOperationException>(() => svc.EditVisualAggregation(sid, Page, "v1", "Fact.Missing", "Sum"));
    }

    // ====================================================================== PART 1e: visual Top N

    [Fact]
    public void AddVisualTopN_AddsTopNodeToVisualQueryWhere()
    {
        var (svc, sid, store) = NewReport(("v1", "barChart"));
        SeedQuery(store, sid, "v1", new JsonObject
        {
            ["Version"] = 2,
            ["From"] = new JsonArray { new JsonObject { ["Name"] = "d", ["Entity"] = "Dim", ["Type"] = 0 } },
            ["Select"] = new JsonArray(),
        });

        svc.AddVisualTopN(sid, Page, "v1", "Dim", "Brand", "Fact", "Sales", 5, "Top");

        var where = (JsonArray)SingleVisual(store, sid, "v1")["prototypeQuery"]!["Where"]!;
        var top = (JsonObject)where[0]!["Condition"]!["Top"]!;
        Assert.Equal(5, top["Count"]!.GetValue<int>());
        Assert.Equal(2, top["OrderBy"]![0]!["Direction"]!.GetValue<int>());   // Top = desc
        Assert.Equal("Sales", (string?)top["OrderBy"]![0]!["Expression"]!["Measure"]!["Property"]);
        Assert.Equal("Brand", (string?)top["Expressions"]![0]!["Column"]!["Property"]);
    }

    [Fact]
    public void AddVisualTopN_Bottom_UsesAscending()
    {
        var (svc, sid, store) = NewReport(("v1", "barChart"));
        SeedQuery(store, sid, "v1", new JsonObject { ["Version"] = 2,
            ["From"] = new JsonArray { new JsonObject { ["Name"] = "d", ["Entity"] = "Dim", ["Type"] = 0 } } });
        svc.AddVisualTopN(sid, Page, "v1", "Dim", "Brand", "Fact", "Sales", 3, "Bottom");
        var top = (JsonObject)((JsonArray)SingleVisual(store, sid, "v1")["prototypeQuery"]!["Where"]!)[0]!["Condition"]!["Top"]!;
        Assert.Equal(1, top["OrderBy"]![0]!["Direction"]!.GetValue<int>());   // Bottom = asc
    }

    // ====================================================================== PART 1f: filter restatement

    [Fact]
    public void SetFilterRestatement_SetsDisplayNameAndFlags()
    {
        var (svc, sid, store) = NewReport();
        svc.AddBetweenFilter(sid, "page", Page, null, "Fact", "Sales", "measure", "1", "9", "int");
        svc.SetFilterRestatement(sid, "page", Page, null, "Fact", "Sales", "Sales 1-9", isHiddenInViewMode: true, isLockedInViewMode: true);

        var f = FirstPageFilter(store, sid);
        Assert.Equal("Sales 1-9", (string?)f["displayName"]);
        Assert.True(f["isHiddenInViewMode"]!.GetValue<bool>());
        Assert.True(f["isLockedInViewMode"]!.GetValue<bool>());
    }

    [Fact]
    public void SetFilterRestatement_NoMatch_Throws()
    {
        var (svc, sid, _) = NewReport();
        Assert.Throws<InvalidOperationException>(() =>
            svc.SetFilterRestatement(sid, "page", Page, null, "Fact", "Nope", "x", null, null));
    }

    // ====================================================================== PART 2: Deneb / Vega-Lite

    private const string Spec = "{\"$schema\":\"https://vega.github.io/schema/vega-lite/v5.json\"," +
        "\"mark\":\"bar\",\"encoding\":{\"x\":{\"field\":\"cat\",\"type\":\"nominal\"}," +
        "\"y\":{\"field\":\"val\",\"type\":\"quantitative\"}},\"title\":\"O'Reilly's \\\"chart\\\"\"}";

    [Fact]
    public void AddDenebVisual_WritesProviderRenderModeAndEnables()
    {
        var (svc, sid, store) = NewReport();
        var result = svc.AddDenebVisual(sid, Page, "MyDeneb", Spec, "vegaLite", "svg", null,
            enableTooltips: true, enableSelection: false, enableHighlight: true, 10, 10, 400, 300, null, null);

        string id = (string?)(JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject)!["visualName"] ?? "";
        var sv = SingleVisual(store, sid, id);
        var props = (JsonObject)((JsonArray)sv["objects"]!["vega"]!)[0]!["properties"]!;

        Assert.Equal("'vegaLite'", (string?)props["provider"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'svg'", (string?)props["renderMode"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("true", (string?)props["enableTooltips"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("false", (string?)props["enableSelection"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("true", (string?)props["enableHighlight"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddDenebVisual_JsonSpec_RoundTrips_BackToExactSpec()
    {
        var (svc, sid, store) = NewReport();
        var result = svc.AddDenebVisual(sid, Page, "MyDeneb", Spec, "vega", "canvas", null,
            true, true, false, 0, 0, 0, 0, null, null);
        string id = (string?)(JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject)!["visualName"] ?? "";

        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["objects"]!["vega"]!)[0]!["properties"]!;
        var jsonSpec = (JsonObject)props["jsonSpec"]!;

        // decode the embedded literal back to JSON and assert it parses to the SAME spec (key-for-key).
        string embedded = ReportService.DenebDecodeJsonLiteral(jsonSpec);
        var roundTripped = JsonNode.Parse(embedded);
        var original = JsonNode.Parse(Spec);
        Assert.Equal(original!.ToJsonString(), roundTripped!.ToJsonString());
        // the title carried both a single quote and escaped double quotes - they survived.
        Assert.Equal("O'Reilly's \"chart\"", (string?)roundTripped!["title"]);
    }

    [Fact]
    public void DenebLiteral_FaultSensitive_QuoteWrappingIsLoadBearing()
    {
        // the #1 failure mode is mangling the single-quote wrapping / doubling. Prove the round-trip is
        // sensitive to it: (a) a NON-quote-wrapped value cannot be decoded (the wrapping is load-bearing);
        // (b) a value with an internal ' that was NOT doubled decodes to DIFFERENT content than the engine's
        // correctly-doubled encoding.
        var unwrapped = new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject {
            ["Value"] = "{\"a\":1}" } } };   // missing the wrapping single quotes
        Assert.Throws<ArgumentException>(() => ReportService.DenebDecodeJsonLiteral(unwrapped));

        // a spec that contains an internal single quote: the engine doubles it on encode. A hand-built literal
        // that left it single ('...O'X...') would, on decode, un-double nothing and so recover the WRONG text.
        var (svc, sid, store) = NewReport();
        var specWithQuote = "{\"label\":\"O'X\"}";
        var result = svc.AddDenebVisual(sid, Page, "Q", specWithQuote, "vegaLite", "svg", null,
            true, true, false, 0, 0, 0, 0, null, null);
        string id = (string?)(JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject)!["visualName"] ?? "";
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["objects"]!["vega"]!)[0]!["properties"]!;
        string stored = (string?)props["jsonSpec"]!["expr"]!["Literal"]!["Value"] ?? "";
        Assert.Contains("O''X", stored);                  // the engine doubled the internal quote
        string decoded = ReportService.DenebDecodeJsonLiteral((JsonObject)props["jsonSpec"]!);
        Assert.Equal(JsonNode.Parse(specWithQuote)!.ToJsonString(), JsonNode.Parse(decoded)!.ToJsonString());
    }

    [Fact]
    public void AddDenebVisual_BindsDataRolesIntoValues()
    {
        var (svc, sid, store) = NewReport();
        var binds = new[] { new FieldBinding("values", "Fact", "Amount", "measure") };
        var result = svc.AddDenebVisual(sid, Page, "D", Spec, "vegaLite", "svg", null,
            true, true, false, 0, 0, 0, 0, binds, null);
        string id = (string?)(JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject)!["visualName"] ?? "";

        var sv = SingleVisual(store, sid, id);
        var proj = (JsonArray)sv["projections"]!["values"]!;
        Assert.Equal("Fact.Amount", (string?)proj[0]!["queryRef"]);
    }

    [Fact]
    public void AddDenebVisual_RegistersGuid_AndBadProviderThrows()
    {
        var (svc, sid, store) = NewReport();
        svc.AddDenebVisual(sid, Page, "D", Spec, "vega", "svg", null, true, true, false, 0, 0, 0, 0, null, null);
        var cfg = JsonNode.Parse((string)Root(store, sid)["config"]!) as JsonObject;
        var pub = (JsonArray)cfg!["publicCustomVisuals"]!;
        Assert.Contains(pub.OfType<JsonValue>(), v => (string?)v == ReportService.DenebDefaultGuid);

        Assert.Throws<ArgumentException>(() =>
            svc.AddDenebVisual(sid, Page, "D2", Spec, "bogus", "svg", null, true, true, false, 0, 0, 0, 0, null, null));
    }

    [Fact]
    public void AddDenebVisual_InvalidSpecJson_Throws()
    {
        var (svc, sid, _) = NewReport();
        Assert.Throws<ArgumentException>(() =>
            svc.AddDenebVisual(sid, Page, "D", "{not json", "vegaLite", "svg", null, true, true, false, 0, 0, 0, 0, null, null));
    }

    // ====================================================================== PART 3: DataMashup M extract + edit

    private const string M =
        "section Section1;\r\n" +
        "shared Sales = let Source = Sql.Database(\"OLDSERVER\", \"DB\") in Source;\r\n" +
        "shared #\"Date Table\" = let s = 1 in s;\r\n";

    private static string NewTempPbixWithMashup(string m, bool withBindings)
    {
        string dir = Path.Combine(
            Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath(),
            "waveS-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string pbix = Path.Combine(dir, "fake.pbix");
        byte[] mashup = ReportService.BuildMashupContainerForTest(m, withBindings);

        using var fs = new FileStream(pbix, FileMode.Create);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        void Add(string name, byte[] bytes)
        {
            var e = zip.CreateEntry(name);
            using var s = e.Open(); s.Write(bytes, 0, bytes.Length);
        }
        Add("[Content_Types].xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"></Types>"));
        Add("DataMashup", mashup);
        // a stub Report/Layout so the file looks like a .pbix (not strictly needed for the mashup tools).
        Add("Report/Layout", new UnicodeEncoding(false, false).GetBytes("{\"sections\":[]}"));
        return pbix;
    }

    private static void Cleanup(string pbix)
    {
        try { var d = Path.GetDirectoryName(pbix)!; if (File.Exists(pbix)) File.Delete(pbix); Directory.Delete(d, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void QdeffContainer_RoundTrips_ReadsBackTheM()
    {
        // synthetic QDEFF container round-trip via the engine's own (internal) helpers.
        byte[] container = ReportService.BuildMashupContainerForTest(M, withBindings: false);
        string? read = ReportService.ReadSection1MFromContainerForTest(container);
        Assert.Equal(M, read);
        Assert.False(ReportService.MashupContainerHasPermissionBindingsForTest(container));

        byte[] withB = ReportService.BuildMashupContainerForTest(M, withBindings: true);
        Assert.True(ReportService.MashupContainerHasPermissionBindingsForTest(withB));
    }

    [Fact]
    public void GetDataMashupInfo_ListsQueryNames()
    {
        var (svc, _, _) = NewReport();
        string pbix = NewTempPbixWithMashup(M, withBindings: true);
        try
        {
            var res = JsonNode.Parse(JsonSerializer.Serialize(svc.GetDataMashupInfo(pbix))) as JsonObject;
            Assert.True(res!["hasDataMashup"]!.GetValue<bool>());
            var queries = (JsonArray)res["queries"]!;
            var names = queries.Select(q => (string?)q).ToList();
            Assert.Contains("Sales", names);
            Assert.Contains("Date Table", names);
            Assert.True(res["hasPermissionBindings"]!.GetValue<bool>());
        }
        finally { Cleanup(pbix); }
    }

    [Fact]
    public void ExtractPowerQuery_ReturnsFullSection1M()
    {
        var (svc, _, _) = NewReport();
        string pbix = NewTempPbixWithMashup(M, withBindings: false);
        try
        {
            var res = JsonNode.Parse(JsonSerializer.Serialize(svc.ExtractPowerQuery(pbix))) as JsonObject;
            Assert.True(res!["hasDataMashup"]!.GetValue<bool>());
            Assert.Equal(M, (string?)res["section1M"]);
        }
        finally { Cleanup(pbix); }
    }

    [Fact]
    public void UpdatePowerQuery_RewritesM_AndRoundTripReadsTheNewM()
    {
        var (svc, _, _) = NewReport();
        string pbix = NewTempPbixWithMashup(M, withBindings: true);
        try
        {
            string newM = "section Section1;\r\nshared Sales = let Source = 1 in Source;\r\n";
            var res = JsonNode.Parse(JsonSerializer.Serialize(svc.UpdatePowerQuery(pbix, newM))) as JsonObject;
            Assert.True(res!["clearedPermissionBindings"]!.GetValue<bool>());

            // round-trip: re-read the M from the rewritten .pbix.
            var read = JsonNode.Parse(JsonSerializer.Serialize(svc.ExtractPowerQuery(pbix))) as JsonObject;
            Assert.Equal(newM, (string?)read!["section1M"]);
        }
        finally { Cleanup(pbix); }
    }

    [Fact]
    public void RewriteConnectionString_ReplacesAndClearsBindings()
    {
        var (svc, _, _) = NewReport();
        string pbix = NewTempPbixWithMashup(M, withBindings: true);
        try
        {
            var res = JsonNode.Parse(JsonSerializer.Serialize(svc.RewriteConnectionString(pbix, "OLDSERVER", "NEWSERVER"))) as JsonObject;
            Assert.Equal(1, res!["replaced"]!.GetValue<int>());
            Assert.True(res["clearedPermissionBindings"]!.GetValue<bool>());

            var read = JsonNode.Parse(JsonSerializer.Serialize(svc.ExtractPowerQuery(pbix))) as JsonObject;
            string m = (string?)read!["section1M"] ?? "";
            Assert.Contains("NEWSERVER", m);
            Assert.DoesNotContain("OLDSERVER", m);
        }
        finally { Cleanup(pbix); }
    }

    [Fact]
    public void GetDataMashupInfo_NoMashupPart_PointsAtTmdlTools()
    {
        var (svc, _, _) = NewReport();
        // a .pbix-like zip with NO DataMashup part.
        string dir = Path.Combine(
            Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath(),
            "waveS-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string pbix = Path.Combine(dir, "nomashup.pbix");
        using (var fs = new FileStream(pbix, FileMode.Create))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("Report/Layout"); using var s = e.Open();
            var b = new UnicodeEncoding(false, false).GetBytes("{\"sections\":[]}"); s.Write(b, 0, b.Length);
        }
        try
        {
            var res = JsonNode.Parse(JsonSerializer.Serialize(svc.GetDataMashupInfo(pbix))) as JsonObject;
            Assert.False(res!["hasDataMashup"]!.GetValue<bool>());
            Assert.Contains("set_partition_m", (string?)res["note"] ?? "");
        }
        finally { try { File.Delete(pbix); Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RewriteConnectionString_NotFound_ReplacesNothing()
    {
        var (svc, _, _) = NewReport();
        string pbix = NewTempPbixWithMashup(M, withBindings: false);
        try
        {
            var res = JsonNode.Parse(JsonSerializer.Serialize(svc.RewriteConnectionString(pbix, "ZZZ-NOPE", "x"))) as JsonObject;
            Assert.Equal(0, res!["replaced"]!.GetValue<int>());
        }
        finally { Cleanup(pbix); }
    }
}
