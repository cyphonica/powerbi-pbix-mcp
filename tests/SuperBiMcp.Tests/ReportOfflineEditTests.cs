using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the OFFLINE report-visual edit engine (ReportService.UpdateVisualProperty / SetSlicerSelection and
/// their one-shot *Offline wrappers) - the general "edit a report visual" capability and the targeted slicer
/// single-select + default-selection fix (the field-parameter flat-lines bug).
///
/// The in-memory tests run over the SAME JsonObject root Open() produces (config is a STRINGIFIED blob), so
/// they need no live .pbix. The round-trip test builds a synthetic .pbix (legacy Report/Layout UTF-16-LE +
/// a DataModel part + a SecurityBindings signature) and asserts the offline save patches ONLY Report/Layout,
/// carries the DataModel through byte-for-byte, and drops the signature.
/// </summary>
public sealed class ReportOfflineEditTests
{
    private const string Page = "Category vs Brand Share";

    // ---- fixture builders (the exact shape the engine reads/writes) ------------------------------

    // a slicer bound to one column, carrying a visible TITLE in vcObjects (like the real 'Chart Period' slicer).
    private static JsonObject SlicerContainer(string id, string table, string field, string? title)
    {
        var sv = new JsonObject
        {
            ["visualType"] = "slicer",
            ["projections"] = new JsonObject { ["Values"] = new JsonArray { new JsonObject { ["queryRef"] = $"{table}.{field}" } } },
            ["prototypeQuery"] = new JsonObject
            {
                ["Version"] = 2,
                ["From"] = new JsonArray { new JsonObject { ["Name"] = "t1", ["Entity"] = table, ["Type"] = 0 } },
                ["Select"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Column"] = new JsonObject
                        {
                            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = "t1" } },
                            ["Property"] = field,
                        },
                        ["Name"] = $"{table}.{field}",
                    },
                },
            },
            ["drillFilterOtherVisuals"] = true,
            ["objects"] = new JsonObject
            {
                ["data"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                    { ["mode"] = Lit("'Dropdown'") } } },
            },
        };
        if (title != null)
            sv["vcObjects"] = new JsonObject
            {
                ["title"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                    { ["text"] = Lit($"'{title}'"), ["show"] = Lit("true") } } },
            };
        return Container(id, sv);
    }

    private static JsonObject ChartContainer(string id, string type, string title)
        => Container(id, new JsonObject
        {
            ["visualType"] = type,
            ["drillFilterOtherVisuals"] = true,
            ["vcObjects"] = new JsonObject
            {
                ["title"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                    { ["text"] = Lit($"'{title}'"), ["show"] = Lit("true") } } },
            },
        });

    private static JsonObject Lit(string value) =>
        new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = value } } };

    private static JsonObject Container(string id, JsonObject singleVisual)
    {
        var config = new JsonObject
        {
            ["name"] = id,
            ["layouts"] = new JsonArray { new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                { ["x"] = 16, ["y"] = 16, ["z"] = 0, ["width"] = 200, ["height"] = 56, ["tabOrder"] = 0 } } },
            ["singleVisual"] = singleVisual,
        };
        return new JsonObject
        {
            ["x"] = 16, ["y"] = 16, ["z"] = 0, ["width"] = 200, ["height"] = 56,
            ["config"] = config.ToJsonString(),
            ["filters"] = "[]",
        };
    }

    private static (ReportService svc, string sid, SessionStore store) NewReport(params JsonObject[] containers)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var arr = new JsonArray();
        foreach (var c in containers) arr.Add(c);
        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 20),
            ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = arr,
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720,
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

    // ---- inspection helpers ----
    private static JsonObject Section(SessionStore store, string sid) =>
        ((JsonArray)store.GetReport(sid).Layout.Root["sections"]!).OfType<JsonObject>().First();

    private static (JsonObject co, JsonObject vc) Visual(SessionStore store, string sid, string id)
    {
        var vc = ((JsonArray)Section(store, sid)["visualContainers"]!).OfType<JsonObject>()
            .First(v => (string?)JsonNode.Parse((string)v["config"]!)!["name"] == id);
        return ((JsonObject)JsonNode.Parse((string)vc["config"]!)!, vc);
    }

    // ================================================================= update_visual_property

    [Fact]
    public void UpdateVisualProperty_SetsScalarByPath_RoundTripsThroughConfig()
    {
        var (svc, sid, store) = NewReport(ChartContainer("v1", "lineChart", "Trend"));

        svc.UpdateVisualProperty(sid, Page, "v1", "drillFilterOtherVisuals", "false", "auto");

        var (co, _) = Visual(store, sid, "v1");
        // set as a real JSON bool (auto coercion), not the string "false"
        Assert.False(co["singleVisual"]!["drillFilterOtherVisuals"]!.GetValue<bool>());
    }

    [Fact]
    public void UpdateVisualProperty_CreatesNestedPath_AndLiteralKindWrapsAsExpr()
    {
        var (svc, sid, store) = NewReport(ChartContainer("v1", "lineChart", "Trend"));

        // path with a fresh object + array index; bool kind -> {expr:{Literal:{Value:"true"}}}
        svc.UpdateVisualProperty(sid, Page, "v1", "objects.custom[0].properties.show", "true", "bool");

        var (co, _) = Visual(store, sid, "v1");
        var val = co["singleVisual"]!["objects"]!["custom"]![0]!["properties"]!["show"]!["expr"]!["Literal"]!["Value"];
        Assert.Equal("true", (string?)val);
    }

    [Fact]
    public void UpdateVisualProperty_JsonKind_SetsStructuredValueVerbatim()
    {
        var (svc, sid, store) = NewReport(ChartContainer("v1", "lineChart", "Trend"));

        svc.UpdateVisualProperty(sid, Page, "v1", "display.mode", "{\"a\":1}", "json");

        var (co, _) = Visual(store, sid, "v1");
        Assert.Equal(1, co["singleVisual"]!["display"]!["mode"]!["a"]!.GetValue<int>());
    }

    [Fact]
    public void UpdateVisualProperty_LocateByTitle_Works()
    {
        var (svc, sid, store) = NewReport(ChartContainer("v1", "lineChart", "Brand Performance by Segment"));

        // resolve by the visible TITLE, not the config name
        svc.UpdateVisualProperty(sid, Page, "Brand Performance by Segment", "drillFilterOtherVisuals", "false", "auto");

        var (co, _) = Visual(store, sid, "v1");
        Assert.False(co["singleVisual"]!["drillFilterOtherVisuals"]!.GetValue<bool>());
    }

    [Fact]
    public void UpdateVisualProperty_ReturnsBeforeAndAfter()
    {
        var (svc, sid, _) = NewReport(ChartContainer("v1", "lineChart", "Trend"));

        var r = Obj(svc.UpdateVisualProperty(sid, Page, "v1", "drillFilterOtherVisuals", "false", "auto"));

        Assert.True((bool)r["ok"]!);
        Assert.Equal("true", (string?)r["before"]);   // was true
        Assert.Equal("false", (string?)r["after"]);
    }

    // ================================================================= set_slicer_selection

    [Fact]
    public void SetSlicerSelection_SetsStrictSingleSelect_GroundTruthEncoding()
    {
        var (svc, sid, store) = NewReport(SlicerContainer("s1", "Brand Chart Axis", "Brand Chart Axis", "Chart Period"));

        svc.SetSlicerSelection(sid, Page, "Chart Period", singleSelect: true, defaultValue: null);

        var (co, _) = Visual(store, sid, "s1");
        var strict = co["singleVisual"]!["objects"]!["selection"]![0]!["properties"]!["strictSingleSelect"]!["expr"]!["Literal"]!["Value"];
        Assert.Equal("true", (string?)strict);
    }

    [Fact]
    public void SetSlicerSelection_LocateByBoundField_Works()
    {
        var (svc, sid, store) = NewReport(SlicerContainer("s1", "Brand Chart Axis", "Brand Chart Axis", null));

        // resolve the slicer by its bound field (no title, no displayName)
        svc.SetSlicerSelection(sid, Page, "Brand Chart Axis", singleSelect: true, defaultValue: null);

        var (co, _) = Visual(store, sid, "s1");
        Assert.NotNull(co["singleVisual"]!["objects"]!["selection"]);
    }

    [Fact]
    public void SetSlicerSelection_DefaultValue_WritesCategoricalSelectionFilter()
    {
        var (svc, sid, store) = NewReport(SlicerContainer("s1", "Brand Chart Axis", "Brand Chart Axis", "Chart Period"));

        svc.SetSlicerSelection(sid, Page, "Chart Period", singleSelect: true, defaultValue: "MonthYear");

        var (_, vc) = Visual(store, sid, "s1");
        var filters = JsonNode.Parse((string)vc["filters"]!) as JsonArray;
        Assert.NotNull(filters);
        var f = filters!.OfType<JsonObject>().Single();
        Assert.Equal("Categorical", (string?)f["type"]);
        Assert.Equal("Brand Chart Axis", (string?)f["expression"]!["Column"]!["Expression"]!["SourceRef"]!["Entity"]);
        var val = f["filter"]!["Where"]![0]!["Condition"]!["In"]!["Values"]![0]![0]!["Literal"]!["Value"];
        Assert.Equal("'MonthYear'", (string?)val);
    }

    [Fact]
    public void SetSlicerSelection_DefaultValue_IsIdempotent_NoDuplicateFilters()
    {
        var (svc, sid, store) = NewReport(SlicerContainer("s1", "Brand Chart Axis", "Brand Chart Axis", "Chart Period"));

        svc.SetSlicerSelection(sid, Page, "Chart Period", true, "MonthYear");
        svc.SetSlicerSelection(sid, Page, "Chart Period", true, "Year");   // re-run with a different default

        var (_, vc) = Visual(store, sid, "s1");
        var filters = (JsonArray)JsonNode.Parse((string)vc["filters"]!)!;
        Assert.Single(filters);   // prior selection on the SAME field replaced, not stacked
        var val = filters[0]!["filter"]!["Where"]![0]!["Condition"]!["In"]!["Values"]![0]![0]!["Literal"]!["Value"];
        Assert.Equal("'Year'", (string?)val);
    }

    [Fact]
    public void SetSlicerSelection_OnNonSlicer_Throws()
    {
        var (svc, sid, _) = NewReport(ChartContainer("v1", "lineChart", "Trend"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            svc.SetSlicerSelection(sid, Page, "Trend", true, null));
        Assert.Contains("not a slicer", ex.Message);
    }

    [Fact]
    public void ResolveVisual_MissingVisual_ThrowsAndListsWhatIsPresent()
    {
        var (svc, sid, _) = NewReport(SlicerContainer("s1", "T", "F", "Chart Period"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            svc.UpdateVisualProperty(sid, Page, "Nope", "drillFilterOtherVisuals", "false", "auto"));
        Assert.Contains("not found", ex.Message);
        Assert.Contains("Chart Period", ex.Message);   // the helpful "visuals present" list
    }

    [Fact]
    public void ResolveVisual_MissingPage_Throws()
    {
        var (svc, sid, _) = NewReport(SlicerContainer("s1", "T", "F", "Chart Period"));
        Assert.Throws<InvalidOperationException>(() =>
            svc.UpdateVisualProperty(sid, "No Such Page", "Chart Period", "drillFilterOtherVisuals", "false", "auto"));
    }

    // ================================================================= fix_slicer_single_select (ONE-CALL)

    // a chart whose CATEGORY axis is bound to a field-parameter column (table == column), like the real
    // 'Category vs Brand Share' line chart whose axis is the 'Brand Chart Axis' field parameter.
    private static JsonObject FieldParamChart(string id, string type, string title, string fpTable, string measureTable, string measure)
    {
        var sv = new JsonObject
        {
            ["visualType"] = type,
            ["projections"] = new JsonObject
            {
                ["Category"] = new JsonArray { new JsonObject { ["queryRef"] = $"{fpTable}.{fpTable}" } },
                ["Y"] = new JsonArray { new JsonObject { ["queryRef"] = $"{measureTable}.{measure}" } },
            },
            ["prototypeQuery"] = new JsonObject
            {
                ["Version"] = 2,
                ["From"] = new JsonArray
                {
                    new JsonObject { ["Name"] = "t1", ["Entity"] = fpTable, ["Type"] = 0 },
                    new JsonObject { ["Name"] = "t2", ["Entity"] = measureTable, ["Type"] = 0 },
                },
                ["Select"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Column"] = new JsonObject
                        {
                            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = "t1" } },
                            ["Property"] = fpTable,
                        },
                        ["Name"] = $"{fpTable}.{fpTable}",
                    },
                    new JsonObject
                    {
                        ["Measure"] = new JsonObject
                        {
                            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = "t2" } },
                            ["Property"] = measure,
                        },
                        ["Name"] = $"{measureTable}.{measure}",
                    },
                },
            },
            ["vcObjects"] = new JsonObject
            {
                ["title"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                    { ["text"] = Lit($"'{title}'"), ["show"] = Lit("true") } } },
            },
        };
        return Container(id, sv);
    }

    private static JsonObject StrictSelect(JsonObject sv)
    {
        // stamp objects.selection[0].properties.strictSingleSelect = true onto a slicer container's config sv
        sv["objects"] = new JsonObject
        {
            ["selection"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                { ["strictSingleSelect"] = Lit("true") } } },
        };
        return sv;
    }

    [Fact]
    public void FixSlicerSingleSelect_AutoFindsFieldParamSlicer_WithOnlyPage()
    {
        // page carries a field-param chart, its field-param slicer, and two ordinary dimension slicers.
        var (svc, sid, store) = NewReport(
            FieldParamChart("chart", "lineChart", "Category vs Brand Share", "Brand Chart Axis", "Fact_Sales", "Total Dollars"),
            SlicerContainer("fp", "Brand Chart Axis", "Brand Chart Axis", null),
            SlicerContainer("d1", "Dim_Products", "Brand", "Brand"),
            SlicerContainer("d2", "Dim_Stores", "STORE_REGION", "Region"));

        // ONLY {page} - no slicer name, no boolean.
        var r = Obj(svc.FixSlicerSingleSelect(sid, Page, null, null));

        Assert.True((bool)r["ok"]!);
        Assert.Equal("Brand Chart Axis", (string?)r["fieldParameter"]);
        Assert.Equal("fp", (string?)r["slicer"]);   // picked the field-param slicer, not the Brand/Region ones
        Assert.True((bool)r["strictSingleSelect"]!);

        var (co, _) = Visual(store, sid, "fp");
        var strict = co["singleVisual"]!["objects"]!["selection"]![0]!["properties"]!["strictSingleSelect"]!["expr"]!["Literal"]!["Value"];
        Assert.Equal("true", (string?)strict);
        // the ordinary dimension slicers are untouched (no strictSingleSelect selection written)
        var (d1, _) = Visual(store, sid, "d1");
        Assert.Null(d1["singleVisual"]!["objects"]!["selection"]);
    }

    [Fact]
    public void FixSlicerSingleSelect_LoosePageMatch_Works()
    {
        var (svc, sid, _) = NewReport(
            FieldParamChart("chart", "lineChart", "Trend", "Brand Chart Axis", "Fact_Sales", "Total Dollars"),
            SlicerContainer("fp", "Brand Chart Axis", "Brand Chart Axis", null));

        // partial, differently-cased page name still resolves the page ("Category vs Brand Share")
        var r = Obj(svc.FixSlicerSingleSelect(sid, "brand share", null, null));
        Assert.True((bool)r["ok"]!);
        Assert.Equal("fp", (string?)r["slicer"]);
    }

    [Fact]
    public void FixSlicerSingleSelect_DefaultValue_LandsMonthYearSelection()
    {
        var (svc, sid, store) = NewReport(
            FieldParamChart("chart", "lineChart", "Category vs Brand Share", "Brand Chart Axis", "Fact_Sales", "Total Dollars"),
            SlicerContainer("fp", "Brand Chart Axis", "Brand Chart Axis", null));

        var r = Obj(svc.FixSlicerSingleSelect(sid, Page, null, "MonthYear"));
        Assert.True((bool)r["default_applied"]!);

        var (_, vc) = Visual(store, sid, "fp");
        var filters = (JsonArray)JsonNode.Parse((string)vc["filters"]!)!;
        var val = filters[0]!["filter"]!["Where"]![0]!["Condition"]!["In"]!["Values"]![0]![0]!["Literal"]!["Value"];
        Assert.Equal("'MonthYear'", (string?)val);
    }

    [Fact]
    public void FixSlicerSingleSelect_SeveralFieldParams_PicksTheOneFeedingTheChart_ViaHint()
    {
        // two field-param slicers; only 'Brand Chart Axis' feeds the line chart's axis. The other ('Extra Axis')
        // is a field param (table==column) not plotted on any chart. Pass the chart to disambiguate.
        var (svc, sid, store) = NewReport(
            FieldParamChart("chart", "lineChart", "Category vs Brand Share", "Brand Chart Axis", "Fact_Sales", "Total Dollars"),
            SlicerContainer("fp1", "Brand Chart Axis", "Brand Chart Axis", null),
            SlicerContainer("fp2", "Extra Axis", "Extra Axis", null));

        var r = Obj(svc.FixSlicerSingleSelect(sid, Page, "Category vs Brand Share", null));
        Assert.True((bool)r["ok"]!);
        Assert.Equal("fp1", (string?)r["slicer"]);

        var (co, _) = Visual(store, sid, "fp1");
        Assert.NotNull(co["singleVisual"]!["objects"]!["selection"]);
    }

    [Fact]
    public void FixSlicerSingleSelect_SeveralFieldParams_Ambiguous_ThrowsAndListsSlicers()
    {
        // two field-param slicers, neither already single-select, and NO chart hint -> ambiguous.
        var (svc, sid, _) = NewReport(
            SlicerContainer("fp1", "Brand Chart Axis", "Brand Chart Axis", "Axis A"),
            SlicerContainer("fp2", "Extra Axis", "Extra Axis", "Axis B"));

        var ex = Assert.Throws<InvalidOperationException>(() => svc.FixSlicerSingleSelect(sid, Page, null, null));
        Assert.Contains("ambiguous", ex.Message);
        Assert.Contains("Axis A", ex.Message);
        Assert.Contains("Axis B", ex.Message);
    }

    [Fact]
    public void FixSlicerSingleSelect_SkipsFieldParamAlreadySingleSelect_PicksTheFlatLiner()
    {
        // 'ShareMetric' field param already single-select (not the problem); 'Brand Chart Axis' is the flat-liner.
        var alreadySingle = SlicerContainer("done", "ShareMetric", "ShareMetric", "Metric");
        var coDone = (JsonObject)JsonNode.Parse((string)alreadySingle["config"]!)!;
        StrictSelect((JsonObject)coDone["singleVisual"]!);
        alreadySingle["config"] = coDone.ToJsonString();

        var (svc, sid, store) = NewReport(
            FieldParamChart("chart", "lineChart", "Category vs Brand Share", "Brand Chart Axis", "Fact_Sales", "Total Dollars"),
            alreadySingle,
            SlicerContainer("fp", "Brand Chart Axis", "Brand Chart Axis", null));

        var r = Obj(svc.FixSlicerSingleSelect(sid, Page, null, null));
        Assert.Equal("fp", (string?)r["slicer"]);
        var (co, _) = Visual(store, sid, "fp");
        Assert.NotNull(co["singleVisual"]!["objects"]!["selection"]);
    }

    [Fact]
    public void FixSlicerSingleSelect_NoFieldParam_Throws()
    {
        var (svc, sid, _) = NewReport(
            SlicerContainer("d1", "Dim_Products", "Brand", "Brand"),
            SlicerContainer("d2", "Dim_Stores", "STORE_REGION", "Region"));

        var ex = Assert.Throws<InvalidOperationException>(() => svc.FixSlicerSingleSelect(sid, Page, null, null));
        Assert.Contains("No field-parameter slicer", ex.Message);
    }

    [Fact]
    public void FixSlicerSingleSelectOffline_LandsStrictSingleSelect_PreservesDataModel()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sbimcp-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string pbix = Path.Combine(dir, "fixture.pbix");
        byte[] dataModel = Encoding.ASCII.GetBytes("VERTIPAQ-BINARY-BLOB-" + new string('Z', 400));
        try
        {
            WriteFixturePbix(pbix, dataModel);
            var store = new SessionStore();
            var svc = new ReportService(store, NullLogger<ReportService>.Instance);

            // ONE call, ONLY {pbix, page} - the exact shape the local model can emit.
            var r = Obj(svc.FixSlicerSingleSelectOffline(pbix, Page, null, null));
            Assert.True((bool)r["ok"]!);
            Assert.True((bool)r["persistedToDisk"]!);

            using var zip = ZipFile.OpenRead(pbix);
            Assert.Equal(dataModel, ReadEntry(zip, "DataModel"));       // DataModel byte-for-byte
            var layout = ReadLayout(zip);
            var sv = FindSlicer(layout, "Chart Period");
            var strict = sv.co["singleVisual"]!["objects"]!["selection"]![0]!["properties"]!["strictSingleSelect"]!["expr"]!["Literal"]!["Value"];
            Assert.Equal("true", (string?)strict);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ================================================================= full OFFLINE round-trip

    [Fact]
    public void SetSlicerSelectionOffline_PatchesLayout_PreservesDataModel_DropsSignature()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sbimcp-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string pbix = Path.Combine(dir, "fixture.pbix");
        byte[] dataModel = Encoding.ASCII.GetBytes("VERTIPAQ-BINARY-BLOB-" + new string('Z', 500));
        try
        {
            WriteFixturePbix(pbix, dataModel);
            var store = new SessionStore();
            var svc = new ReportService(store, NullLogger<ReportService>.Instance);

            var r = Obj(svc.SetSlicerSelectionOffline(pbix, Page, "Chart Period", true, "MonthYear"));
            Assert.True((bool)r["ok"]!);
            Assert.True((bool)r["persistedToDisk"]!);

            using var zip = ZipFile.OpenRead(pbix);
            // (1) DataModel carried through byte-for-byte
            var dm = ReadEntry(zip, "DataModel");
            Assert.Equal(dataModel, dm);
            // (2) SecurityBindings signature dropped (it no longer matches the patched report)
            Assert.Null(zip.GetEntry("SecurityBindings"));
            // (3) the patched Report/Layout carries strictSingleSelect + the MonthYear selection
            var layout = ReadLayout(zip);
            var sv = FindSlicer(layout, "Chart Period");
            var strict = sv.co["singleVisual"]!["objects"]!["selection"]![0]!["properties"]!["strictSingleSelect"]!["expr"]!["Literal"]!["Value"];
            Assert.Equal("true", (string?)strict);
            var filters = (JsonArray)JsonNode.Parse((string)sv.vc["filters"]!)!;
            var val = filters[0]!["filter"]!["Where"]![0]!["Condition"]!["In"]!["Values"]![0]![0]!["Literal"]!["Value"];
            Assert.Equal("'MonthYear'", (string?)val);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void UpdateVisualPropertyOffline_PatchesLayout_PreservesDataModel()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sbimcp-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string pbix = Path.Combine(dir, "fixture.pbix");
        byte[] dataModel = Encoding.ASCII.GetBytes("VERTIPAQ-" + new string('Q', 300));
        try
        {
            WriteFixturePbix(pbix, dataModel);
            var store = new SessionStore();
            var svc = new ReportService(store, NullLogger<ReportService>.Instance);

            svc.UpdateVisualPropertyOffline(pbix, Page, "Chart Period", "drillFilterOtherVisuals", "false", "auto");

            using var zip = ZipFile.OpenRead(pbix);
            Assert.Equal(dataModel, ReadEntry(zip, "DataModel"));
            var layout = ReadLayout(zip);
            var sv = FindSlicer(layout, "Chart Period");
            Assert.False(sv.co["singleVisual"]!["drillFilterOtherVisuals"]!.GetValue<bool>());
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ---- synthetic-pbix helpers ----

    private void WriteFixturePbix(string pbix, byte[] dataModel)
    {
        // reuse the same in-memory layout the fixture builders produce
        var (_, sid, store) = NewReport(SlicerContainer("s1", "Brand Chart Axis", "Brand Chart Axis", "Chart Period"));
        var root = store.GetReport(sid).Layout.Root;
        byte[] layoutBytes = new UnicodeEncoding(false, false).GetBytes(root.ToJsonString());

        using var fs = new FileStream(pbix, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Write(string name, byte[] bytes, CompressionLevel lvl)
        {
            var e = zip.CreateEntry(name, lvl); using var s = e.Open(); s.Write(bytes, 0, bytes.Length);
        }
        Write("Version", Encoding.ASCII.GetBytes("1.0"), CompressionLevel.Optimal);
        Write("Report/Layout", layoutBytes, CompressionLevel.Optimal);
        Write("DataModel", dataModel, CompressionLevel.NoCompression);
        Write("[Content_Types].xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"json\" ContentType=\"application/json\" />" +
            "<Override PartName=\"/SecurityBindings\" ContentType=\"application/x-ms-securitybindings\" /></Types>"),
            CompressionLevel.Optimal);
        Write("SecurityBindings", new byte[] { 9, 9, 9 }, CompressionLevel.Optimal);
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    private static JsonObject ReadLayout(ZipArchive zip)
    {
        byte[] b = ReadEntry(zip, "Report/Layout");
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) b = b[2..];
        return (JsonObject)JsonNode.Parse(new UnicodeEncoding(false, true).GetString(b))!;
    }

    private static (JsonObject co, JsonObject vc) FindSlicer(JsonObject layout, string title)
    {
        var section = ((JsonArray)layout["sections"]!).OfType<JsonObject>()
            .First(s => (string?)s["displayName"] == Page);
        foreach (var vcn in (JsonArray)section["visualContainers"]!)
        {
            var vc = (JsonObject)vcn!;
            var co = (JsonObject)JsonNode.Parse((string)vc["config"]!)!;
            var t = co["singleVisual"]?["vcObjects"]?["title"]?[0]?["properties"]?["text"]?["expr"]?["Literal"]?["Value"]?.GetValue<string>();
            if (t != null && t.Trim('\'') == title) return (co, vc);
        }
        throw new Xunit.Sdk.XunitException($"slicer '{title}' not found in saved layout");
    }

    private static JsonObject Obj(object result) =>
        JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject ?? new JsonObject();
}
