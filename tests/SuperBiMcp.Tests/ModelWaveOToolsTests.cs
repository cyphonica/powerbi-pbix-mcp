using System;
using System.Collections.Generic;
using System.Linq;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave O model-side helpers: the SVG-as-ImageUrl measure suite (SvgBuilder DAX + DataCategory=ImageUrl),
/// dynamic title text measure, static custom format string, the calc-group format switcher, and IBCS
/// variance measures. Each test builds an in-memory <c>new TOM.Model()</c>, calls the *Core mutation helper
/// or the pure SvgBuilder generator, and asserts on the resulting object tree / DAX - no live AS server is
/// needed. Fault-sensitive: assertions pin the exact DAX scaling, the encode-safety rules and the ImageUrl
/// category, so a regression breaks a specific test.
/// </summary>
public sealed class ModelWaveOToolsTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model", Culture = "en-US" };
        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        sales.Measures.Add(new TOM.Measure { Name = "Target", Expression = "100" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales PY", Expression = "CALCULATE([Total Sales], SAMEPERIODLASTYEAR('Calendar'[Date]))" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = #table({},{}) in Source" } });
        model.Tables.Add(sales);

        var calendar = new TOM.Table { Name = "Calendar" };
        calendar.Columns.Add(new TOM.DataColumn { Name = "Month", DataType = TOM.DataType.String, SourceColumn = "Month" });
        calendar.Columns.Add(new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime, SourceColumn = "Date" });
        calendar.Partitions.Add(new TOM.Partition { Name = "Calendar", Source = new TOM.MPartitionSource { Expression = "let Source = #table({},{}) in Source" } });
        model.Tables.Add(calendar);

        // a single-column table to host a calculation group (object-tree only; no SaveChanges).
        var cg = new TOM.Table { Name = "Format Switch" };
        cg.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name" });
        model.Tables.Add(cg);
        return model;
    }

    // ====================================================================== SvgBuilder encode-safety
    [Fact]
    public void EncodeColor_ReplacesHashAndQuotes()
    {
        Assert.Equal("%232E86AB", SvgBuilder.EncodeColor("#2E86AB"));
        // the # is %23-encoded and any embedded double-quote becomes a single quote (SVG-attribute safe).
        Assert.Equal("'%23FF0000'", SvgBuilder.EncodeColor("\"#FF0000\""));
        Assert.DoesNotContain("\"", SvgBuilder.EncodeColor("\"#FF0000\""));
        // a missing colour falls back to a grey, never an empty/illegal value
        Assert.StartsWith("%23", SvgBuilder.EncodeColor(null));
    }

    [Fact]
    public void MeasureRef_NormalisesToBareBrackets()
    {
        Assert.Equal("[Total Sales]", SvgBuilder.MeasureRef("Total Sales"));
        Assert.Equal("[Total Sales]", SvgBuilder.MeasureRef("[Total Sales]"));
        Assert.Equal("[Total Sales]", SvgBuilder.MeasureRef("Sales[Total Sales]"));   // table qualifier dropped
    }

    [Fact]
    public void ColumnRef_NormalisesToQuotedTable()
    {
        Assert.Equal("'Calendar'[Month]", SvgBuilder.ColumnRef("Calendar[Month]"));
        Assert.Equal("'Calendar'[Month]", SvgBuilder.ColumnRef("'Calendar'[Month]"));
        Assert.Throws<ArgumentException>(() => SvgBuilder.ColumnRef("NoBracket"));
    }

    // ====================================================================== add_svg_databar
    [Fact]
    public void AddSvgDatabar_AuthorsScaledRect_AndSetsImageUrlCategory()
    {
        var model = NewModel();
        var dax = SvgBuilder.DataBar("Total Sales", "Target", "#2E86AB", "#D7263D", 120, 16, "left");
        var me = ModelService.AddSvgMeasureCore(model, "Sales", "Bar", dax);

        // FAULT-SENSITIVE: the measure must carry the ImageUrl data category so PBI renders the SVG.
        Assert.Equal("ImageUrl", me.DataCategory);
        // the DAX returns the data-URI prefix + an <svg> with a <rect> whose width scales to value/max.
        Assert.Contains("data:image/svg+xml;utf8,", me.Expression);
        Assert.Contains("<rect", me.Expression);
        Assert.Contains("DIVIDE ( ABS ( _v ), _max )", me.Expression);
        // encode-safety: the # in the colours is %23 and there are no raw double-quote attributes inside the svg.
        Assert.Contains("%232E86AB", me.Expression);
        Assert.Contains("%23D7263D", me.Expression);   // negative fill
        Assert.DoesNotContain("#2E86AB", me.Expression);
    }

    [Fact]
    public void AddSvgDatabar_RightAlign_GrowsLeftwards()
    {
        var dax = SvgBuilder.DataBar("Total Sales", "Target", "#2E86AB", null, 100, 16, "right");
        // a right-aligned bar offsets x by (width - barwidth)
        Assert.Contains("(100 - _w)", dax);
    }

    [Fact]
    public void AddSvgMeasure_DuplicateName_Throws()
    {
        var model = NewModel();
        ModelService.AddSvgMeasureCore(model, "Sales", "Bar", "\"x\"");
        Assert.Throws<InvalidOperationException>(() => ModelService.AddSvgMeasureCore(model, "Sales", "Bar", "\"y\""));
    }

    // ====================================================================== add_svg_sparkline
    [Fact]
    public void Sparkline_BuildsMinMaxScaledPolyline()
    {
        var dax = SvgBuilder.Sparkline("Total Sales", "Calendar[Month]", "line", showLastPoint: true, intercept: true, "#2E86AB", 100, 24);
        Assert.Contains("<polyline", dax);
        Assert.Contains("CONCATENATEX", dax);
        // min-max scaling: y = h - (v - min)/range * h
        Assert.Contains("DIVIDE ( [@v] - _min, _range )", dax);
        Assert.Contains("VALUES ( 'Calendar'[Month] )", dax);
        Assert.Contains("<circle", dax);          // showLastPoint marker
        Assert.Contains("stroke-dasharray", dax); // intercept baseline
    }

    [Fact]
    public void Sparkline_GradientArea_EmitsGradientFillAndPolygon()
    {
        var dax = SvgBuilder.Sparkline("Total Sales", "Calendar[Month]", "gradient-area", false, false, "#2E86AB", 100, 24);
        Assert.Contains("linearGradient", dax);
        Assert.Contains("<polygon", dax);
        Assert.Contains("url(%23g)", dax);   // the gradient id reference, # encoded
    }

    // ====================================================================== add_svg_progress_bar / gauge / icon / chip
    [Fact]
    public void ProgressBar_Bullet_AddsTrackFillAndTargetTick()
    {
        var dax = SvgBuilder.ProgressBar("Total Sales", "Target", "bullet", "#E6E9EF", "#2E7D32", 100, 14);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(dax, "<rect").Count);   // track + fill
        Assert.Contains("DIVIDE ( _v, _t )", dax);
        Assert.Contains("<line", dax);   // bullet target tick
    }

    [Fact]
    public void ProgressBar_Bar_HasNoTargetTick()
    {
        var dax = SvgBuilder.ProgressBar("Total Sales", "Target", "bar", "#E6E9EF", "#2E7D32", 100, 14);
        Assert.DoesNotContain("<line", dax);
    }

    [Fact]
    public void Gauge_WithThresholds_PicksBandColour()
    {
        var thresholds = new List<(double, string)> { (50, "#E8A93B"), (80, "#2E7D32") };
        var dax = SvgBuilder.Gauge("Total Sales", 0, 100, thresholds, "#D7263D", 60);
        Assert.Contains("<path", dax);                       // background + value arcs
        Assert.Contains("IF ( _v >= 80, \"%232E7D32\"", dax); // highest band first in the nested IF
        Assert.Contains("IF ( _v >= 50, \"%23E8A93B\"", dax);
        Assert.Contains("DIVIDE ( _v - _min, _max - _min )", dax);
    }

    [Fact]
    public void Gauge_NoThresholds_UsesPlainFill()
    {
        var dax = SvgBuilder.Gauge("Total Sales", 0, 100, null, "#2E86AB", 60);
        Assert.Contains("VAR _fill = \"%232E86AB\"", dax);
        Assert.DoesNotContain("IF ( _v >=", dax);
    }

    [Fact]
    public void Icon_NestedBandPicker_FirstMatchingRuleWins()
    {
        var rules = new List<(double, double, string?, string?)>
        {
            (-1e9, 0, "▼", "#D7263D"),
            (0, 1e9, "▲", "#2E7D32"),
        };
        var dax = SvgBuilder.Icon("Total Sales", rules, 16);
        Assert.Contains("<text", dax);
        // both bands are wired as half-open [min,max) conditions
        Assert.Contains("_v >= 0 && _v < 1000000000", dax);
        Assert.Contains("\"%232E7D32\"", dax);
        Assert.Contains("\"%23D7263D\"", dax);
    }

    [Fact]
    public void Icon_NoRules_Throws()
    {
        Assert.Throws<ArgumentException>(() => SvgBuilder.Icon("Total Sales", new List<(double, double, string?, string?)>(), 16));
    }

    [Fact]
    public void Chip_AutoSizesFromText_AndUsesColorMeasureWhenGiven()
    {
        var withColour = SvgBuilder.Chip("Status Text", "Status Colour", "#2E86AB", 20);
        Assert.Contains("LEN ( _txt )", withColour);                 // width auto-sizes from text length
        Assert.Contains("SUBSTITUTE ( [Status Colour], \"#\", \"%23\" )", withColour);   // runtime # encoding
        Assert.Contains("<rect", withColour);
        Assert.Contains("<text", withColour);

        var fixedColour = SvgBuilder.Chip("Status Text", null, "#2E86AB", 20);
        Assert.Contains("VAR _fill = \"%232E86AB\"", fixedColour);
    }

    // ====================================================================== dynamic title measure
    [Fact]
    public void BuildDynamicTitleDax_WithTemplate_ConcatsAroundSelectedValue()
    {
        var dax = ModelService.BuildDynamicTitleDax("Dim_Product[Category]", "Sales - {value}", "All categories");
        Assert.Contains("SELECTEDVALUE ( 'Dim_Product'[Category], \"All categories\" )", dax);
        Assert.StartsWith("\"Sales - \" & ", dax);   // literal prefix concatenated with the selected value
    }

    [Fact]
    public void BuildDynamicTitleDax_NoTemplate_IsBareSelectedValue()
    {
        var dax = ModelService.BuildDynamicTitleDax("Calendar[Month]", null, null);
        Assert.Equal("SELECTEDVALUE ( 'Calendar'[Month], \"All\" )", dax);
    }

    [Fact]
    public void AddDynamicTitleMeasure_BadColumn_Throws()
    {
        var dax_ok = ModelService.BuildDynamicTitleDax("Calendar[Month]", "{value} sales", null);
        Assert.Contains("SELECTEDVALUE", dax_ok);
        Assert.Throws<InvalidOperationException>(() => ModelService.BuildDynamicTitleDax("NoBracket", null, null));
    }

    // ====================================================================== static custom format string
    [Fact]
    public void SetCustomFormatString_WritesStaticFormatString()
    {
        var model = NewModel();
        var me = ModelService.SetCustomFormatStringCore(model, "Sales", "Total Sales", "[Green]▲ #,0;[Red]▼ #,0;0");
        Assert.Equal("[Green]▲ #,0;[Red]▼ #,0;0", me.FormatString);
    }

    [Fact]
    public void SetCustomFormatString_UnknownMeasure_Throws()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetCustomFormatStringCore(model, "Sales", "Nope", "0"));
    }

    // ====================================================================== calc-group format switcher
    [Fact]
    public void AddCalcGroupFormat_ItemsOverrideFormatString_WithSelectedMeasure()
    {
        var model = NewModel();
        var items = new List<(string, string)> { ("Currency", "$#,0"), ("Percent", "0.0%") };
        var cg = ModelService.AddCalcGroupFormatCore(model, "Format Switch", items, precedence: 10);

        Assert.Equal(10, cg.Precedence);
        Assert.Equal(2, cg.CalculationItems.Count);
        var currency = cg.CalculationItems.Find("Currency")!;
        // each item keeps the value (SELECTEDMEASURE) and overrides the format via a string-literal definition.
        Assert.Equal("SELECTEDMEASURE()", currency.Expression);
        Assert.Equal("\"$#,0\"", currency.FormatStringDefinition!.Expression);
        Assert.Equal(0, currency.Ordinal);
        Assert.Equal(1, cg.CalculationItems.Find("Percent")!.Ordinal);
    }

    [Fact]
    public void AddCalcGroupFormat_NoItems_Throws()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddCalcGroupFormatCore(model, "Format Switch", new List<(string, string)>(), null));
    }

    // ====================================================================== IBCS variance measures
    [Fact]
    public void AddIbcsVarianceMeasure_Abs_BuildsAcMinusBase_WithSignFormat()
    {
        var model = NewModel();
        var created = ModelService.AddIbcsVarianceMeasureCore(model, "Sales", "Total Sales", "PY", "abs", null, applyIbcsFormat: true);

        string name = Assert.Single(created);
        var me = model.Tables.Find("Sales")!.Measures.Find(name)!;
        Assert.Equal("[Total Sales] - [Total Sales PY]", me.Expression);
        Assert.Equal("+#,0;-#,0", me.FormatString);   // IBCS leading-sign number format
    }

    [Fact]
    public void AddIbcsVarianceMeasure_Rel_BuildsDivideByAbsBase()
    {
        var model = NewModel();
        var created = ModelService.AddIbcsVarianceMeasureCore(model, "Sales", "Total Sales", "PY", "rel", null, applyIbcsFormat: true);
        var me = model.Tables.Find("Sales")!.Measures.Find(created[0])!;
        Assert.Equal("DIVIDE ( [Total Sales] - [Total Sales PY], ABS ( [Total Sales PY] ) )", me.Expression);
        Assert.Equal("+0.0%;-0.0%", me.FormatString);
    }

    [Fact]
    public void AddIbcsVarianceMeasure_ExplicitComparisonMeasure_IsUsed()
    {
        var model = NewModel();
        model.Tables.Find("Sales")!.Measures.Add(new TOM.Measure { Name = "Budget", Expression = "1000" });
        var created = ModelService.AddIbcsVarianceMeasureCore(model, "Sales", "Total Sales", "PL", "abs", "Budget", applyIbcsFormat: false);
        var me = model.Tables.Find("Sales")!.Measures.Find(created[0])!;
        Assert.Equal("[Total Sales] - [Budget]", me.Expression);
        Assert.True(string.IsNullOrEmpty(me.FormatString));   // no IBCS format applied
    }

    [Fact]
    public void AddIbcsVarianceMeasure_BadComparison_Throws()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddIbcsVarianceMeasureCore(model, "Sales", "Total Sales", "BUDGET", "abs", null, true));
    }

    [Fact]
    public void AddIbcsVarianceMeasure_BadKind_Throws()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddIbcsVarianceMeasureCore(model, "Sales", "Total Sales", "PY", "ratio", null, true));
    }
}
