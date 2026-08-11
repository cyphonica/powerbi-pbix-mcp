using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive tests for the discrete Power Query (M) transform generators - the ergonomic layer that
/// maps a UI gesture (Merge, Append, Pivot, Group By, ...) onto an appended M step. The whole point is the
/// PRODUCED M TEXT, so every test pins the exact step: the right Table.* function, the right arguments, a
/// reference to the PREVIOUS step's identifier, and the rewired <c>in</c>. No live model is needed.
///
/// Three structural guarantees are proven across the suite:
///   - a let-block gains exactly one step and `in` points at it;
///   - a bare (non-let) source is wrapped as `let Source = &lt;expr&gt;, #"Step" = ... in #"Step"`;
///   - chaining two transforms makes the second reference the first's step (not the original source).
/// </summary>
public sealed class MTransformBuilderTests
{
    // a minimal canonical let-block: a Source step the transforms build on.
    private const string Let = "let\n    Source = Csv.Document(File.Contents(\"c:\\\\x.csv\"))\nin\n    Source";

    // ---- core mechanism: split + append ---------------------------------------------------------

    [Fact]
    public void TrySplitLet_FindsBodyAndFinalRef()
    {
        bool ok = MTransformBuilder.TrySplitLet(Let, out string body, out string finalRef);
        Assert.True(ok);
        Assert.Equal("Source", finalRef);
        Assert.StartsWith("Source = Csv.Document", body);
    }

    [Fact]
    public void TrySplitLet_IgnoresInsideStringsAndBrackets()
    {
        // an `in` inside a string literal and a record must NOT be taken as the let's terminator.
        string m = "let\n    Source = Table.FromRecords({[note=\"shut in the barn\"]})\nin\n    Source";
        bool ok = MTransformBuilder.TrySplitLet(m, out _, out string finalRef);
        Assert.True(ok);
        Assert.Equal("Source", finalRef);
    }

    [Fact]
    public void TrySplitLet_ReturnsFalseForBareExpression()
        => Assert.False(MTransformBuilder.TrySplitLet("Excel.CurrentWorkbook()", out _, out _));

    [Fact]
    public void AppendStep_OnLetBlock_AddsOneStepAndRewiresIn()
    {
        string outM = MTransformBuilder.AppendStep(Let, "My Step", prev => $"Table.Buffer({prev})");
        // the new step references the previous step (Source) and `in` now points at the new step.
        Assert.Contains("#\"My Step\" = Table.Buffer(Source)", outM);
        Assert.EndsWith("in\n    #\"My Step\"", outM);
        // the original Source step is preserved.
        Assert.Contains("Source = Csv.Document", outM);
    }

    [Fact]
    public void AppendStep_OnBareExpression_WrapsItAsSource()
    {
        string outM = MTransformBuilder.AppendStep("Excel.CurrentWorkbook()", "Navigation", prev => $"{prev}{{0}}[Data]");
        Assert.Contains("let\n    Source = Excel.CurrentWorkbook(),", outM);
        Assert.Contains("#\"Navigation\" = Source{0}[Data]", outM);
        Assert.EndsWith("in\n    #\"Navigation\"", outM);
    }

    // ---- one assertion per transform: the exact appended step -----------------------------------

    [Fact]
    public void MergeQueries_AppendsNestedJoin()
    {
        string outM = MTransformBuilder.MergeQueries(Let, "Dim_Product", new[] { "ProductKey" }, new[] { "Key" }, "LeftOuter", null);
        Assert.Contains("#\"Merged Queries\" = Table.NestedJoin(Source, {\"ProductKey\"}, Dim_Product, {\"Key\"}, \"Dim_Product\", JoinKind.LeftOuter)", outM);
        Assert.EndsWith("in\n    #\"Merged Queries\"", outM);
    }

    [Fact]
    public void MergeQueries_WithExpand_AddsExpandTableColumnReferencingTheMergeStep()
    {
        string outM = MTransformBuilder.MergeQueries(Let, "Dim_Product", new[] { "Key" }, new[] { "Key" }, "Inner",
            new[] { "Name", "Category" });
        Assert.Contains("Table.NestedJoin(Source, {\"Key\"}, Dim_Product, {\"Key\"}, \"Dim_Product\", JoinKind.Inner)", outM);
        // the expand step references the merge step (chaining inside the one call) and uses the right columns.
        Assert.Contains("#\"Expanded Dim_Product\" = Table.ExpandTableColumn(#\"Merged Queries\", \"Dim_Product\", {\"Name\", \"Category\"}, {\"Name\", \"Category\"})", outM);
        Assert.EndsWith("in\n    #\"Expanded Dim_Product\"", outM);
    }

    [Fact]
    public void MergeQueries_RejectsMismatchedKeyCounts()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.MergeQueries(Let, "R", new[] { "a", "b" }, new[] { "x" }, "Inner", null));

    [Fact]
    public void MergeQueries_RejectsUnknownJoinKind()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.MergeQueries(Let, "R", new[] { "a" }, new[] { "x" }, "Crossways", null));

    [Fact]
    public void AppendQueries_AppendsCombineWithThePreviousStepFirst()
    {
        string outM = MTransformBuilder.AppendQueries(Let, new[] { "Sales2023", "Sales2024" });
        Assert.Contains("#\"Appended Query\" = Table.Combine({Source, Sales2023, Sales2024})", outM);
    }

    [Fact]
    public void PivotColumn_AppendsPivotWithDistinctValuesAndSum()
    {
        string outM = MTransformBuilder.PivotColumn(Let, "Month", "Sales", "sum");
        Assert.Contains("#\"Pivoted Column\" = Table.Pivot(Source, List.Distinct(Source[Month]), \"Month\", \"Sales\", List.Sum)", outM);
    }

    [Fact]
    public void GroupBy_AppendsGroupWithKeysAndAggregates()
    {
        var aggs = new List<(string, string, string?)>
        {
            ("Total Sales", "Sum", "Amount"),
            ("Lines", "Count", null),
            ("Distinct Customers", "CountDistinct", "CustomerId"),
        };
        string outM = MTransformBuilder.GroupBy(Let, new[] { "Region", "Year" }, aggs);
        Assert.Contains("Table.Group(Source, {\"Region\", \"Year\"}, {", outM);
        Assert.Contains("{\"Total Sales\", each List.Sum(List.Transform(_, each _[Amount])), type number}", outM);
        Assert.Contains("{\"Lines\", each Table.RowCount(_), Int64.Type}", outM);
        Assert.Contains("{\"Distinct Customers\", each List.Count(List.Distinct(List.Transform(_, each _[CustomerId]))), Int64.Type}", outM);
    }

    [Fact]
    public void SplitColumn_Delimiter_UsesSplitTextByDelimiter()
    {
        string outM = MTransformBuilder.SplitColumn(Let, "Full Name", "delimiter", " ");
        Assert.Contains("Table.SplitColumn(Source, \"Full Name\", Splitter.SplitTextByDelimiter(\" \", QuoteStyle.Csv), {\"Full Name.1\", \"Full Name.2\"})", outM);
    }

    [Fact]
    public void SplitColumn_Positions_UsesSplitTextByPositions()
    {
        string outM = MTransformBuilder.SplitColumn(Let, "Code", "positions", "0,5");
        Assert.Contains("Splitter.SplitTextByPositions({0,5})", outM);
    }

    [Fact]
    public void ReplaceValues_AppendsReplaceValueWithReplacerReplaceText()
    {
        string outM = MTransformBuilder.ReplaceValues(Let, "Region", "N/A", "Unknown");
        Assert.Contains("#\"Replaced Value\" = Table.ReplaceValue(Source, \"N/A\", \"Unknown\", Replacer.ReplaceText, {\"Region\"})", outM);
    }

    [Fact]
    public void ChangeColumnType_MapsFriendlyTokensToMTypes()
    {
        var types = new List<(string, string)> { ("Amount", "number"), ("OrderDate", "date"), ("Qty", "int"), ("Active", "bool") };
        string outM = MTransformBuilder.ChangeColumnType(Let, types);
        Assert.Contains("Table.TransformColumnTypes(Source, {{\"Amount\", type number}, {\"OrderDate\", type date}, {\"Qty\", Int64.Type}, {\"Active\", type logical}})", outM);
    }

    [Fact]
    public void FilterRows_WrapsABareConditionInEach()
    {
        string outM = MTransformBuilder.FilterRows(Let, "[Amount] > 0");
        Assert.Contains("#\"Filtered Rows\" = Table.SelectRows(Source, each [Amount] > 0)", outM);
    }

    [Fact]
    public void FilterRows_PassesThroughAnExplicitEach()
    {
        string outM = MTransformBuilder.FilterRows(Let, "each [Region] = \"NZ\"");
        Assert.Contains("Table.SelectRows(Source, each [Region] = \"NZ\")", outM);
    }

    [Fact]
    public void AddCustomColumn_AppendsAddColumnWithEach()
    {
        string outM = MTransformBuilder.AddCustomColumn(Let, "Line Total", "[Qty] * [Price]");
        Assert.Contains("#\"Added Custom\" = Table.AddColumn(Source, \"Line Total\", each [Qty] * [Price])", outM);
    }

    [Fact]
    public void AddIndexColumn_AppendsAddIndexColumnWithStartAndStep()
    {
        string outM = MTransformBuilder.AddIndexColumn(Let, "Row", 1, 1);
        Assert.Contains("#\"Added Index\" = Table.AddIndexColumn(Source, \"Row\", 1, 1, Int64.Type)", outM);
    }

    [Fact]
    public void AddIndexColumn_DefaultsNameToIndex()
    {
        string outM = MTransformBuilder.AddIndexColumn(Let, null, 0, 1);
        Assert.Contains("Table.AddIndexColumn(Source, \"Index\", 0, 1, Int64.Type)", outM);
    }

    [Fact]
    public void RemoveColumns_AppendsRemoveColumns()
    {
        string outM = MTransformBuilder.RemoveColumns(Let, new[] { "Temp", "Scratch" });
        Assert.Contains("#\"Removed Columns\" = Table.RemoveColumns(Source, {\"Temp\", \"Scratch\"})", outM);
    }

    [Fact]
    public void RenameColumns_AppendsRenameColumns()
    {
        var renames = new List<(string, string)> { ("col1", "Amount"), ("col2", "Region") };
        string outM = MTransformBuilder.RenameColumns(Let, renames);
        Assert.Contains("#\"Renamed Columns\" = Table.RenameColumns(Source, {{\"col1\", \"Amount\"}, {\"col2\", \"Region\"}})", outM);
    }

    [Fact]
    public void FillDown_AppendsFillDown()
        => Assert.Contains("#\"Filled Down\" = Table.FillDown(Source, {\"Category\"})",
            MTransformBuilder.FillDown(Let, new[] { "Category" }));

    [Fact]
    public void FillUp_AppendsFillUp()
        => Assert.Contains("#\"Filled Up\" = Table.FillUp(Source, {\"Category\"})",
            MTransformBuilder.FillUp(Let, new[] { "Category" }));

    [Fact]
    public void RemoveDuplicates_WholeRow_UsesTableDistinctNoColumns()
        => Assert.Contains("#\"Removed Duplicates\" = Table.Distinct(Source)",
            MTransformBuilder.RemoveDuplicates(Let, null));

    [Fact]
    public void RemoveDuplicates_OnKeyColumns_PassesTheColumnList()
        => Assert.Contains("Table.Distinct(Source, {\"CustomerId\"})",
            MTransformBuilder.RemoveDuplicates(Let, new[] { "CustomerId" }));

    [Fact]
    public void PromoteHeaders_AppendsPromoteHeaders()
        => Assert.Contains("#\"Promoted Headers\" = Table.PromoteHeaders(Source, [PromoteAllScalars=true])",
            MTransformBuilder.PromoteHeaders(Let));

    [Fact]
    public void Transpose_AppendsTranspose()
        => Assert.Contains("#\"Transposed Table\" = Table.Transpose(Source)", MTransformBuilder.Transpose(Let));

    // ---- escaping + identifier quoting ----------------------------------------------------------

    [Fact]
    public void ColumnNameWithQuote_IsDoubledInTheMLiteral()
    {
        string outM = MTransformBuilder.RemoveColumns(Let, new[] { "He said \"hi\"" });
        Assert.Contains("{\"He said \"\"hi\"\"\"}", outM);
    }

    [Fact]
    public void RightTableWithSpace_IsQuotedAsAStepReference()
    {
        string outM = MTransformBuilder.AppendQueries(Let, new[] { "Sales 2024" });
        Assert.Contains("Table.Combine({Source, #\"Sales 2024\"})", outM);
    }

    // ---- chaining: the second transform references the first's step -----------------------------

    [Fact]
    public void ChainingTwoTransforms_SecondReferencesTheFirstsStep()
    {
        string afterFilter = MTransformBuilder.FilterRows(Let, "[Amount] > 0");
        string afterIndex = MTransformBuilder.AddIndexColumn(afterFilter, "Index", 0, 1);

        // both steps survive; the index step builds on the filter step (NOT on Source); `in` points at the index.
        Assert.Contains("#\"Filtered Rows\" = Table.SelectRows(Source, each [Amount] > 0)", afterIndex);
        Assert.Contains("#\"Added Index\" = Table.AddIndexColumn(#\"Filtered Rows\", \"Index\", 0, 1, Int64.Type)", afterIndex);
        Assert.EndsWith("in\n    #\"Added Index\"", afterIndex);
    }

    [Fact]
    public void ChainingFromABareSource_FirstWrapsThenSecondChains()
    {
        string first = MTransformBuilder.PromoteHeaders("Excel.CurrentWorkbook(){[Name=\"Sheet1\"]}[Data]");
        string second = MTransformBuilder.ChangeColumnType(first, new List<(string, string)> { ("A", "text") });

        // first wrapped the bare source; second chains off the promote step.
        Assert.Contains("let\n    Source = Excel.CurrentWorkbook()", second);
        Assert.Contains("#\"Promoted Headers\" = Table.PromoteHeaders(Source, [PromoteAllScalars=true])", second);
        Assert.Contains("#\"Changed Type\" = Table.TransformColumnTypes(#\"Promoted Headers\", {{\"A\", type text}})", second);
        Assert.EndsWith("in\n    #\"Changed Type\"", second);
    }

    // ============================================================ Wave J: remaining transforms

    [Fact]
    public void UnpivotColumns_AppendsTableUnpivot()
    {
        string outM = MTransformBuilder.UnpivotColumns(Let, new[] { "Jan", "Feb", "Mar" });
        Assert.Contains("#\"Unpivoted Columns\" = Table.Unpivot(Source, {\"Jan\", \"Feb\", \"Mar\"}, \"Attribute\", \"Value\")", outM);
        Assert.EndsWith("in\n    #\"Unpivoted Columns\"", outM);
    }

    [Fact]
    public void UnpivotOtherColumns_KeepsTheGivenColumns()
    {
        string outM = MTransformBuilder.UnpivotOtherColumns(Let, new[] { "Region", "Year" });
        Assert.Contains("#\"Unpivoted Other Columns\" = Table.UnpivotOtherColumns(Source, {\"Region\", \"Year\"}, \"Attribute\", \"Value\")", outM);
    }

    [Fact]
    public void MergeColumns_UsesCombineColumnsWithCombiner()
    {
        string outM = MTransformBuilder.MergeColumns(Let, new[] { "First", "Last" }, " ", "Full Name");
        Assert.Contains("#\"Merged Columns\" = Table.CombineColumns(Source, {\"First\", \"Last\"}, Combiner.CombineTextByDelimiter(\" \", QuoteStyle.None), \"Full Name\")", outM);
    }

    [Fact]
    public void MergeColumns_RejectsFewerThanTwoColumns()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.MergeColumns(Let, new[] { "One" }, " ", "X"));

    [Fact]
    public void ExpandColumn_Record_UsesExpandRecordColumn()
    {
        string outM = MTransformBuilder.ExpandColumn(Let, "Details", new[] { "Name", "Age" }, "record");
        Assert.Contains("#\"Expanded Details\" = Table.ExpandRecordColumn(Source, \"Details\", {\"Name\", \"Age\"}, {\"Name\", \"Age\"})", outM);
    }

    [Fact]
    public void ExpandColumn_Table_UsesExpandTableColumn()
    {
        string outM = MTransformBuilder.ExpandColumn(Let, "Lines", new[] { "Qty" }, "table");
        Assert.Contains("Table.ExpandTableColumn(Source, \"Lines\", {\"Qty\"}, {\"Qty\"})", outM);
    }

    [Fact]
    public void ExpandColumn_List_UsesExpandListColumnAndIgnoresFields()
    {
        string outM = MTransformBuilder.ExpandColumn(Let, "Tags", null, "list");
        Assert.Contains("#\"Expanded Tags\" = Table.ExpandListColumn(Source, \"Tags\")", outM);
    }

    [Fact]
    public void ExpandColumn_DefaultsToRecord()
    {
        string outM = MTransformBuilder.ExpandColumn(Let, "Rec", new[] { "A" }, null);
        Assert.Contains("Table.ExpandRecordColumn(Source, \"Rec\", {\"A\"}, {\"A\"})", outM);
    }

    [Fact]
    public void ExpandColumn_RecordWithoutFields_Throws()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.ExpandColumn(Let, "Rec", null, "record"));

    [Fact]
    public void KeepTopRows_UsesTableFirstN()
        => Assert.Contains("#\"Kept First Rows\" = Table.FirstN(Source, 10)", MTransformBuilder.KeepTopRows(Let, 10));

    [Fact]
    public void KeepBottomRows_UsesTableLastN()
        => Assert.Contains("#\"Kept Last Rows\" = Table.LastN(Source, 5)", MTransformBuilder.KeepBottomRows(Let, 5));

    [Fact]
    public void SkipRows_UsesTableSkip()
        => Assert.Contains("#\"Skipped Rows\" = Table.Skip(Source, 3)", MTransformBuilder.SkipRows(Let, 3));

    [Fact]
    public void KeepRangeRows_UsesTableRange()
        => Assert.Contains("#\"Kept Range of Rows\" = Table.Range(Source, 2, 7)", MTransformBuilder.KeepRangeRows(Let, 2, 7));

    [Fact]
    public void KeepTopRows_RejectsNegativeCount()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.KeepTopRows(Let, -1));

    [Fact]
    public void SortRows_AppendsTableSortWithOrderLiterals()
    {
        var sorts = new List<(string, string)> { ("Region", "Ascending"), ("Sales", "Descending") };
        string outM = MTransformBuilder.SortRows(Let, sorts);
        Assert.Contains("#\"Sorted Rows\" = Table.Sort(Source, {{\"Region\", Order.Ascending}, {\"Sales\", Order.Descending}})", outM);
    }

    [Fact]
    public void SortRows_RejectsUnknownDirection()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.SortRows(Let, new List<(string, string)> { ("A", "sideways") }));

    [Fact]
    public void SelectColumns_KeepsOnlyTheListedColumns()
        => Assert.Contains("#\"Removed Other Columns\" = Table.SelectColumns(Source, {\"A\", \"B\"})",
            MTransformBuilder.SelectColumns(Let, new[] { "A", "B" }));

    [Fact]
    public void ReorderColumns_UsesTableReorderColumns()
        => Assert.Contains("#\"Reordered Columns\" = Table.ReorderColumns(Source, {\"B\", \"A\"})",
            MTransformBuilder.ReorderColumns(Let, new[] { "B", "A" }));

    [Fact]
    public void DuplicateColumn_UsesTableDuplicateColumn()
        => Assert.Contains("#\"Duplicated Column\" = Table.DuplicateColumn(Source, \"Region\", \"Region copy\")",
            MTransformBuilder.DuplicateColumn(Let, "Region", "Region copy"));

    [Theory]
    [InlineData("upper", "each Text.Upper(_)", "type text")]
    [InlineData("trim", "each Text.Trim(_)", "type text")]
    [InlineData("length", "each Text.Length(_)", "Int64.Type")]
    [InlineData("round", "each Number.Round(_, 0)", "type number")]
    [InlineData("abs", "each Number.Abs(_)", "type number")]
    [InlineData("year", "each Date.Year(_)", "Int64.Type")]
    [InlineData("startofmonth", "each Date.StartOfMonth(_)", "type date")]
    public void TransformColumn_MapsOperationToFunctionAndType(string op, string fn, string resultType)
    {
        string outM = MTransformBuilder.TransformColumn(Let, "Col", op);
        Assert.Contains($"#\"Transformed Column\" = Table.TransformColumns(Source, {{{{\"Col\", {fn}, {resultType}}}}})", outM);
    }

    [Fact]
    public void TransformColumn_RejectsUnknownOperation()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.TransformColumn(Let, "Col", "frobnicate"));

    // ---- folding-control hints ------------------------------------------------------------------

    [Fact]
    public void BufferTable_AppendsTableBuffer()
        => Assert.Contains("#\"Buffered Table\" = Table.Buffer(Source)", MTransformBuilder.BufferTable(Let));

    [Fact]
    public void StopFolding_AppendsTableStopFolding()
        => Assert.Contains("#\"Stopped Folding\" = Table.StopFolding(Source)", MTransformBuilder.StopFolding(Let));

    // ---- connector source step ------------------------------------------------------------------

    [Fact]
    public void SourceStep_Sql_WithoutQuery()
    {
        var p = new Dictionary<string, string> { ["server"] = "srv01", ["database"] = "Sales" };
        Assert.Equal("Sql.Database(\"srv01\", \"Sales\")", MTransformBuilder.SourceStep("sql", p));
    }

    [Fact]
    public void SourceStep_Sql_WithQuery()
    {
        var p = new Dictionary<string, string> { ["server"] = "srv01", ["database"] = "Sales", ["query"] = "SELECT 1" };
        Assert.Equal("Sql.Database(\"srv01\", \"Sales\", [Query=\"SELECT 1\"])", MTransformBuilder.SourceStep("sql", p));
    }

    [Fact]
    public void SourceStep_Web_WithRelativePathAndHeaders()
    {
        var p = new Dictionary<string, string>
        {
            ["url"] = "https://api.example.com",
            ["relativePath"] = "v1/items",
            ["headers"] = "[#\"Authorization\"=\"Bearer x\"]",
        };
        Assert.Equal("Web.Contents(\"https://api.example.com\", [RelativePath=\"v1/items\", Headers=[#\"Authorization\"=\"Bearer x\"]])",
            MTransformBuilder.SourceStep("web", p));
    }

    [Fact]
    public void SourceStep_Web_PlainUrl()
        => Assert.Equal("Web.Contents(\"https://x.test\")",
            MTransformBuilder.SourceStep("web", new Dictionary<string, string> { ["url"] = "https://x.test" }));

    [Fact]
    public void SourceStep_ODataExcelCsvFolderSharePointBlob()
    {
        Assert.Equal("OData.Feed(\"https://svc/odata\")",
            MTransformBuilder.SourceStep("odata", new Dictionary<string, string> { ["url"] = "https://svc/odata" }));
        Assert.Equal(@"Excel.Workbook(File.Contents(""c:\book.xlsx""), null, true)",
            MTransformBuilder.SourceStep("excel", new Dictionary<string, string> { ["path"] = @"c:\book.xlsx" }));
        Assert.Equal(@"Csv.Document(File.Contents(""c:\d.csv""), [Delimiter="","", Encoding=65001, QuoteStyle=QuoteStyle.Csv])",
            MTransformBuilder.SourceStep("csv", new Dictionary<string, string> { ["path"] = @"c:\d.csv" }));
        Assert.Equal(@"Folder.Files(""c:\data"")",
            MTransformBuilder.SourceStep("folder", new Dictionary<string, string> { ["path"] = @"c:\data" }));
        Assert.Equal("SharePoint.Files(\"https://team.sharepoint.com\", [ApiVersion=15])",
            MTransformBuilder.SourceStep("sharepoint", new Dictionary<string, string> { ["url"] = "https://team.sharepoint.com" }));
        Assert.Equal("AzureStorage.Blobs(\"myaccount\")",
            MTransformBuilder.SourceStep("azureblob", new Dictionary<string, string> { ["account"] = "myaccount" }));
    }

    [Fact]
    public void SourceStep_MissingRequiredParam_Throws()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.SourceStep("sql", new Dictionary<string, string> { ["server"] = "srv" }));

    [Fact]
    public void SourceStep_UnknownConnector_Throws()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.SourceStep("teleport", new Dictionary<string, string>()));

    [Fact]
    public void ReplaceSourceStep_OnLetBlock_ReplacesFirstStepRhsKeepingDownstream()
    {
        // a two-step query: Source then a downstream Promoted step.
        string m = "let\n    Source = Csv.Document(File.Contents(\"old.csv\")),\n    #\"Promoted\" = Table.PromoteHeaders(Source)\nin\n    #\"Promoted\"";
        string outM = MTransformBuilder.ReplaceSourceStep(m, "Sql.Database(\"srv\", \"db\")");
        Assert.Contains("Source = Sql.Database(\"srv\", \"db\")", outM);
        Assert.DoesNotContain("Csv.Document", outM);
        // downstream step + the original `in` target survive.
        Assert.Contains("#\"Promoted\" = Table.PromoteHeaders(Source)", outM);
        Assert.EndsWith("in\n    #\"Promoted\"", outM);
    }

    [Fact]
    public void ReplaceSourceStep_OnSingleStepLet_ReplacesRhsAndKeepsInTarget()
    {
        // a one-step query (Source then `in Source`): only the rhs changes.
        string outM = MTransformBuilder.ReplaceSourceStep(Let, "Sql.Database(\"srv\", \"db\")");
        Assert.Equal("let\n    Source = Sql.Database(\"srv\", \"db\")\nin\n    Source", outM);
    }

    [Fact]
    public void ReplaceSourceStep_OnBareOrEmpty_ProducesFreshLet()
    {
        string outM = MTransformBuilder.ReplaceSourceStep("", "Sql.Database(\"s\", \"d\")");
        Assert.Equal("let\n    Source = Sql.Database(\"s\", \"d\")\nin\n    Source", outM);
    }

    // ---- M parameter ----------------------------------------------------------------------------

    [Fact]
    public void ParameterExpression_Text_WithDefault()
    {
        string m = MTransformBuilder.ParameterExpression("Text", "NZ", null);
        Assert.Equal("\"NZ\" meta [IsParameterQuery=true, List=null, DefaultValue=null, Type=\"Text\", IsParameterQueryRequired=true]", m);
    }

    [Fact]
    public void ParameterExpression_Number_RendersUnquotedLiteral()
    {
        string m = MTransformBuilder.ParameterExpression("Number", "42", null);
        Assert.StartsWith("42 meta [", m);
        Assert.Contains("Type=\"Number\"", m);
    }

    [Fact]
    public void ParameterExpression_Logical_RendersBoolean()
        => Assert.StartsWith("true meta [", MTransformBuilder.ParameterExpression("Logical", "true", null));

    [Fact]
    public void ParameterExpression_DateTime_RendersDateTimeLiteral()
    {
        string m = MTransformBuilder.ParameterExpression("DateTime", "2024-01-15", null);
        Assert.StartsWith("#datetime(2024, 1, 15, 0, 0, 0) meta [", m);
        Assert.Contains("Type=\"DateTime\"", m);
    }

    [Fact]
    public void ParameterExpression_AllowedValues_BecomeTheList()
    {
        string m = MTransformBuilder.ParameterExpression("Text", "AU", new[] { "AU", "NZ", "UK" });
        Assert.Contains("List={\"AU\", \"NZ\", \"UK\"}", m);
        Assert.StartsWith("\"AU\" meta [", m);
    }

    [Fact]
    public void ParameterExpression_RejectsUnknownType()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.ParameterExpression("Guid", "x", null));

    [Fact]
    public void ParameterExpression_RejectsNonNumericNumberValue()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.ParameterExpression("Number", "abc", null));

    // ============================================================ Wave L: error rows, row trims, value/error replace

    [Fact]
    public void RemoveErrors_WholeRow_UsesRemoveRowsWithErrorsNoColumns()
        => Assert.Contains("#\"Removed Errors\" = Table.RemoveRowsWithErrors(Source)",
            MTransformBuilder.RemoveErrors(Let, null));

    [Fact]
    public void RemoveErrors_OnColumns_PassesTheColumnList()
        => Assert.Contains("Table.RemoveRowsWithErrors(Source, {\"Amount\", \"Qty\"})",
            MTransformBuilder.RemoveErrors(Let, new[] { "Amount", "Qty" }));

    [Fact]
    public void KeepErrors_WholeRow_UsesSelectRowsWithErrorsNoColumns()
        => Assert.Contains("#\"Kept Errors\" = Table.SelectRowsWithErrors(Source)",
            MTransformBuilder.KeepErrors(Let, null));

    [Fact]
    public void KeepErrors_OnColumns_PassesTheColumnList()
        => Assert.Contains("Table.SelectRowsWithErrors(Source, {\"Amount\"})",
            MTransformBuilder.KeepErrors(Let, new[] { "Amount" }));

    [Fact]
    public void ReplaceErrors_NumberAndText_EmitsTypedReplaceErrorValues()
    {
        var repl = new List<(string, string?, string)>
        {
            ("Amount", "0", "number"),
            ("Region", "Unknown", "text"),
        };
        string outM = MTransformBuilder.ReplaceErrors(Let, repl);
        Assert.Contains("#\"Replaced Errors\" = Table.ReplaceErrorValues(Source, {{\"Amount\", 0}, {\"Region\", \"Unknown\"}})", outM);
        Assert.EndsWith("in\n    #\"Replaced Errors\"", outM);
    }

    [Fact]
    public void ReplaceErrors_NullValue_EmitsBareNull()
    {
        string outM = MTransformBuilder.ReplaceErrors(Let, new List<(string, string?, string)> { ("Qty", null, "null") });
        Assert.Contains("Table.ReplaceErrorValues(Source, {{\"Qty\", null}})", outM);
    }

    [Fact]
    public void ReplaceErrors_RejectsEmpty()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.ReplaceErrors(Let, new List<(string, string?, string)>()));

    [Fact]
    public void RemoveBlankRows_UsesSelectRowsAllFieldsNonBlankPredicate()
    {
        string outM = MTransformBuilder.RemoveBlankRows(Let);
        Assert.Contains("#\"Removed Blank Rows\" = Table.SelectRows(Source, each not List.IsEmpty(List.RemoveMatchingItems(Record.FieldValues(_), {\"\", null})))", outM);
    }

    [Fact]
    public void RemoveBottomRows_UsesRemoveLastN()
        => Assert.Contains("#\"Removed Bottom Rows\" = Table.RemoveLastN(Source, 4)",
            MTransformBuilder.RemoveBottomRows(Let, 4));

    [Fact]
    public void RemoveAlternateRows_UsesAlternateRowsWithThePattern()
        => Assert.Contains("#\"Removed Alternate Rows\" = Table.AlternateRows(Source, 1, 1, 2)",
            MTransformBuilder.RemoveAlternateRows(Let, 1, 1, 2));

    [Fact]
    public void RemoveAlternateRows_RejectsNegative()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.RemoveAlternateRows(Let, -1, 1, 1));

    [Fact]
    public void ReplaceValue_Numeric_UsesReplacerReplaceValue()
    {
        string outM = MTransformBuilder.ReplaceValue(Let, "Amount", "-1", "0", "number");
        Assert.Contains("#\"Replaced Value\" = Table.ReplaceValue(Source, -1, 0, Replacer.ReplaceValue, {\"Amount\"})", outM);
        // proves it is NOT the text-substring ReplaceText path.
        Assert.DoesNotContain("Replacer.ReplaceText", outM);
    }

    [Fact]
    public void ReplaceValue_NullToValue_EmitsNullFindLiteral()
    {
        // null -> 0 : the find side is a bare null, the replace a number.
        string outM = MTransformBuilder.ReplaceValue(Let, "Qty", null, "0", "number");
        Assert.Contains("Table.ReplaceValue(Source, null, 0, Replacer.ReplaceValue, {\"Qty\"})", outM);
    }

    [Fact]
    public void ReplaceValue_ValueToNull_EmitsNullReplaceLiteral()
    {
        // 0 -> null : value cleared, type still number so the find is numeric.
        string outM = MTransformBuilder.ReplaceValue(Let, "Qty", "0", null, "number");
        Assert.Contains("Table.ReplaceValue(Source, 0, null, Replacer.ReplaceValue, {\"Qty\"})", outM);
    }

    [Fact]
    public void ReplaceValue_Text_QuotesBothSides()
    {
        string outM = MTransformBuilder.ReplaceValue(Let, "Region", "N/A", "Unknown", "text");
        Assert.Contains("Table.ReplaceValue(Source, \"N/A\", \"Unknown\", Replacer.ReplaceValue, {\"Region\"})", outM);
    }

    [Fact]
    public void ReplaceValue_RejectsNonNumericNumberValue()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.ReplaceValue(Let, "Amount", "x", "0", "number"));

    // ============================================================ Wave L: split N cols / split to rows

    [Fact]
    public void SplitColumn_IntoFourColumns_NamesAllFour()
    {
        string outM = MTransformBuilder.SplitColumn(Let, "Code", "delimiter", "-", 4);
        Assert.Contains("Table.SplitColumn(Source, \"Code\", Splitter.SplitTextByDelimiter(\"-\", QuoteStyle.Csv), {\"Code.1\", \"Code.2\", \"Code.3\", \"Code.4\"})", outM);
    }

    [Fact]
    public void SplitColumn_DefaultsToTwoColumns()
    {
        string outM = MTransformBuilder.SplitColumn(Let, "Full Name", "delimiter", " ");
        Assert.Contains("{\"Full Name.1\", \"Full Name.2\"}", outM);
    }

    [Fact]
    public void SplitColumn_RejectsZeroParts()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.SplitColumn(Let, "C", "delimiter", ",", 0));

    [Fact]
    public void SplitColumnToRows_SplitsToListThenExpands()
    {
        string outM = MTransformBuilder.SplitColumnToRows(Let, "Tags", ",");
        // first: split into a list (no output-name list -> extractValues form); then expand the list column.
        Assert.Contains("#\"Split Column to Rows\" = Table.SplitColumn(Source, \"Tags\", Splitter.SplitTextByDelimiter(\",\", QuoteStyle.Csv))", outM);
        Assert.Contains("#\"Expanded Tags\" = Table.ExpandListColumn(#\"Split Column to Rows\", \"Tags\")", outM);
        Assert.EndsWith("in\n    #\"Expanded Tags\"", outM);
    }

    // ============================================================ Wave L: demote / change-type locale / detect types

    [Fact]
    public void DemoteHeaders_UsesTableDemoteHeaders()
        => Assert.Contains("#\"Demoted Headers\" = Table.DemoteHeaders(Source)", MTransformBuilder.DemoteHeaders(Let));

    [Fact]
    public void ChangeColumnType_WithCulture_AppendsLocaleArgument()
    {
        var types = new List<(string, string)> { ("OrderDate", "date") };
        string outM = MTransformBuilder.ChangeColumnType(Let, types, "en-US");
        Assert.Contains("Table.TransformColumnTypes(Source, {{\"OrderDate\", type date}}, \"en-US\")", outM);
    }

    [Fact]
    public void ChangeColumnType_WithoutCulture_OmitsLocaleArgument()
    {
        string outM = MTransformBuilder.ChangeColumnType(Let, new List<(string, string)> { ("A", "text") });
        Assert.Contains("Table.TransformColumnTypes(Source, {{\"A\", type text}})", outM);
        // no trailing locale string slipped in.
        Assert.DoesNotContain("}}, \"", outM);
    }

    [Fact]
    public void DetectColumnTypes_EmitsAutoDetectTransformColumnTypes()
    {
        string outM = MTransformBuilder.DetectColumnTypes(Let);
        Assert.Contains("#\"Detected Column Types\" = Table.TransformColumnTypes(Source, List.Transform(Table.ColumnNames(Source),", outM);
        Assert.Contains("if v is number then type number", outM);
        Assert.EndsWith("in\n    #\"Detected Column Types\"", outM);
    }

    // ============================================================ Wave L: move column

    [Fact]
    public void MoveColumn_ToStart_CombinesColumnFirst()
    {
        string outM = MTransformBuilder.MoveColumn(Let, "Region", "start", null);
        Assert.Contains("#\"Reordered Columns\" = Table.ReorderColumns(Source, List.Distinct(List.Combine({{\"Region\"}, Table.ColumnNames(Source)})))", outM);
    }

    [Fact]
    public void MoveColumn_ToEnd_AppendsColumnLast()
    {
        string outM = MTransformBuilder.MoveColumn(Let, "Region", "end", null);
        Assert.Contains("Table.ReorderColumns(Source, List.Distinct(List.Combine({List.RemoveItems(Table.ColumnNames(Source), {\"Region\"}), {\"Region\"}})))", outM);
    }

    [Fact]
    public void MoveColumn_Before_InsertsAtRefPosition()
    {
        string outM = MTransformBuilder.MoveColumn(Let, "Region", "before", "Amount");
        Assert.Contains("List.InsertRange(List.RemoveItems(Table.ColumnNames(Source), {\"Region\"}), List.PositionOf(List.RemoveItems(Table.ColumnNames(Source), {\"Region\"}), \"Amount\"), {\"Region\"})", outM);
    }

    [Fact]
    public void MoveColumn_After_InsertsAtRefPositionPlusOne()
    {
        string outM = MTransformBuilder.MoveColumn(Let, "Region", "after", "Amount");
        Assert.Contains("List.PositionOf(List.RemoveItems(Table.ColumnNames(Source), {\"Region\"}), \"Amount\") + 1", outM);
    }

    [Fact]
    public void MoveColumn_BeforeWithoutRef_Throws()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.MoveColumn(Let, "Region", "before", null));

    [Fact]
    public void MoveColumn_UnknownPosition_Throws()
        => Assert.Throws<ArgumentException>(() => MTransformBuilder.MoveColumn(Let, "Region", "diagonally", null));

    // ============================================================ Wave L: conditional column

    [Fact]
    public void AddConditionalColumn_BuildsNestedIfThenElseChain()
    {
        var rules = new List<(string, string, string?, string?)>
        {
            ("Score", "ge", "90", "A"),
            ("Score", "ge", "80", "B"),
        };
        string outM = MTransformBuilder.AddConditionalColumn(Let, "Grade", rules, "C", "number", "text");
        Assert.Contains("#\"Added Conditional Column\" = Table.AddColumn(Source, \"Grade\", each if [Score] >= 90 then \"A\" else if [Score] >= 80 then \"B\" else \"C\", type text)", outM);
    }

    [Fact]
    public void AddConditionalColumn_TextOps_UseTextFunctions()
    {
        var rules = new List<(string, string, string?, string?)>
        {
            ("Name", "contains", "Ltd", "Company"),
        };
        string outM = MTransformBuilder.AddConditionalColumn(Let, "Kind", rules, "Person", "text", "text");
        Assert.Contains("each if Text.Contains([Name], \"Ltd\") then \"Company\" else \"Person\"", outM);
    }

    [Fact]
    public void AddConditionalColumn_NumericResults_AreUnquoted()
    {
        var rules = new List<(string, string, string?, string?)> { ("Flag", "eq", "true", "1") };
        string outM = MTransformBuilder.AddConditionalColumn(Let, "N", rules, "0", "logical", "number");
        Assert.Contains("each if [Flag] = true then 1 else 0, type number", outM);
    }

    [Fact]
    public void AddConditionalColumn_RejectsNoRules()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.AddConditionalColumn(Let, "X", new List<(string, string, string?, string?)>(), "z", "text", "text"));

    [Fact]
    public void AddConditionalColumn_RejectsUnknownOp()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.AddConditionalColumn(Let, "X",
                new List<(string, string, string?, string?)> { ("A", "approximately", "1", "y") }, "z", "number", "text"));

    // ============================================================ Wave L: fuzzy merge / group / cluster

    [Fact]
    public void FuzzyMerge_EmitsFuzzyNestedJoinWithOptions()
    {
        string outM = MTransformBuilder.FuzzyMerge(Let, "Dim_Customer", new[] { "Name" }, new[] { "CustName" },
            "LeftOuter", 0.85, true, false, null, null);
        Assert.Contains("#\"Fuzzy Merged Queries\" = Table.FuzzyNestedJoin(Source, {\"Name\"}, Dim_Customer, {\"CustName\"}, \"Dim_Customer\", [JoinKind=JoinKind.LeftOuter, Threshold=0.85, IgnoreCase=true, IgnoreSpace=false])", outM);
        Assert.EndsWith("in\n    #\"Fuzzy Merged Queries\"", outM);
    }

    [Fact]
    public void FuzzyMerge_WithTransformationTableAndExpand()
    {
        string outM = MTransformBuilder.FuzzyMerge(Let, "Dim", new[] { "K" }, new[] { "K" }, "Inner",
            0.8, null, null, "Aliases", new[] { "Label" });
        Assert.Contains("TransformationTable=Aliases]", outM);
        Assert.Contains("#\"Expanded Dim\" = Table.ExpandTableColumn(#\"Fuzzy Merged Queries\", \"Dim\", {\"Label\"}, {\"Label\"})", outM);
        Assert.EndsWith("in\n    #\"Expanded Dim\"", outM);
    }

    [Fact]
    public void FuzzyMerge_RejectsMismatchedKeyCounts()
        => Assert.Throws<ArgumentException>(() =>
            MTransformBuilder.FuzzyMerge(Let, "R", new[] { "a", "b" }, new[] { "x" }, "Inner", 0.8, null, null, null, null));

    [Fact]
    public void FuzzyGroup_EmitsFuzzyGroupWithThreshold()
    {
        var aggs = new List<(string, string, string?)> { ("Rows", "Count", null) };
        string outM = MTransformBuilder.FuzzyGroup(Let, new[] { "Company" }, aggs, 0.75);
        Assert.Contains("#\"Fuzzy Grouped Rows\" = Table.FuzzyGroup(Source, \"Company\", {{\"Rows\", each Table.RowCount(_), Int64.Type}}, [Threshold=0.75])", outM);
    }

    [Fact]
    public void FuzzyGroup_NoThreshold_OmitsOptions()
    {
        var aggs = new List<(string, string, string?)> { ("Total", "Sum", "Amount") };
        string outM = MTransformBuilder.FuzzyGroup(Let, new[] { "A", "B" }, aggs, null);
        Assert.Contains("Table.FuzzyGroup(Source, {\"A\", \"B\"}, {{\"Total\", each List.Sum(List.Transform(_, each _[Amount])), type number}})", outM);
        Assert.DoesNotContain("Threshold", outM);
    }

    [Fact]
    public void FuzzyClusterColumn_EmitsAddFuzzyClusterColumn()
    {
        string outM = MTransformBuilder.FuzzyClusterColumn(Let, "Company", "Company (clustered)", 0.7);
        Assert.Contains("#\"Added Fuzzy Cluster\" = Table.AddFuzzyClusterColumn(Source, \"Company\", \"Company (clustered)\", [Threshold=0.7])", outM);
    }

    [Fact]
    public void FuzzyClusterColumn_NoThreshold_OmitsOptions()
        => Assert.Contains("Table.AddFuzzyClusterColumn(Source, \"Name\", \"NameKey\")",
            MTransformBuilder.FuzzyClusterColumn(Let, "Name", "NameKey", null));

    // ============================================================ Wave L: extended connectors

    [Fact]
    public void SourceStep_Json_FromFile()
        => Assert.Equal("Json.Document(File.Contents(\"c:\\d.json\"))",
            MTransformBuilder.SourceStep("json", new Dictionary<string, string> { ["path"] = "c:\\d.json" }));

    [Fact]
    public void SourceStep_Json_FromWeb()
        => Assert.Equal("Json.Document(Web.Contents(\"https://api.test/x\"))",
            MTransformBuilder.SourceStep("json", new Dictionary<string, string> { ["url"] = "https://api.test/x" }));

    [Fact]
    public void SourceStep_Xml_FromFile()
        => Assert.Equal("Xml.Tables(File.Contents(\"c:\\d.xml\"))",
            MTransformBuilder.SourceStep("xml", new Dictionary<string, string> { ["path"] = "c:\\d.xml" }));

    [Fact]
    public void SourceStep_Pdf_FromFile()
        => Assert.Equal("Pdf.Tables(File.Contents(\"c:\\d.pdf\"))",
            MTransformBuilder.SourceStep("pdf", new Dictionary<string, string> { ["path"] = "c:\\d.pdf" }));

    [Fact]
    public void SourceStep_Html_FromUrl()
        => Assert.Equal("Html.Table(Web.Contents(\"https://x.test\"), {})",
            MTransformBuilder.SourceStep("html", new Dictionary<string, string> { ["url"] = "https://x.test" }));

    [Fact]
    public void SourceStep_AnalysisServices_WithDaxQuery()
    {
        var p = new Dictionary<string, string> { ["server"] = "asazure://x", ["database"] = "Model", ["query"] = "EVALUATE Sales" };
        Assert.Equal("AnalysisServices.Database(\"asazure://x\", \"Model\", [Query=\"EVALUATE Sales\"])",
            MTransformBuilder.SourceStep("analysisservices", p));
    }

    [Fact]
    public void SourceStep_Oracle_MySql_Postgres_Db2()
    {
        Assert.Equal("Oracle.Database(\"orcl\", \"HR\")",
            MTransformBuilder.SourceStep("oracle", new Dictionary<string, string> { ["server"] = "orcl", ["database"] = "HR" }));
        Assert.Equal("MySQL.Database(\"db01\", \"sales\")",
            MTransformBuilder.SourceStep("mysql", new Dictionary<string, string> { ["server"] = "db01", ["database"] = "sales" }));
        Assert.Equal("PostgreSQL.Database(\"pg01\", \"sales\")",
            MTransformBuilder.SourceStep("postgresql", new Dictionary<string, string> { ["server"] = "pg01", ["database"] = "sales" }));
        Assert.Equal("Db2.Database(\"db2srv\", \"WH\")",
            MTransformBuilder.SourceStep("db2", new Dictionary<string, string> { ["server"] = "db2srv", ["database"] = "WH" }));
    }

    [Fact]
    public void SourceStep_Odbc_WithConnectionStringAndQuery()
    {
        var p = new Dictionary<string, string> { ["connectionString"] = "dsn=Prod", ["query"] = "SELECT 1" };
        Assert.Equal("Odbc.Query(\"dsn=Prod\", \"SELECT 1\")", MTransformBuilder.SourceStep("odbc", p));
    }

    [Fact]
    public void SourceStep_Odbc_DsnOnly_UsesDataSource()
        => Assert.Equal("Odbc.DataSource(\"MyDsn\")",
            MTransformBuilder.SourceStep("odbc", new Dictionary<string, string> { ["dsn"] = "MyDsn" }));

    [Fact]
    public void SourceStep_OleDb_DataSource()
        => Assert.Equal("OleDb.DataSource(\"Provider=SQLOLEDB;...\")",
            MTransformBuilder.SourceStep("oledb", new Dictionary<string, string> { ["connectionString"] = "Provider=SQLOLEDB;..." }));

    [Fact]
    public void SourceStep_AzureTable_DataLake_DeltaLake_Cdm()
    {
        Assert.Equal("AzureStorage.Tables(\"acct\")",
            MTransformBuilder.SourceStep("azuretable", new Dictionary<string, string> { ["account"] = "acct" }));
        Assert.Equal("AzureStorage.DataLake(\"https://lake.dfs.core.windows.net/fs\")",
            MTransformBuilder.SourceStep("datalake", new Dictionary<string, string> { ["url"] = "https://lake.dfs.core.windows.net/fs" }));
        Assert.Equal("DeltaLake.Table(AzureStorage.DataLake(\"https://lake.dfs.core.windows.net/fs/t\"))",
            MTransformBuilder.SourceStep("deltalake", new Dictionary<string, string> { ["url"] = "https://lake.dfs.core.windows.net/fs/t" }));
        Assert.Equal("Cdm.Contents(\"https://lake/cdm\")",
            MTransformBuilder.SourceStep("cdm", new Dictionary<string, string> { ["url"] = "https://lake/cdm" }));
    }

    [Fact]
    public void SourceStep_Web_WithQueryRecordAndPostContent()
    {
        var p = new Dictionary<string, string>
        {
            ["url"] = "https://api.example.com",
            ["relativePath"] = "v1/items",
            ["query"] = "[#\"$top\"=\"10\"]",
            ["content"] = "Text.ToBinary(\"{}\")",
            ["manualStatusHandling"] = "404, 500",
        };
        string outM = MTransformBuilder.SourceStep("web", p);
        Assert.Equal("Web.Contents(\"https://api.example.com\", [RelativePath=\"v1/items\", Query=[#\"$top\"=\"10\"], Content=Text.ToBinary(\"{}\"), ManualStatusHandling={404, 500}])", outM);
    }

    [Fact]
    public void SourceStep_OData_FoldsFilterSelectTop()
    {
        var p = new Dictionary<string, string>
        {
            ["url"] = "https://svc/odata/Orders",
            ["filter"] = "Amount gt 100",
            ["select"] = "Id,Amount",
            ["top"] = "50",
        };
        string outM = MTransformBuilder.SourceStep("odata", p);
        Assert.Equal("OData.Feed(\"https://svc/odata/Orders\", null, [Query=[#\"$filter\"=\"Amount gt 100\", #\"$select\"=\"Id,Amount\", #\"$top\"=\"50\"]])", outM);
    }

    [Fact]
    public void SourceStep_OData_PlainUrl_StillBare()
        => Assert.Equal("OData.Feed(\"https://svc/odata\")",
            MTransformBuilder.SourceStep("odata", new Dictionary<string, string> { ["url"] = "https://svc/odata" }));
}
