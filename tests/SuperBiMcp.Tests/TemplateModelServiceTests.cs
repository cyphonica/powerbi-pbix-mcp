using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using SuperBiMcp.Tools;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Headless tests for OFFLINE template-model editing (<see cref="TemplateModelService"/>): read + edit a
/// closed .pbit template's model by editing its plain-JSON DataModelSchema (TMSL), with NO Power BI Desktop
/// and NO engine. A synthetic minimal .pbit is authored under the test scratch dir (a ZIP holding a valid
/// DataModelSchema + a stub Report/Layout + [Content_Types].xml + Version). The round-trip drives every tool
/// (open -> add/update/delete measure -> set column -> add/delete relationship -> save -> reopen) and asserts
/// the edits persist AND that the untouched parts (Report/Layout, [Content_Types].xml) survive byte-for-byte.
/// The error paths (missing file, missing table, duplicate measure, missing DataModelSchema, ...) are covered
/// too. Pure JSON + System.IO.Compression, so it runs green on any box - no Desktop, no engine.
/// </summary>
public sealed class TemplateModelServiceTests
{
    // ---------------- scratch + synthetic-.pbit helpers ----------------

    private static string ScratchDir()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "sbimcp-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // UTF-16-LE + BOM, exactly how a real template stores DataModelSchema / Report/Layout text parts.
    private static byte[] Utf16LeBom(string text)
    {
        byte[] body = new UnicodeEncoding(false, false).GetBytes(text);
        var outBytes = new byte[2 + body.Length];
        outBytes[0] = 0xFF; outBytes[1] = 0xFE;
        Buffer.BlockCopy(body, 0, outBytes, 2, body.Length);
        return outBytes;
    }

    private static JsonObject Col(string name, string dataType) =>
        new() { ["name"] = name, ["dataType"] = dataType, ["sourceColumn"] = name };

    // a small but complete TMSL model: Sales + Date, one measure, one relationship.
    private static string SampleSchemaJson()
    {
        var model = new JsonObject
        {
            ["culture"] = "en-US",
            ["tables"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Sales",
                    ["columns"] = new JsonArray { Col("DateKey", "int64"), Col("Amount", "decimal"), Col("Year", "int64") },
                    ["measures"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "Total Sales", ["expression"] = "SUM(Sales[Amount])", ["formatString"] = "#,0" },
                    },
                    ["partitions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "Sales-part",
                            ["source"] = new JsonObject { ["type"] = "m", ["expression"] = "let Source = #table({}, {}) in Source" },
                        },
                    },
                },
                new JsonObject
                {
                    ["name"] = "Date",
                    ["columns"] = new JsonArray { Col("DateKey", "int64"), Col("Year", "int64") },
                },
            },
            ["relationships"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "r-datekey",
                    ["fromTable"] = "Sales", ["fromColumn"] = "DateKey",
                    ["toTable"] = "Date", ["toColumn"] = "DateKey",
                },
            },
        };
        var root = new JsonObject
        {
            ["name"] = "Sales Template",
            ["compatibilityLevel"] = 1550,
            ["model"] = model,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private const string StubLayout =
        "{\"version\":\"5.43\",\"themeCollection\":{},\"sections\":[{\"name\":\"p1\",\"displayName\":\"Page 1\",\"visualContainers\":[]}]}";

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
        + "<Default Extension=\"json\" ContentType=\"application/json\" /></Types>";

    /// <summary>Write a synthetic .pbit: DataModelSchema (uncompressed, UTF-16-LE+BOM) + a stub Report/Layout
    /// + [Content_Types].xml + Version. Pass includeSchema:false to model a template that has NO model part.</summary>
    private static void WriteSyntheticPbit(string pbit, bool includeSchema = true, bool includeSecurityBindings = false)
    {
        using var fs = new FileStream(pbit, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Write(string name, byte[] bytes, CompressionLevel lvl)
        {
            var e = zip.CreateEntry(name, lvl); using var s = e.Open(); s.Write(bytes, 0, bytes.Length);
        }
        Write("Version", Encoding.ASCII.GetBytes("1.0"), CompressionLevel.Optimal);
        if (includeSchema)
            Write("DataModelSchema", Utf16LeBom(SampleSchemaJson()), CompressionLevel.NoCompression);
        Write("Report/Layout", Utf16LeBom(StubLayout), CompressionLevel.Optimal);
        Write("[Content_Types].xml", Encoding.UTF8.GetBytes(ContentTypes), CompressionLevel.Optimal);
        if (includeSecurityBindings)
            Write("SecurityBindings", new byte[] { 0x01, 0x02, 0x03, 0x04 }, CompressionLevel.Optimal);
    }

    private static bool PartExists(string pbit, string name)
    {
        using var zip = ZipFile.OpenRead(pbit);
        return zip.GetEntry(name) != null;
    }

    private static byte[] ReadPart(string pbit, string name)
    {
        using var zip = ZipFile.OpenRead(pbit);
        var e = zip.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"part '{name}' missing");
        using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    private static JsonObject Obj(object result) =>
        JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject ?? new JsonObject();

    private static JsonObject OpenModel(TemplateModelService svc, string pbit)
        => (JsonObject)Obj(svc.OpenTemplateModel(pbit))["model"]!;

    [Fact]
    public void Save_DropsSecurityBindingsSignature_KeepingOtherParts()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "signed.pbit");
            WriteSyntheticPbit(pbit, includeSecurityBindings: true);
            Assert.True(PartExists(pbit, "SecurityBindings"));                 // present before any edit

            byte[] origLayout = ReadPart(pbit, "Report/Layout");
            var svc = new TemplateModelService();
            svc.AddTemplateMeasure(pbit, "Sales", "Sig Check", "COUNTROWS(Sales)");  // in-place edit + save

            // an edited model no longer matches the signature - it must be dropped so Desktop re-signs on open
            Assert.False(PartExists(pbit, "SecurityBindings"));
            // ... while the untouched parts and the (now edited) model survive
            Assert.Equal(origLayout, ReadPart(pbit, "Report/Layout"));
            Assert.True(PartExists(pbit, "DataModelSchema"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    private static JsonObject Table(JsonObject model, string name)
        => model["tables"]!.AsArray().First(t => (string?)t!["name"] == name)!.AsObject();

    // ---------------- open ----------------

    [Fact]
    public void Open_ReadsTables_ColumnsWithTypes_MeasuresWithDax_AndRelationships()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "fixture.pbit");
            WriteSyntheticPbit(pbit);
            var svc = new TemplateModelService();

            var r = Obj(svc.OpenTemplateModel(pbit));
            Assert.True((bool)r["ok"]!);
            var model = (JsonObject)r["model"]!;
            Assert.Equal("Sales Template", (string?)model["name"]);
            Assert.Equal(1550, (int?)model["compatibilityLevel"]);
            Assert.Equal(2, (int?)model["tableCount"]);
            Assert.Equal(1, (int?)model["relationshipCount"]);

            var sales = Table(model, "Sales");
            Assert.Equal(3, (int?)sales["columnCount"]);
            var amount = sales["columns"]!.AsArray().First(c => (string?)c!["name"] == "Amount")!;
            Assert.Equal("decimal", (string?)amount["dataType"]);

            var measure = sales["measures"]!.AsArray().Single()!;
            Assert.Equal("Total Sales", (string?)measure["name"]);
            Assert.Equal("SUM(Sales[Amount])", (string?)measure["expression"]);   // measure DAX must be present
            Assert.Equal("#,0", (string?)measure["formatString"]);

            var rel = model["relationships"]!.AsArray().Single()!;
            Assert.Equal("Sales", (string?)rel["fromTable"]);
            Assert.Equal("DateKey", (string?)rel["fromColumn"]);
            Assert.Equal("Date", (string?)rel["toTable"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Open_TemplateWithNoDataModelSchema_ReturnsOkFalseWithNote()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "nomodel.pbit");
            WriteSyntheticPbit(pbit, includeSchema: false);
            var svc = new TemplateModelService();

            var r = Obj(svc.OpenTemplateModel(pbit));
            Assert.False((bool)r["ok"]!);
            Assert.Contains("DataModelSchema", (string?)r["note"]!);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ---------------- full round-trip (every tool), preserving untouched parts ----------------

    [Fact]
    public void RoundTrip_EditsPersist_AndUntouchedPartsSurviveByteForByte()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "fixture.pbit");
            WriteSyntheticPbit(pbit);

            // capture the untouched parts BEFORE any edit, so we can prove they survive verbatim
            byte[] origLayout = ReadPart(pbit, "Report/Layout");
            byte[] origContentTypes = ReadPart(pbit, "[Content_Types].xml");

            var svc = new TemplateModelService();

            // (1) add + (2) add scratch + (3) update + (4) delete scratch measure
            Assert.Equal("added", (string?)Obj(svc.AddTemplateMeasure(pbit, "Sales", "Gross Margin",
                "SUMX(Sales, Sales[Amount] * 0.4)", "#,0", "KPIs"))["action"]);
            svc.AddTemplateMeasure(pbit, "Sales", "Scratch Measure", "COUNTROWS(Sales)");
            Assert.Equal("updated", (string?)Obj(svc.UpdateTemplateMeasure(pbit, "Sales", "Total Sales",
                "SUMX(Sales, Sales[Amount])"))["action"]);
            Assert.Equal("deleted", (string?)Obj(svc.DeleteTemplateMeasure(pbit, "Sales", "Scratch Measure"))["action"]);

            // (5) set a column's properties
            svc.SetTemplateColumn(pbit, "Sales", "Amount",
                formatString: "$#,0.00", isHidden: true, summarizeBy: "sum");

            // (6) add a relationship + (7) delete the original relationship
            Assert.Equal("added", (string?)Obj(svc.AddTemplateRelationship(pbit, "Sales", "Year", "Date", "Year",
                "bothDirections"))["action"]);
            Assert.Equal("deleted", (string?)Obj(svc.DeleteTemplateRelationship(pbit, "Sales", "DateKey", "Date", "DateKey"))["action"]);

            // (8) explicit save-as to a copy, leaving the edited source in place
            string outPbit = Path.Combine(dir, "edited-copy.pbit");
            var saved = Obj(svc.SaveTemplateModel(pbit, outPbit));
            Assert.True((bool)saved["ok"]!);
            Assert.Equal(Path.GetFullPath(outPbit), Path.GetFullPath((string)saved["outPath"]!));

            // (9) reopen the copy and assert every edit persisted
            foreach (string target in new[] { pbit, outPbit })
            {
                var model = OpenModel(svc, target);
                var sales = Table(model, "Sales");
                var measures = sales["measures"]!.AsArray();
                Assert.Equal(2, measures.Count);                                       // Total Sales + Gross Margin (Scratch gone)
                var total = measures.First(m => (string?)m!["name"] == "Total Sales")!;
                Assert.Equal("SUMX(Sales, Sales[Amount])", (string?)total["expression"]);
                Assert.Contains(measures, m => (string?)m!["name"] == "Gross Margin");
                Assert.DoesNotContain(measures, m => (string?)m!["name"] == "Scratch Measure");

                var amount = sales["columns"]!.AsArray().First(c => (string?)c!["name"] == "Amount")!;
                Assert.Equal("$#,0.00", (string?)amount["formatString"]);
                Assert.True((bool?)amount["isHidden"]);
                Assert.Equal("sum", (string?)amount["summarizeBy"]);

                var rels = model["relationships"]!.AsArray();
                Assert.Single(rels);                                                   // original dropped, one added
                Assert.Equal("Year", (string?)rels[0]!["fromColumn"]);
                Assert.Equal("bothDirections", (string?)rels[0]!["crossFilteringBehavior"]);
            }

            // (10) the untouched parts came through byte-for-byte in BOTH the in-place file and the copy
            Assert.Equal(origLayout, ReadPart(pbit, "Report/Layout"));
            Assert.Equal(origContentTypes, ReadPart(pbit, "[Content_Types].xml"));
            Assert.Equal(origLayout, ReadPart(outPbit, "Report/Layout"));
            Assert.Equal(origContentTypes, ReadPart(outPbit, "[Content_Types].xml"));

            // the edited DataModelSchema kept its original UTF-16-LE + BOM encoding
            byte[] schema = ReadPart(outPbit, "DataModelSchema");
            Assert.Equal((byte)0xFF, schema[0]);
            Assert.Equal((byte)0xFE, schema[1]);

            // a .bak of the pre-write original sits beside the in-place file
            Assert.True(File.Exists(pbit + ".bak"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Partition_AndOtherModelProperties_AreCarriedThrough_NotDroppedByEdits()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "fixture.pbit");
            WriteSyntheticPbit(pbit);
            var svc = new TemplateModelService();

            svc.AddTemplateMeasure(pbit, "Sales", "Row Count", "COUNTROWS(Sales)");

            // a surgical JSON edit must not drop properties it does not touch (partitions, culture, sourceColumn)
            var schema = TemplateModelService.DecodeTextPart(ReadPart(pbit, "DataModelSchema")).Text;
            var root = TemplateModelService.ParseModel(schema);
            var salesNode = root["model"]!["tables"]!.AsArray().First(t => (string?)t!["name"] == "Sales")!;
            Assert.NotNull(salesNode["partitions"]);
            Assert.Equal("Sales-part", (string?)salesNode["partitions"]![0]!["name"]);
            Assert.Equal("en-US", (string?)root["model"]!["culture"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ---------------- tool wrapper (J.Try) surfaces errors as ok:false JSON ----------------

    [Fact]
    public void ToolWrapper_ReturnsJson_AndSurfacesErrorsAsOkFalse()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "fixture.pbit");
            WriteSyntheticPbit(pbit);
            var svc = new TemplateModelService();

            // happy path through the actual [McpServerTool] method returns a JSON string
            var ok = JsonNode.Parse(TemplateModelTools.OpenTemplateModel(svc, pbit))!.AsObject();
            Assert.True((bool)ok["ok"]!);

            // a duplicate-measure add is caught by J.Try and rendered as a clean ok:false error object
            var err = JsonNode.Parse(TemplateModelTools.AddTemplateMeasure(svc, pbit, "Sales", "Total Sales",
                "SUM(Sales[Amount])", null, null))!.AsObject();
            Assert.False((bool)err["ok"]!);
            Assert.Contains("already exists", (string?)err["error"]!);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ---------------- error paths ----------------

    [Fact]
    public void MissingFile_AndWrongExtension_AreRejected()
    {
        var svc = new TemplateModelService();
        var missing = Assert.Throws<InvalidOperationException>(
            () => svc.OpenTemplateModel(Path.Combine(ScratchDir(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".pbit")));
        Assert.Contains("not found", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => svc.OpenTemplateModel("C:/nope/model.txt"));
    }

    [Fact]
    public void Edit_AgainstMissingTable_DuplicateMeasure_MissingMeasure_MissingColumn_AreRejected()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "fixture.pbit");
            WriteSyntheticPbit(pbit);
            var svc = new TemplateModelService();

            Assert.Contains("not found", Assert.Throws<InvalidOperationException>(
                () => svc.AddTemplateMeasure(pbit, "Nope", "M", "1")).Message);
            Assert.Contains("already exists", Assert.Throws<InvalidOperationException>(
                () => svc.AddTemplateMeasure(pbit, "Sales", "Total Sales", "1")).Message);
            Assert.Contains("does not exist", Assert.Throws<InvalidOperationException>(
                () => svc.UpdateTemplateMeasure(pbit, "Sales", "Ghost", "1")).Message);
            Assert.Contains("does not exist", Assert.Throws<InvalidOperationException>(
                () => svc.DeleteTemplateMeasure(pbit, "Sales", "Ghost")).Message);
            Assert.Contains("does not exist", Assert.Throws<InvalidOperationException>(
                () => svc.SetTemplateColumn(pbit, "Sales", "Ghost", dataType: "string")).Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Relationship_BadEndpoint_AndDeleteMissing_AreRejected()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "fixture.pbit");
            WriteSyntheticPbit(pbit);
            var svc = new TemplateModelService();

            Assert.Contains("not found", Assert.Throws<InvalidOperationException>(
                () => svc.AddTemplateRelationship(pbit, "Sales", "Ghost", "Date", "DateKey")).Message);
            Assert.Contains("No relationship", Assert.Throws<InvalidOperationException>(
                () => svc.DeleteTemplateRelationship(pbit, "Sales", "Year", "Date", "Year")).Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Edit_AgainstTemplateWithNoDataModelSchema_ThrowsClearly()
    {
        string dir = ScratchDir();
        try
        {
            string pbit = Path.Combine(dir, "nomodel.pbit");
            WriteSyntheticPbit(pbit, includeSchema: false);
            var svc = new TemplateModelService();

            Assert.Contains("DataModelSchema", Assert.Throws<InvalidOperationException>(
                () => svc.AddTemplateMeasure(pbit, "Sales", "M", "1")).Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ---------------- pure helpers (encoding round-trip, expression rendering, cross-filter normalisation) ----------------

    [Fact]
    public void DecodeEncode_RoundTrips_EachCharset_PreservingBom()
    {
        foreach (var charset in new[]
                 {
                     TemplateModelService.PartCharset.Utf8,
                     TemplateModelService.PartCharset.Utf16Le,
                     TemplateModelService.PartCharset.Utf16Be,
                 })
        foreach (bool bom in new[] { true, false })
        {
            const string text = "{\"a\":\"kōura\"}";                      // non-ASCII to exercise the encoders
            byte[] bytes = TemplateModelService.EncodeTextPart(charset, bom, text);
            var decoded = TemplateModelService.DecodeTextPart(bytes);
            Assert.Equal(text, decoded.Text);
            Assert.Equal(bom, decoded.HasBom);
            Assert.Equal(charset, decoded.Charset);
        }
    }

    [Fact]
    public void MeasureExpressionText_JoinsArrayFormExpressions()
    {
        var arr = new JsonArray { "VAR x = 1", "RETURN x" };
        Assert.Equal("VAR x = 1\nRETURN x", TemplateModelService.MeasureExpressionText(arr));
        Assert.Equal("SUM(Sales[Amount])", TemplateModelService.MeasureExpressionText(JsonValue.Create("SUM(Sales[Amount])")));
        Assert.Null(TemplateModelService.MeasureExpressionText(null));
    }

    [Fact]
    public void NormaliseCrossFilter_MapsSynonyms_AndRejectsGarbage()
    {
        Assert.Null(TemplateModelService.NormaliseCrossFilter(null));
        Assert.Equal("bothDirections", TemplateModelService.NormaliseCrossFilter("both"));
        Assert.Equal("oneDirection", TemplateModelService.NormaliseCrossFilter("single"));
        Assert.Equal("automatic", TemplateModelService.NormaliseCrossFilter("automatic"));
        Assert.Throws<InvalidOperationException>(() => TemplateModelService.NormaliseCrossFilter("sideways"));
    }
}
