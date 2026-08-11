using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive tests for the Wave Q community generators (calendar / 445 / paginated REST / combine-folder /
/// control-table rename / bulk header transform / group-keep-all / running-total / text pivot / null-preserving
/// unpivot / dynamic unpivot / concatenate-by-group / Table.View folding / Value.NativeQuery folding / list buffer
/// / dataflow model.json). Every test pins the load-bearing M shape: the right function, the documented defect
/// fix, and (for step generators) that the new step references the previous step and `in` is rewired. The point
/// is the PRODUCED TEXT, so the assertions would break if the generator silently changed function or argument.
/// </summary>
public sealed class WaveQGeneratorsTests
{
    // a minimal canonical let-block the step generators build on.
    private const string Let = "let\n    Source = Csv.Document(File.Contents(\"c:\\\\x.csv\"))\nin\n    Source";

    // ---- calendar table -------------------------------------------------------------------------

    [Fact]
    public void GenerateCalendarTable_UsesListDatesAndPartColumns()
    {
        string m = MTransformBuilder.GenerateCalendarTableM("#date(2020,1,1)", "#date(2025,12,31)");
        // the spine: List.Dates over the computed day count, then FromList to a Date column.
        Assert.Contains("List.Dates(StartDate, DayCount, #duration(1, 0, 0, 0))", m);
        Assert.Contains("Table.FromList(Dates, Splitter.SplitByNothing(), {\"Date\"}", m);
        // a representative spread of the ~20 part columns.
        Assert.Contains("\"Year\", each Date.Year([Date])", m);
        Assert.Contains("\"Quarter\", each Date.QuarterOfYear([Date])", m);
        Assert.Contains("\"MonthName\", each Date.MonthName([Date])", m);
        Assert.Contains("\"MonthNo\", each Date.Month([Date])", m);
        Assert.Contains("\"ISOWeek\", each Date.WeekOfYear([Date], Day.Monday)", m);
        Assert.Contains("\"DayName\", each Date.DayOfWeekName([Date])", m);
        Assert.Contains("\"WeekdayNo\", each Date.DayOfWeek([Date], Day.Monday) + 1", m);
        Assert.Contains("\"IsWeekend\", each Date.DayOfWeek([Date], Day.Monday) >= 5", m);
        Assert.Contains("\"YearMonthNo\"", m);
        // a self-contained let..in (a full query, not just a step).
        Assert.StartsWith("let\n", m);
        Assert.EndsWith("in\n    AddIsWeekend", m);
        // without a fiscal request, NO fiscal columns leak in.
        Assert.DoesNotContain("FiscalYear", m);
    }

    [Fact]
    public void GenerateCalendarTable_WithFiscalYearEnd_AddsFiscalColumns()
    {
        // fiscal year-end June (6) -> fiscal year starts July (7).
        string m = MTransformBuilder.GenerateCalendarTableM("#date(2020,1,1)", "#date(2025,12,31)", fiscalYearEndMonth: 6);
        Assert.Contains("\"FiscalMonthNo\", each Number.Mod(Date.Month([Date]) - 7, 12) + 1", m);
        Assert.Contains("\"FiscalYear\", each if Date.Month([Date]) >= 7 then Date.Year([Date]) + 1 else Date.Year([Date])", m);
        Assert.Contains("\"FiscalQuarter\"", m);
        Assert.EndsWith("in\n    AddFiscalQuarter", m);
    }

    [Fact]
    public void GenerateCalendarTable_WithLocale_PassesCultureToNameFunctions()
    {
        string m = MTransformBuilder.GenerateCalendarTableM("#date(2020,1,1)", "#date(2020,12,31)", locale: "en-NZ");
        Assert.Contains("Date.MonthName([Date], \"en-NZ\")", m);
        Assert.Contains("Date.DayOfWeekName([Date], \"en-NZ\")", m);
    }

    [Fact]
    public void GenerateCalendarTable_RejectsBadFiscalMonth()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.GenerateCalendarTableM("#date(2020,1,1)", "#date(2020,12,31)", fiscalYearEndMonth: 13));

    [Fact]
    public void GenerateCalendarTable_RejectsEmptyStart()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.GenerateCalendarTableM("", "#date(2020,12,31)"));

    // ---- 445 calendar ---------------------------------------------------------------------------

    [Fact]
    public void Generate445Calendar_445Pattern_HasWeeksByPeriodAndWeekColumns()
    {
        string m = MTransformBuilder.Generate445CalendarM("#date(2024,1,29)", 445, 12, 3);
        // the 4-4-5 quarter repeated four times across 12 periods.
        Assert.Contains("WeeksByPeriod = {4, 4, 5, 4, 4, 5, 4, 4, 5, 4, 4, 5}", m);
        Assert.Contains("PeriodOfYear", m);
        Assert.Contains("WeekOfPeriod", m);
        Assert.Contains("WeekOfYear", m);
        Assert.Contains("WeekStart = Date.AddDays(StartDate, WeekIndex * 7)", m);
        Assert.Contains("Date.AddDays(WeekStart, 6)", m);
        Assert.EndsWith("in\n    Typed", m);
    }

    [Fact]
    public void Generate445Calendar_454And544_ReorderTheQuarter()
    {
        Assert.Contains("WeeksByPeriod = {4, 5, 4, 4, 5, 4, 4, 5, 4, 4, 5, 4}",
            MTransformBuilder.Generate445CalendarM("#date(2024,1,29)", 454, 12, 1));
        Assert.Contains("WeeksByPeriod = {5, 4, 4, 5, 4, 4, 5, 4, 4, 5, 4, 4}",
            MTransformBuilder.Generate445CalendarM("#date(2024,1,29)", 544, 12, 1));
    }

    [Fact]
    public void Generate445Calendar_13Periods_AreAllFourWeeks()
    {
        string m = MTransformBuilder.Generate445CalendarM("#date(2024,1,29)", 445, 13, 1);
        Assert.Contains("WeeksByPeriod = {4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4}", m);
    }

    [Fact]
    public void Generate445Calendar_RejectsBadPattern()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.Generate445CalendarM("#date(2024,1,29)", 999, 12, 1));

    [Fact]
    public void Generate445Calendar_RejectsBadPeriods()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.Generate445CalendarM("#date(2024,1,29)", 445, 11, 1));

    // ---- paginated REST -------------------------------------------------------------------------

    [Fact]
    public void PaginatedRest_Offset_UsesListGenerateAndStaticUrlWithQueryAndBuffer()
    {
        string m = MTransformBuilder.PaginatedRestSourceM("https://api.test/items", "offset", "value",
            "offset", "limit", 50, null, null);
        Assert.Contains("List.Generate(", m);
        // static base URL + folded page key through the Query record (NOT a concatenated URL).
        Assert.Contains("BaseUrl = \"https://api.test/items\"", m);
        Assert.Contains("Web.Contents(BaseUrl, [Query = [offset = Text.From(offset), limit = Text.From(PageSize)]])", m);
        // page buffering inside the loop is the documented speed-up.
        Assert.Contains("List.Buffer(Records)", m);
        // REST combines RECORDS (List.Combine), not tables.
        Assert.DoesNotContain("Table.Combine", m);
        Assert.Contains("List.Combine(Pages)", m);
        Assert.Contains("Table.FromRecords(AllRows)", m);
        Assert.EndsWith("in\n    Source", m);
    }

    [Fact]
    public void PaginatedRest_Cursor_ReadsNextFieldAndStopsOnNull()
    {
        string m = MTransformBuilder.PaginatedRestSourceM("https://api.test/items", "cursor", "data",
            "cursor", null, 0, "nextCursor", null);
        Assert.Contains("List.Generate(", m);
        Assert.Contains("state[First] or state[Cursor] <> null", m);
        Assert.Contains("Record.FieldOrDefault(Json, \"nextCursor\", null)", m);
        Assert.Contains("Json[data]", m);
        Assert.Contains("List.Buffer(Records)", m);
    }

    [Fact]
    public void PaginatedRest_CursorWithoutNextField_Throws()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.PaginatedRestSourceM("https://api.test", "cursor", "value", "cursor", null, 0, null, null));

    [Fact]
    public void PaginatedRest_RejectsUnknownMode()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.PaginatedRestSourceM("https://api.test", "telepathy", "value", null, null, 10, null, null));

    // ---- combine folder files -------------------------------------------------------------------

    [Fact]
    public void CombineFolder_Csv_UsesTableCombineNotExpandWithSample()
    {
        string m = MTransformBuilder.CombineFolderFilesM("c:\\data", "csv", ",", 0, true, false, false);
        Assert.Contains("Folder.Files(\"c:\\data\")", m);
        Assert.Contains("Text.Lower([Extension]) = \".csv\"", m);
        Assert.Contains("Csv.Document(content, [Delimiter = \",\", Encoding = 65001, QuoteStyle = QuoteStyle.Csv])", m);
        Assert.Contains("Table.PromoteHeaders", m);
        // the load-bearing point: schema-drift safe Table.Combine over per-file tables, NOT the MS default expand-with-sample.
        Assert.Contains("Table.Combine(WithData[Data])", m);
        Assert.DoesNotContain("Sample File", m);
        Assert.DoesNotContain("Transform File", m);
        Assert.EndsWith("in\n    Combined", m);
    }

    [Fact]
    public void CombineFolder_Excel_ReadsFirstSheet()
    {
        string m = MTransformBuilder.CombineFolderFilesM("c:\\data", "excel", null, 0, true, false, false);
        Assert.Contains("Text.Lower([Extension]) = \".xlsx\"", m);
        Assert.Contains("Excel.Workbook(content, null, true)", m);
        Assert.Contains("Workbook{0}[Data]", m);
    }

    [Fact]
    public void CombineFolder_KeepFilename_AddsSourceNameAndFolderPath()
    {
        string m = MTransformBuilder.CombineFolderFilesM("c:\\data", "csv", ",", 0, true, keepFilename: true, skipErrors: false);
        Assert.Contains("\"Source.Name\"", m);
        Assert.Contains("\"Source.Folder Path\"", m);
        Assert.Contains("Table.Combine(Tagged[Tagged])", m);
    }

    [Fact]
    public void CombineFolder_SkipErrors_WrapsEachFileInTryOtherwise()
    {
        string m = MTransformBuilder.CombineFolderFilesM("c:\\data", "csv", ",", 0, true, false, skipErrors: true);
        Assert.Contains("try ReadFile([Content]) otherwise", m);
    }

    [Fact]
    public void CombineFolder_SkipRows_AddsTableSkip()
    {
        string m = MTransformBuilder.CombineFolderFilesM("c:\\data", "csv", ",", 3, true, false, false);
        Assert.Contains("Table.Skip(Csv, 3)", m);
    }

    [Fact]
    public void CombineFolder_RejectsUnknownFileType()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.CombineFolderFilesM("c:\\d", "parquet", null, 0, true, false, false));

    // ---- rename from mapping + bulk header transform --------------------------------------------

    [Fact]
    public void RenameColumnsFromMapping_UsesToRowsAndMissingFieldIgnore()
    {
        string m = MTransformBuilder.RenameColumnsFromMapping(Let, "ColumnMap", "Old", "New");
        Assert.Contains("#\"Renamed From Mapping\" = Table.RenameColumns(Source, Table.ToRows(Table.SelectColumns(ColumnMap, {\"Old\", \"New\"})), MissingField.Ignore)", m);
        Assert.EndsWith("in\n    #\"Renamed From Mapping\"", m);
    }

    [Theory]
    [InlineData("snakeToSpace", "each Text.Replace(_, \"_\", \" \")")]
    [InlineData("toUpper", "Text.Upper")]
    [InlineData("toLower", "Text.Lower")]
    [InlineData("trim", "Text.Trim")]
    public void TransformAllColumnNames_MapsTransformToFunction(string transform, string fn)
    {
        string m = MTransformBuilder.TransformAllColumnNames(Let, transform, null);
        Assert.Contains($"#\"Transformed Column Names\" = Table.TransformColumnNames(Source, {fn})", m);
    }

    [Fact]
    public void TransformAllColumnNames_Prefix_PrependsArg()
    {
        string m = MTransformBuilder.TransformAllColumnNames(Let, "prefix", "dim_");
        Assert.Contains("Table.TransformColumnNames(Source, each \"dim_\" & _)", m);
    }

    [Fact]
    public void TransformAllColumnNames_CamelSplit_InsertsSpaces()
    {
        string m = MTransformBuilder.TransformAllColumnNames(Let, "camelSplit", null);
        Assert.Contains("Text.ToList(_)", m);
        Assert.Contains("\" \" & c", m);
    }

    [Fact]
    public void TransformAllColumnNames_RejectsUnknown()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.TransformAllColumnNames(Let, "wibble", null));

    // ---- group keep all columns -----------------------------------------------------------------

    [Fact]
    public void GroupKeepAllColumns_UsesEachUnderscoreThenDynamicExpand()
    {
        string m = MTransformBuilder.GroupKeepAllColumns(Let, new[] { "Region", "Year" });
        // the {each _} grouping that keeps every column.
        Assert.Contains("#\"Grouped Keep All\" = Table.Group(Source, {\"Region\", \"Year\"}, {{\"AllRows\", each _, type table}})", m);
        // expanded over the NON-key columns derived at evaluation time (schema-drift safe).
        Assert.Contains("Table.ExpandTableColumn(#\"Grouped Keep All\", \"AllRows\", List.Difference(Table.ColumnNames(#\"Grouped Keep All\"{0}[AllRows]), {\"Region\", \"Year\"}))", m);
        Assert.EndsWith("in\n    #\"Expanded AllRows\"", m);
    }

    [Fact]
    public void GroupKeepAllColumns_RejectsNoKeys()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.GroupKeepAllColumns(Let, System.Array.Empty<string>()));

    // ---- running total --------------------------------------------------------------------------

    [Fact]
    public void RunningTotal_Plain_UsesListBufferAndAccumulatesByIndex()
    {
        string m = MTransformBuilder.RunningTotalM(Let, "Amount", "Date", null);
        // sorted, indexed, then buffered single-pass sum (NOT a per-row re-scan of the whole table).
        Assert.Contains("Table.Sort(Source, {{\"Date\", Order.Ascending}})", m);
        Assert.Contains("Table.AddIndexColumn(#\"Sorted For Running Total\", \"RT_Index\", 0, 1, Int64.Type)", m);
        Assert.Contains("List.Buffer(Table.Column(#\"Indexed For Running Total\", \"Amount\"))", m);
        Assert.Contains("List.Sum(List.FirstN(Buffered, [RT_Index] + 1))", m);
        Assert.Contains("\"Running Total\"", m);
        Assert.EndsWith("in\n    #\"Added Running Total\"", m);
    }

    [Fact]
    public void RunningTotal_Grouped_SortsByGroupFirstAndResetsWithinGroup()
    {
        string m = MTransformBuilder.RunningTotalM(Let, "Amount", "Date", "Region");
        Assert.Contains("Table.Sort(Source, {{\"Region\", Order.Ascending}, {\"Date\", Order.Ascending}})", m);
        // group + value lists buffered once; sum ranges from the group's first index.
        Assert.Contains("List.Buffer(Table.Column(#\"Indexed For Running Total\", \"Amount\"))", m);
        Assert.Contains("List.Buffer(Table.Column(#\"Indexed For Running Total\", \"Region\"))", m);
        Assert.Contains("List.PositionOf(Grps, g)", m);
        Assert.Contains("List.Sum(List.Range(Vals, start, i - start + 1))", m);
    }

    [Fact]
    public void RunningTotal_RejectsMissingValueColumn()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.RunningTotalM(Let, "", "Date", null));

    // ---- text pivot / null-preserving unpivot / dynamic unpivot / concatenate --------------------

    [Fact]
    public void PivotTextValues_UsesTextCombineAggregation()
    {
        string m = MTransformBuilder.PivotTextValues(Let, "Attribute", "Note", null);
        Assert.Contains("Table.Pivot(Source, List.Distinct(Source[Attribute]), \"Attribute\", \"Note\", each Text.Combine(List.Transform(_, (v) => Text.From(v)), \", \"))", m);
    }

    [Fact]
    public void PivotTextValues_CustomDelimiter()
    {
        string m = MTransformBuilder.PivotTextValues(Let, "Tag", "Val", " | ");
        Assert.Contains("Text.From(v)), \" | \")", m);
    }

    [Fact]
    public void UnpivotKeepNulls_ReplacesNullToSentinelUnpivotsThenRestores()
    {
        string m = MTransformBuilder.UnpivotKeepNulls(Let, new[] { "Id" });
        // null -> sentinel across the non-keep columns.
        Assert.Contains("Table.ReplaceValue(Source, null, \"##NULL##\", Replacer.ReplaceValue, List.Difference(Table.ColumnNames(Source), {\"Id\"}))", m);
        // unpivot the rest.
        Assert.Contains("Table.UnpivotOtherColumns(#\"Replaced Nulls For Unpivot\", {\"Id\"}, \"Attribute\", \"Value\")", m);
        // sentinel -> null on the Value column.
        Assert.Contains("Table.ReplaceValue(#\"Unpivoted Other Columns\", \"##NULL##\", null, Replacer.ReplaceValue, {\"Value\"})", m);
        Assert.EndsWith("in\n    #\"Restored Nulls\"", m);
    }

    [Fact]
    public void DynamicUnpivotOtherColumns_DerivesSetFromColumnNames()
    {
        string m = MTransformBuilder.DynamicUnpivotOtherColumns(Let, new[] { "Region", "Year" });
        Assert.Contains("Table.UnpivotOtherColumns(Source, List.Intersect({Table.ColumnNames(Source), {\"Region\", \"Year\"}}), \"Attribute\", \"Value\")", m);
    }

    [Fact]
    public void ConcatenateWithGroupBy_UsesGroupPlusTextCombine()
    {
        string m = MTransformBuilder.ConcatenateWithGroupBy(Let, new[] { "OrderId" }, "Tag", "; ", null);
        Assert.Contains("Table.Group(Source, {\"OrderId\"}, {{\"Tag Concatenated\", each Text.Combine(List.Transform(Table.Column(_, \"Tag\"), (v) => Text.From(v)), \"; \"), type text}})", m);
    }

    // ---- folding ---------------------------------------------------------------------------------

    [Fact]
    public void AddTableViewFolding_HasGetRowsAndOnTakeAndMandatoryBaseline()
    {
        string m = MTransformBuilder.AddTableViewFolding(Let, new[] { "OnTake", "GetRowCount" });
        Assert.Contains("Table.View(null, [", m);
        // mandatory baseline always present even though the caller only asked for OnTake/GetRowCount.
        Assert.Contains("GetType = () => Value.Type(prev)", m);
        Assert.Contains("GetRows = () => prev", m);
        Assert.Contains("OnTake = (count as number) => Table.FirstN(prev, count)", m);
        Assert.Contains("GetRowCount = () => Table.RowCount(prev)", m);
        Assert.EndsWith("in\n    #\"Table View Folding\"", m);
    }

    [Fact]
    public void AddTableViewFolding_OptionalHandlersOnlyWhenRequested()
    {
        string m = MTransformBuilder.AddTableViewFolding(Let, new[] { "OnSelectColumns" });
        Assert.Contains("OnSelectColumns = (columns as list) => Table.SelectColumns(prev, columns)", m);
        // not requested -> not emitted.
        Assert.DoesNotContain("OnTake", m);
        Assert.DoesNotContain("GetRowCount", m);
    }

    [Fact]
    public void ValueNativeQueryFolding_KeepsEnableFoldingTrue()
    {
        string m = MTransformBuilder.ValueNativeQueryFolding(Let, "Sql.Database(\"srv\", \"db\")",
            "SELECT * FROM dbo.Sales", null);
        Assert.Contains("#\"Native Query\" = Value.NativeQuery(Sql.Database(\"srv\", \"db\"), \"SELECT * FROM dbo.Sales\", null, [EnableFolding=true])", m);
    }

    [Fact]
    public void ValueNativeQueryFolding_WithParams()
    {
        string m = MTransformBuilder.ValueNativeQueryFolding(Let, "src", "SELECT @id", "[id = 7]");
        Assert.Contains("Value.NativeQuery(src, \"SELECT @id\", [id = 7], [EnableFolding=true])", m);
    }

    [Fact]
    public void SetListBuffer_List_WrapsInListBuffer()
        => Assert.Contains("#\"Buffered List\" = List.Buffer(MyLookup[Key])",
            MTransformBuilder.SetListBuffer(Let, "MyLookup[Key]", "list"));

    [Fact]
    public void SetListBuffer_Table_WrapsInTableBuffer()
        => Assert.Contains("#\"Buffered Reference\" = Table.Buffer(DimDate)",
            MTransformBuilder.SetListBuffer(Let, "DimDate", "table"));

    [Fact]
    public void SetListBuffer_RejectsUnknownKind()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.SetListBuffer(Let, "X", "deque"));

    // ---- dataflow model.json --------------------------------------------------------------------

    [Fact]
    public void ExportDataflowModelJson_HasEntitiesAttributesAndEmbeddedMashup()
    {
        var entities = new List<(string, string, IReadOnlyList<(string, string)>)>
        {
            ("Sales", "let Source = Csv.Document(File.Contents(\"s.csv\")) in Source",
                new List<(string, string)> { ("OrderId", "int64"), ("Amount", "double"), ("Region", "string") }),
        };
        string json = MTransformBuilder.ExportDataflowModelJson("MyDataflow", entities, null);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("1.0", root.GetProperty("version").GetString());
        Assert.Equal("MyDataflow", root.GetProperty("name").GetString());

        var ents = root.GetProperty("entities");
        Assert.Equal(1, ents.GetArrayLength());
        var e0 = ents[0];
        Assert.Equal("LocalEntity", e0.GetProperty("$type").GetString());
        Assert.Equal("Sales", e0.GetProperty("name").GetString());

        var attrs = e0.GetProperty("attributes");
        Assert.Equal(3, attrs.GetArrayLength());
        Assert.Equal("OrderId", attrs[0].GetProperty("name").GetString());
        Assert.Equal("int64", attrs[0].GetProperty("dataType").GetString());
        Assert.Equal("double", attrs[1].GetProperty("dataType").GetString());

        // partitions[] exists on the entity.
        Assert.True(e0.GetProperty("partitions").GetArrayLength() >= 1);

        // the M is embedded once at the root pbi:mashup.document.
        string mashupDoc = root.GetProperty("pbi:mashup").GetProperty("document").GetString()!;
        Assert.Contains("section Section1;", mashupDoc);
        Assert.Contains("shared Sales =", mashupDoc);
    }

    [Fact]
    public void ExportDataflowModelJson_MapsFriendlyTypesToCdmTypes()
    {
        var entities = new List<(string, string, IReadOnlyList<(string, string)>)>
        {
            ("E", "let x = 1 in x", new List<(string, string)> { ("d", "date"), ("b", "bool"), ("c", "currency") }),
        };
        string json = MTransformBuilder.ExportDataflowModelJson("D", entities, "en-NZ");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var attrs = doc.RootElement.GetProperty("entities")[0].GetProperty("attributes");
        Assert.Equal("date", attrs[0].GetProperty("dataType").GetString());
        Assert.Equal("boolean", attrs[1].GetProperty("dataType").GetString());
        Assert.Equal("decimal", attrs[2].GetProperty("dataType").GetString());
        Assert.Equal("en-NZ", doc.RootElement.GetProperty("culture").GetString());
    }

    [Fact]
    public void ExportDataflowModelJson_RejectsNoEntities()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.ExportDataflowModelJson("D",
                new List<(string, string, IReadOnlyList<(string, string)>)>(), null));
}
