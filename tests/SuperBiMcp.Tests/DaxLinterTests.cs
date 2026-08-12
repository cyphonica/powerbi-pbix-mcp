using System.Linq;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive tests for the Wave G3 PURE OFFLINE DAX linter: the tokenizer (comments,
/// strings, quoted tables, bracket refs, line numbers), every lint rule firing on its trigger and
/// staying silent on clean DAX, the known-function catalogue (hallucination catching, INFO.*
/// prefix, extra admissions), and the concrete before/after rewrites with the full suggested
/// expression. No live engine anywhere - removing a rule's check or a rewrite breaks its test.
/// </summary>
public sealed class DaxLinterTests
{
    private static List<DaxLinter.Finding> Lint(string dax, params string[] extra) =>
        DaxLinter.Lint(dax, extra.Length == 0 ? null : extra);

    private static List<string> Rules(string dax) => Lint(dax).Select(f => f.Rule).ToList();

    // ---------------------------------------------------------------- tokenizer

    [Fact]
    public void Tokenizer_Handles_Comments_Strings_Tables_Brackets_And_Lines()
    {
        var toks = DaxLinter.Tokenize(
            "// line comment\n" +
            "SUM ( 'My Table'[My Col] ) -- trailing\n" +
            "/* block\ncomment */ + \"a ) \"\" string\"");

        Assert.DoesNotContain(toks, t => t.Text.Contains("comment"));
        var sum = toks.First(t => t.Text == "SUM");
        Assert.Equal(2, sum.Line);
        Assert.Equal("'My Table'", toks.First(t => t.Kind == DaxLinter.TokKind.TableRef).Text);
        Assert.Equal("[My Col]", toks.First(t => t.Kind == DaxLinter.TokKind.BracketRef).Text);
        var str = toks.First(t => t.Kind == DaxLinter.TokKind.Str);
        Assert.Equal("\"a ) \"\" string\"", str.Text);
        Assert.Equal(4, str.Line);                              // after the 2-line block comment
    }

    [Fact]
    public void Tokenizer_TwoCharOperators_StayWhole()
    {
        var toks = DaxLinter.Tokenize("[A] <> [B] && [C] >= 1");
        var ops = toks.Where(t => t.Kind == DaxLinter.TokKind.Op).Select(t => t.Text).ToList();
        Assert.Equal(new[] { "<>", "&&", ">=" }, ops);
    }

    // ---------------------------------------------------------------- unknown function (hallucination catcher)

    [Fact]
    public void UnknownFunction_Flagged_KnownAndInfoAndExtra_Admitted()
    {
        // the classic hallucination: a plausible-sounding function that does not exist
        var findings = Lint("SUMMARIZEBY ( Sales, Sales[Region] )");
        var f = Assert.Single(findings, x => x.Rule == "UNKNOWN_FUNCTION");
        Assert.Equal("error", f.Severity);
        Assert.Contains("SUMMARIZEBY", f.Message);

        Assert.DoesNotContain("UNKNOWN_FUNCTION", Rules("SUM ( Sales[Amount] )"));
        Assert.DoesNotContain("UNKNOWN_FUNCTION", Rules("EVALUATE INFO.VIEW.MEASURES ( )"));
        // a model UDF admitted via extras
        Assert.Contains(Lint("MyUdf ( 1 )"), x => x.Rule == "UNKNOWN_FUNCTION");
        Assert.DoesNotContain(Lint("MyUdf ( 1 )", "MyUdf"), x => x.Rule == "UNKNOWN_FUNCTION");
    }

    [Fact]
    public void UnknownFunction_NotTripped_By_TableNames_Vars_Or_MeasureRefs()
    {
        // bare table args, VAR names and [Measure] refs are not function calls
        var rules = Rules("VAR t = FILTER ( ALL ( Sales ), [Total Sales] > 0 ) RETURN COUNTROWS ( t )");
        Assert.DoesNotContain("UNKNOWN_FUNCTION", rules);
    }

    [Fact]
    public void UnknownFunction_CaseInsensitive()
    {
        Assert.DoesNotContain("UNKNOWN_FUNCTION", Rules("sum ( Sales[Amount] )"));
        Assert.DoesNotContain("UNKNOWN_FUNCTION", Rules("Divide ( 1, 2 )"));
    }

    // ---------------------------------------------------------------- FILTER(wholeTable) in CALCULATE

    [Fact]
    public void FilterWholeTable_InCalculate_Flagged_WithLine()
    {
        var findings = Lint("CALCULATE (\n    SUM ( Sales[Amount] ),\n    FILTER ( Sales, Sales[Region] = \"NZ\" )\n)");
        var f = Assert.Single(findings, x => x.Rule == "FILTER_WHOLE_TABLE_IN_CALCULATE");
        Assert.Equal(3, f.Line);
        Assert.Equal("warning", f.Severity);
        Assert.Contains("Sales", f.Message);
    }

    [Fact]
    public void FilterWholeTable_QuotedTable_AlsoFlagged()
    {
        Assert.Contains("FILTER_WHOLE_TABLE_IN_CALCULATE",
            Rules("CALCULATE ( [X], FILTER ( 'My Sales', 'My Sales'[R] = 1 ) )"));
    }

    [Fact]
    public void FilterWholeTable_NotFlagged_OutsideCalculate_OrOverAll()
    {
        // FILTER outside CALCULATE is an iterator input, not a filter argument
        Assert.DoesNotContain("FILTER_WHOLE_TABLE_IN_CALCULATE",
            Rules("COUNTROWS ( FILTER ( Sales, Sales[Amount] > 0 ) )"));
        // FILTER ( ALL ( col ), ... ) is the recommended shape - first arg is a call, not a bare table
        Assert.DoesNotContain("FILTER_WHOLE_TABLE_IN_CALCULATE",
            Rules("CALCULATE ( [X], FILTER ( ALL ( Sales[Region] ), Sales[Region] = \"NZ\" ) )"));
    }

    // ---------------------------------------------------------------- nested CALCULATE

    [Fact]
    public void NestedCalculate_Flagged_AtAnyDepth()
    {
        Assert.Contains("NESTED_CALCULATE",
            Rules("CALCULATE ( CALCULATE ( SUM ( Sales[Amount] ) ) )"));
        Assert.Contains("NESTED_CALCULATE",
            Rules("CALCULATE ( SUMX ( Sales, CALCULATE ( [X] ) ) )"));
        Assert.DoesNotContain("NESTED_CALCULATE",
            Rules("CALCULATE ( SUM ( Sales[Amount] ), Sales[Y] = 1 ) + CALCULATE ( [X] )"));
    }

    // ---------------------------------------------------------------- division / IFERROR / +0 / EARLIER / SUMMARIZE

    [Fact]
    public void DivisionOperator_Info_ButNotInComments_OrStrings()
    {
        var findings = Lint("[A] / [B]");
        var f = Assert.Single(findings, x => x.Rule == "DIVISION_OPERATOR");
        Assert.Equal("info", f.Severity);
        Assert.Empty(Lint("DIVIDE ( [A], [B] ) // was [A] / [B]"));
        Assert.Empty(Lint("\"miles/hour\" & FORMAT ( [A], \"0\" )"));
    }

    [Fact]
    public void Iferror_Earlier_Summarize_Flagged()
    {
        Assert.Contains("IFERROR_WRAPPING", Rules("IFERROR ( [A] / [B], 0 )"));
        Assert.Contains("EARLIER_USAGE", Rules("FILTER ( Sales, Sales[V] = EARLIER ( Sales[V] ) )"));
        Assert.Contains("SUMMARIZE_AGGREGATION",
            Rules("SUMMARIZE ( Sales, Sales[Region], \"Total\", SUM ( Sales[Amount] ) )"));
        // SUMMARIZE for pure grouping is fine; nested strings inside a deeper call do not trip it
        Assert.DoesNotContain("SUMMARIZE_AGGREGATION", Rules("SUMMARIZE ( Sales, Sales[Region] )"));
        Assert.DoesNotContain("SUMMARIZE_AGGREGATION",
            Rules("ADDCOLUMNS ( SUMMARIZE ( Sales, Sales[Region] ), \"T\", [Total Sales] )"));
    }

    [Fact]
    public void PlusZero_Flagged_TrailingAndLeading_ButNotRealAddition()
    {
        Assert.Contains("PLUS_ZERO", Rules("( SUM ( Sales[Amount] ) + 0 )"));
        Assert.Contains("PLUS_ZERO", Rules("DIVIDE ( [A] + 0, [B] )"));
        Assert.Contains("PLUS_ZERO", Rules("CALCULATE ( 0 + [A] )"));
        Assert.DoesNotContain("PLUS_ZERO", Rules("[A] + 0.5"));
        Assert.DoesNotContain("PLUS_ZERO", Rules("[A] + 10"));
    }

    [Fact]
    public void CleanMeasure_NoFindings()
    {
        Assert.Empty(Lint(
            "VAR total = CALCULATE ( SUM ( Sales[Amount] ), KEEPFILTERS ( Sales[Region] = \"NZ\" ) )\n" +
            "RETURN DIVIDE ( total, [All Sales] )"));
    }

    [Fact]
    public void Lint_EmptyExpression_Throws()
    {
        Assert.Throws<ArgumentException>(() => DaxLinter.Lint("  "));
    }

    // ---------------------------------------------------------------- rewrites

    [Fact]
    public void Rewrite_Division_ToDivide_WithFunctionOperands()
    {
        var (rewrites, _, suggested) = DaxLinter.SuggestRewrite("SUM ( Sales[Amount] ) / COUNTROWS ( Sales )");
        var r = Assert.Single(rewrites, x => x.Rule == "DIVISION_OPERATOR");
        Assert.Equal("SUM ( Sales[Amount] ) / COUNTROWS ( Sales )", r.Before);
        Assert.Equal("DIVIDE(SUM ( Sales[Amount] ), COUNTROWS ( Sales ))", r.After);
        Assert.Equal("DIVIDE(SUM ( Sales[Amount] ), COUNTROWS ( Sales ))", suggested);
    }

    [Fact]
    public void Rewrite_Division_OperandBoundaries_StopAtLooserOperators()
    {
        var (rewrites, _, _) = DaxLinter.SuggestRewrite("[A] + [B] / [C] - [D]");
        var r = Assert.Single(rewrites);
        Assert.Equal("[B] / [C]", r.Before);
        Assert.Equal("DIVIDE([B], [C])", r.After);
    }

    [Fact]
    public void Rewrite_Iferror_Division_ToDivide_WithAlternate()
    {
        var (rewrites, _, suggested) = DaxLinter.SuggestRewrite("IFERROR ( [A] / [B], 0 )");
        var r = Assert.Single(rewrites, x => x.Rule == "IFERROR_WRAPPING");
        Assert.Equal("DIVIDE([A], [B], 0)", r.After);
        Assert.Equal("DIVIDE([A], [B], 0)", suggested);
        // the division INSIDE the rewritten IFERROR is not rewritten a second time
        Assert.DoesNotContain(rewrites, x => x.Rule == "DIVISION_OPERATOR");

        var (r2, _, s2) = DaxLinter.SuggestRewrite("IFERROR ( [A] / [B], BLANK ( ) )");
        Assert.Equal("DIVIDE([A], [B])", Assert.Single(r2, x => x.Rule == "IFERROR_WRAPPING").After);
        Assert.Equal("DIVIDE([A], [B])", s2);
    }

    [Fact]
    public void Rewrite_PlusZero_Removed()
    {
        var (rewrites, _, suggested) = DaxLinter.SuggestRewrite("( SUM ( Sales[Amount] ) + 0 )");
        Assert.Single(rewrites, x => x.Rule == "PLUS_ZERO");
        Assert.Equal("( SUM ( Sales[Amount] ) )", suggested);
    }

    [Fact]
    public void Rewrite_FilterWholeTable_SimplePredicate_Collapsed()
    {
        var (rewrites, _, suggested) = DaxLinter.SuggestRewrite(
            "CALCULATE ( [X], FILTER ( Sales, Sales[Region] = \"NZ\" ) )");
        var r = Assert.Single(rewrites, x => x.Rule == "FILTER_WHOLE_TABLE_IN_CALCULATE");
        Assert.Equal("Sales[Region] = \"NZ\"", r.After);
        Assert.Equal("CALCULATE ( [X], Sales[Region] = \"NZ\" )", suggested);
    }

    [Fact]
    public void Rewrite_FilterWholeTable_ComplexPredicate_NoteOnly()
    {
        // a predicate touching a measure has no safe mechanical collapse - hint note instead
        var (rewrites, notes, _) = DaxLinter.SuggestRewrite(
            "CALCULATE ( [X], FILTER ( Sales, [Total Sales] > 100 ) )");
        Assert.DoesNotContain(rewrites, r => r.Rule == "FILTER_WHOLE_TABLE_IN_CALCULATE");
        Assert.Contains(notes, n => n.Rule == "FILTER_WHOLE_TABLE_IN_CALCULATE");
    }

    [Fact]
    public void Rewrite_HintOnlyRules_SurfaceAsNotes()
    {
        var (rewrites, notes, suggested) = DaxLinter.SuggestRewrite(
            "SUMX ( FILTER ( Sales, Sales[V] = EARLIER ( Sales[V] ) ), Sales[V] )");
        Assert.Empty(rewrites);
        Assert.Contains(notes, n => n.Rule == "EARLIER_USAGE");
        Assert.Equal("SUMX ( FILTER ( Sales, Sales[V] = EARLIER ( Sales[V] ) ), Sales[V] )", suggested);
    }
}
