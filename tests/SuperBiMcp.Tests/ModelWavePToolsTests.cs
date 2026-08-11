using System;
using System.Collections.Generic;
using System.Linq;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave P: community DAX comb - UDFs, INFO/profiling primitives, EVALUATEANDLOG inject/strip, and the
/// parameterised generator suite (time intelligence, running total, moving average, percent-of-total/parent,
/// rank, semi-additive, segmentation, ABC, dynamic Top N, calc groups, dynamic RLS, custom-calendar TI),
/// plus the field-parameter metadata verify. Each test either calls the pure <see cref="DaxGenerators"/>
/// builder (string asserts) or a ModelService *Core mutation helper against an in-memory <c>new TOM.Model()</c>;
/// no live Analysis Services server is needed. Fault-sensitive: assertions pin specific DAX functions,
/// extended-property metadata and the GroupBy binding, so a regression breaks a named test.
/// </summary>
public sealed class ModelWavePToolsTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model", Culture = "en-US" };

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Qty", DataType = TOM.DataType.Int64, SourceColumn = "Qty" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM ( 'Sales'[Amount] )" });
        model.Tables.Add(sales);

        var product = new TOM.Table { Name = "Product" };
        product.Columns.Add(new TOM.DataColumn { Name = "Category", DataType = TOM.DataType.String, SourceColumn = "Category" });
        product.Columns.Add(new TOM.DataColumn { Name = "Subcategory", DataType = TOM.DataType.String, SourceColumn = "Subcategory" });
        product.Columns.Add(new TOM.DataColumn { Name = "ProductKey", DataType = TOM.DataType.Int64, SourceColumn = "ProductKey" });
        model.Tables.Add(product);

        var calendar = new TOM.Table { Name = "Calendar" };
        calendar.Columns.Add(new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime, SourceColumn = "Date" });
        calendar.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name" });
        model.Tables.Add(calendar);

        var users = new TOM.Table { Name = "UserSecurity" };
        users.Columns.Add(new TOM.DataColumn { Name = "Email", DataType = TOM.DataType.String, SourceColumn = "Email" });
        users.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region" });
        users.Columns.Add(new TOM.DataColumn { Name = "Path", DataType = TOM.DataType.String, SourceColumn = "Path" });
        model.Tables.Add(users);

        var cgHost = new TOM.Table { Name = "Time Intelligence" };
        cgHost.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name" });
        model.Tables.Add(cgHost);

        return model;
    }

    // ====================================================================== A1. UDFs
    [Fact]
    public void UdfExpression_emits_typed_params_and_return_type()
    {
        var dax = DaxGenerators.UdfExpression("AddMargin",
            new List<(string, string)> { ("amount", "Scalar"), ("cost", "Scalar") },
            "amount - cost", "Scalar", "net margin");
        Assert.Contains("AddMargin = (amount: scalar val, cost: scalar val): scalar =>", dax);
        Assert.Contains("amount - cost", dax);
        Assert.StartsWith("/// net margin", dax);   // JSDoc doc comment
    }

    [Fact]
    public void UdfExpression_supports_untyped_params()
    {
        var dax = DaxGenerators.UdfExpression("Echo", new List<(string, string)> { ("x", "") }, "x", null, null);
        Assert.Contains("Echo = (x) =>", dax);
        Assert.DoesNotContain(":", dax.Split("=>")[0]);   // no type annotation on the param
    }

    [Fact]
    public void UdfExpression_rejects_unknown_param_type()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DaxGenerators.UdfExpression("F", new List<(string, string)> { ("x", "Bogus") }, "x", null, null));
    }

    [Fact]
    public void DefineUdfCore_adds_a_function_object_to_the_model()
    {
        var model = NewModel();
        string expr = DaxGenerators.UdfExpression("AddMargin",
            new List<(string, string)> { ("a", "Scalar"), ("b", "Scalar") }, "a - b", "Scalar", null);
        var fn = ModelService.DefineUdfCore(model, "AddMargin", expr, "desc");

        Assert.True(model.Functions.Contains("AddMargin"));
        Assert.Same(fn, model.Functions.Find("AddMargin"));
        Assert.Contains("a - b", fn.Expression);
        Assert.Equal("desc", fn.Description);
    }

    [Fact]
    public void DefineUdfCore_rejects_duplicate()
    {
        var model = NewModel();
        ModelService.DefineUdfCore(model, "F", "F = () => 1", null);
        Assert.Throws<InvalidOperationException>(() => ModelService.DefineUdfCore(model, "F", "F = () => 2", null));
    }

    // ====================================================================== A2/A3. INFO + COLUMNSTATISTICS
    [Theory]
    [InlineData("TABLES", "EVALUATE INFO.VIEW.TABLES()")]
    [InlineData("columns", "EVALUATE INFO.VIEW.COLUMNS()")]
    [InlineData("MEASURES", "EVALUATE INFO.VIEW.MEASURES()")]
    [InlineData("RELATIONSHIPS", "EVALUATE INFO.VIEW.RELATIONSHIPS()")]
    [InlineData("CALCDEPENDENCY", "EVALUATE INFO.CALCDEPENDENCY()")]
    [InlineData("STORAGETABLES", "EVALUATE INFO.STORAGETABLES()")]
    public void InfoViewQuery_maps_views_to_evaluate_text(string view, string expected)
        => Assert.Equal(expected, DaxGenerators.InfoViewQuery(view));

    [Fact]
    public void InfoViewQuery_rejects_empty_view()
        => Assert.Throws<InvalidOperationException>(() => DaxGenerators.InfoViewQuery(""));

    [Fact]
    public void ColumnStatisticsQuery_is_columnstatistics()
        => Assert.Equal("EVALUATE COLUMNSTATISTICS()", DaxGenerators.ColumnStatisticsQuery());

    // ====================================================================== A4. EVALUATEANDLOG
    [Fact]
    public void InjectEvaluateAndLog_wraps_and_labels()
    {
        var wrapped = DaxGenerators.InjectEvaluateAndLog("SUM ( 'Sales'[Amount] )", "sales-probe");
        Assert.StartsWith("EVALUATEANDLOG (", wrapped);
        Assert.Contains("SUM ( 'Sales'[Amount] )", wrapped);
        Assert.Contains("\"sales-probe\"", wrapped);
    }

    [Fact]
    public void InjectEvaluateAndLog_is_idempotent()
    {
        var once = DaxGenerators.InjectEvaluateAndLog("1 + 1", "x");
        var twice = DaxGenerators.InjectEvaluateAndLog(once, "x");
        Assert.Equal(once, twice);
    }

    [Fact]
    public void StripEvaluateAndLog_restores_inner_expression()
    {
        var wrapped = DaxGenerators.InjectEvaluateAndLog("SUM ( 'Sales'[Amount] )", "lbl");
        var stripped = DaxGenerators.StripEvaluateAndLog(wrapped);
        Assert.Equal("SUM ( 'Sales'[Amount] )", stripped);
        Assert.DoesNotContain("EVALUATEANDLOG", stripped);
        Assert.DoesNotContain("\"lbl\"", stripped);
    }

    [Fact]
    public void StripEvaluateAndLog_keeps_inner_function_calls_intact()
    {
        // ensure the matching-paren scan does not over-strip a body that itself has parens + commas
        var wrapped = DaxGenerators.InjectEvaluateAndLog("DIVIDE ( [A], [B] )", null);
        Assert.Equal("DIVIDE ( [A], [B] )", DaxGenerators.StripEvaluateAndLog(wrapped));
    }

    [Fact]
    public void InjectStrip_roundtrips_through_the_model_measure()
    {
        // model-side: a measure expression is wrapped then stripped via the *Core path's pure helpers.
        var model = NewModel();
        var me = model.Tables.Find("Sales")!.Measures.Find("Total Sales")!;
        me.Expression = DaxGenerators.InjectEvaluateAndLog(me.Expression, "p");
        Assert.Contains("EVALUATEANDLOG", me.Expression);
        me.Expression = DaxGenerators.StripEvaluateAndLog(me.Expression);
        Assert.Equal("SUM ( 'Sales'[Amount] )", me.Expression);
    }

    // ====================================================================== B1. time intelligence measures
    [Fact]
    public void TimeIntelligenceMeasures_builds_the_full_set()
    {
        var specs = DaxGenerators.TimeIntelligenceMeasures("Sales", "Total Sales", "Calendar", "Date", null);
        var names = specs.Select(s => s.Name).ToList();
        foreach (var n in new[] { "Total Sales YTD", "Total Sales QTD", "Total Sales MTD", "Total Sales PY",
            "Total Sales PYTD", "Total Sales YoY", "Total Sales YoY %", "Total Sales YOYTD", "Total Sales YOYTD %" })
            Assert.Contains(n, names);

        var ytd = specs.Single(s => s.Name == "Total Sales YTD");
        Assert.Contains("DATESYTD ( 'Calendar'[Date] )", ytd.Dax);
        var py = specs.Single(s => s.Name == "Total Sales PY");
        Assert.Contains("SAMEPERIODLASTYEAR ( 'Calendar'[Date] )", py.Dax);
        var yoyPct = specs.Single(s => s.Name == "Total Sales YoY %");
        Assert.Contains("DIVIDE (", yoyPct.Dax);
        Assert.Equal("0.0%;-0.0%;0.0%", yoyPct.Format);
    }

    [Fact]
    public void TimeIntelligenceMeasures_honours_fiscal_year_end()
    {
        var specs = DaxGenerators.TimeIntelligenceMeasures("Sales", "Total Sales", "Calendar", "Date", "06-30");
        var ytd = specs.Single(s => s.Name == "Total Sales YTD");
        Assert.Contains("DATESYTD ( 'Calendar'[Date], \"06-30\" )", ytd.Dax);
    }

    [Fact]
    public void TimeIntelligenceMeasures_apply_to_model_adds_all_measures()
    {
        var model = NewModel();
        var specs = DaxGenerators.TimeIntelligenceMeasures("Sales", "Total Sales", "Calendar", "Date", null);
        var added = ModelService.ApplyMeasureSpecsCore(model, specs);
        Assert.Equal(specs.Count, added.Count);
        Assert.True(model.Tables.Find("Sales")!.Measures.Contains("Total Sales YTD"));
    }

    // ====================================================================== B2. running total
    [Fact]
    public void RunningTotalOverDate_filters_dates_up_to_max()
    {
        var spec = DaxGenerators.RunningTotalOverDate("Sales", "Total Sales", "Calendar", "Date");
        Assert.Equal("Total Sales Running Total", spec.Name);
        Assert.Contains("FILTER ( ALLSELECTED ( 'Calendar'[Date] ), 'Calendar'[Date] <= MAX ( 'Calendar'[Date] ) )", spec.Dax);
    }

    [Fact]
    public void RunningTotalOverColumn_uses_the_generic_sort_column()
    {
        var spec = DaxGenerators.RunningTotalOverColumn("Sales", "Total Sales", "Product[Category]");
        Assert.Contains("ALLSELECTED ( 'Product'[Category] )", spec.Dax);
        Assert.Contains("'Product'[Category] <= MAX ( 'Product'[Category] )", spec.Dax);
    }

    // ====================================================================== B3. moving average
    [Fact]
    public void MovingAverage_uses_datesinperiod_over_days()
    {
        var spec = DaxGenerators.MovingAverage("Sales", "Total Sales", "Calendar", "Date", 7);
        Assert.Equal("Total Sales 7-Day Moving Avg", spec.Name);
        Assert.Contains("DATESINPERIOD ( 'Calendar'[Date], _lastDate, -7, DAY )", spec.Dax);
        Assert.Contains("AVERAGEX", spec.Dax);
    }

    [Fact]
    public void MovingAverage_rejects_non_positive_periods()
        => Assert.Throws<InvalidOperationException>(() =>
            DaxGenerators.MovingAverage("Sales", "Total Sales", "Calendar", "Date", 0));

    // ====================================================================== B4. percent of total / parent
    [Theory]
    [InlineData("ALL", "ALL ( 'Product'[Category] )")]
    [InlineData("ALLSELECTED", "ALLSELECTED ( 'Product'[Category] )")]
    public void PercentOfTotal_respects_scope(string scope, string expected)
    {
        var spec = DaxGenerators.PercentOfTotal("Sales", "Total Sales", "Product[Category]", scope);
        Assert.Contains("DIVIDE (", spec.Dax);
        Assert.Contains(expected, spec.Dax);
        Assert.Equal("0.0%", spec.Format);
    }

    [Fact]
    public void PercentOfTotal_allexcept_keeps_the_table()
    {
        var spec = DaxGenerators.PercentOfTotal("Sales", "Total Sales", "Product[Category]", "ALLEXCEPT");
        Assert.Contains("ALLEXCEPT ( 'Product', 'Product'[Category] )", spec.Dax);
    }

    [Fact]
    public void PercentOfParent_switches_on_inscope_levels()
    {
        var spec = DaxGenerators.PercentOfParent("Sales", "Total Sales",
            new[] { "Product[Category]", "Product[Subcategory]", "Product[ProductKey]" });
        Assert.Contains("SWITCH (", spec.Dax);
        Assert.Contains("ISINSCOPE ( 'Product'[ProductKey] )", spec.Dax);
        Assert.Contains("ISINSCOPE ( 'Product'[Subcategory] )", spec.Dax);
        Assert.Contains("ALLSELECTED ( 'Product'[Category] )", spec.Dax);   // top-level fallback
        Assert.Equal("0.0%", spec.Format);
    }

    // ====================================================================== B5. rank
    [Fact]
    public void RankMeasure_uses_rankx_isinscope_and_blank_guard()
    {
        var spec = DaxGenerators.RankMeasure("Sales", "Total Sales", "Product[Category]", "DESC", "SKIP", null);
        Assert.Contains("ISINSCOPE ( 'Product'[Category] )", spec.Dax);
        Assert.Contains("RANKX (", spec.Dax);
        Assert.Contains("ALLSELECTED ( 'Product'[Category] )", spec.Dax);
        Assert.Contains("DESC", spec.Dax);
        Assert.Contains("SKIP", spec.Dax);
        Assert.Contains("IF ( NOT ISBLANK ( [Total Sales] ), _rank )", spec.Dax);
    }

    [Fact]
    public void RankMeasure_supports_asc_dense_and_within_group()
    {
        var spec = DaxGenerators.RankMeasure("Sales", "Total Sales", "Product[ProductKey]", "ASC", "DENSE", "Product[Category]");
        Assert.Contains("ASC", spec.Dax);
        Assert.Contains("DENSE", spec.Dax);
        Assert.Contains("FILTER ( ALLSELECTED ( 'Product'[ProductKey] ), 'Product'[Category] = MAX ( 'Product'[Category] ) )", spec.Dax);
    }

    // ====================================================================== B6. semi-additive
    [Fact]
    public void SemiAdditiveMeasures_build_open_close_and_nonblank()
    {
        var specs = DaxGenerators.SemiAdditiveMeasures("Sales", "Sales[Amount]", "Calendar[Date]");
        var names = specs.Select(s => s.Name).ToList();
        Assert.Contains("Amount Closing Balance", names);
        Assert.Contains("Amount Opening Balance", names);
        Assert.Contains("Amount Last Non-Blank", names);
        Assert.Contains("Amount First Non-Blank", names);
        Assert.Contains("LASTNONBLANK ( 'Calendar'[Date]", specs.Single(s => s.Name == "Amount Closing Balance").Dax);
        Assert.Contains("FIRSTNONBLANK ( 'Calendar'[Date]", specs.Single(s => s.Name == "Amount Opening Balance").Dax);
    }

    // ====================================================================== B7. dynamic segmentation
    [Fact]
    public void DynamicSegmentation_classifies_with_left_closed_right_open_bounds()
    {
        var spec = DaxGenerators.DynamicSegmentation("Customer", "Total Sales", "Bands",
            "Product[ProductKey]", "Lower", "Upper", "Segment");
        Assert.Contains("'Bands'[Lower] <= _v && _v < 'Bands'[Upper]", spec.Dax);
        Assert.Contains("SELECTEDVALUE ( 'Bands'[Segment] )", spec.Dax);
    }

    // ====================================================================== B8. ABC
    [Fact]
    public void AbcClassificationDynamic_uses_cumulative_share_thresholds()
    {
        var spec = DaxGenerators.AbcClassificationDynamic("Product", "Product[ProductKey]", "Total Sales", 0.7, 0.9);
        Assert.Contains("_cumPct <= 0.7, \"A\"", spec.Dax);
        Assert.Contains("_cumPct <= 0.9, \"B\"", spec.Dax);
        Assert.Contains("\"C\"", spec.Dax);
        Assert.Contains("DIVIDE ( _cumAboveIncl, _grand )", spec.Dax);
    }

    [Fact]
    public void AbcClassTableDax_returns_a_class_per_entity()
    {
        var dax = DaxGenerators.AbcClassTableDax("Product", "Product[ProductKey]", "Total Sales", 0.7, 0.9);
        Assert.Contains("ADDCOLUMNS (", dax);
        Assert.Contains("VALUES ( 'Product'[ProductKey] )", dax);
        Assert.Contains("\"@Class\"", dax);
    }

    [Fact]
    public void AddAbcClassificationCore_path_builds_measure_and_class_table()
    {
        // exercise the same composition the public method does, but without SaveChanges.
        var model = NewModel();
        var spec = DaxGenerators.AbcClassificationDynamic("Product", "Product[ProductKey]", "Total Sales", 0.7, 0.9);
        ModelService.AddMeasureCore(model, spec.Table, spec.Name, spec.Dax, spec.Format, spec.Folder, null);
        Assert.True(model.Tables.Find("Product")!.Measures.Contains("Total Sales ABC Class"));
    }

    // ====================================================================== B9. dynamic Top N
    [Fact]
    public void DynamicTopNTableDax_unions_values_with_an_others_row()
    {
        var dax = DaxGenerators.DynamicTopNTableDax("Product[Category]", "Others");
        Assert.Contains("UNION (", dax);
        Assert.Contains("SELECTCOLUMNS ( VALUES ( 'Product'[Category] ), \"Member\", 'Product'[Category] )", dax);
        Assert.Contains("ROW ( \"Member\", \"Others\" )", dax);
    }

    [Fact]
    public void DynamicTopNMeasure_buckets_below_rank_into_others()
    {
        var spec = DaxGenerators.DynamicTopNMeasure("Sales", "Product[Category]", "Total Sales",
            "Total Sales Top N Value", "Others", "TopTable[Member]");
        Assert.Contains("RANKX ( ALLSELECTED ( 'Product'[Category] ), [Total Sales]", spec.Dax);
        Assert.Contains("[Total Sales Top N Value]", spec.Dax);
        Assert.Contains("_current = \"Others\"", spec.Dax);
    }

    // ====================================================================== B10. time-intelligence calc group
    [Fact]
    public void TimeIntelligenceCalcItems_cover_the_standard_items_with_ordinals()
    {
        var items = DaxGenerators.TimeIntelligenceCalcItems("Calendar", "Date");
        var names = items.Select(i => i.Name).ToList();
        foreach (var n in new[] { "Current", "MTD", "QTD", "YTD", "PY", "PYTD", "YoY", "YoY %" })
            Assert.Contains(n, names);
        Assert.Equal(0, items.Single(i => i.Name == "Current").Ordinal);
        Assert.Contains("SELECTEDMEASURE ()", items.Single(i => i.Name == "Current").Dax);
        Assert.Contains("DATESYTD ( 'Calendar'[Date] )", items.Single(i => i.Name == "YTD").Dax);
        Assert.NotNull(items.Single(i => i.Name == "YoY %").FormatStringExpression);
    }

    [Fact]
    public void AddTimeIntelligenceCalcGroupCore_builds_group_items_and_sets_discourage_implicit()
    {
        var model = NewModel();
        ModelService.AddTimeIntelligenceCalcGroupCore(model, "Time Intelligence", "Calendar", "Date", precedence: 10);

        var cg = model.Tables.Find("Time Intelligence")!.CalculationGroup;
        Assert.NotNull(cg);
        Assert.Equal(10, cg!.Precedence);
        Assert.True(cg.CalculationItems.Contains("YTD"));
        Assert.True(cg.CalculationItems.Contains("YoY %"));
        Assert.NotNull(cg.CalculationItems.Find("YoY %")!.FormatStringDefinition);
        Assert.True(model.DiscourageImplicitMeasures);   // required for calc groups to function
    }

    // ====================================================================== B11. currency conversion calc group
    [Fact]
    public void CurrencyConversionCalcItem_applies_the_rate_over_date()
    {
        var item = DaxGenerators.CurrencyConversionCalcItem("Rates", "Rate", "Currency");
        Assert.Contains("SUMX (", item.Dax);
        Assert.Contains("SELECTEDVALUE ( 'Rates'[Rate] )", item.Dax);
        Assert.Contains("SELECTEDMEASURE ()", item.Dax);
    }

    // ====================================================================== B12. dynamic RLS
    [Theory]
    [InlineData("direct", "LOOKUPVALUE")]
    [InlineData("bridge", "CONTAINS")]
    [InlineData("hierarchy", "PATHCONTAINS")]
    public void DynamicRlsFilter_builds_the_right_shape(string shape, string token)
    {
        var filter = DaxGenerators.DynamicRlsFilter(shape, "Sales", "Region", "UserSecurity", "Email", "Region", "Path");
        Assert.Contains("USERPRINCIPALNAME ()", filter);
        Assert.Contains(token, filter);
    }

    [Fact]
    public void AddDynamicRlsCore_creates_role_and_filters_the_secured_table()
    {
        var model = NewModel();
        ModelService.AddDynamicRlsCore(model, "Region Reader", "direct", "Sales", "Region",
            "UserSecurity", "Email", "Region", null, "Read");

        Assert.True(model.Roles.Contains("Region Reader"));
        var tp = model.Roles.Find("Region Reader")!.TablePermissions.Find("Sales");
        Assert.NotNull(tp);
        Assert.Contains("USERPRINCIPALNAME ()", tp!.FilterExpression);
    }

    [Fact]
    public void AddDynamicRlsCore_refuses_to_secure_the_user_table_itself()
    {
        var model = NewModel();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddDynamicRlsCore(model, "Bad", "direct", "UserSecurity", "Region",
                "UserSecurity", "Email", "Region", null, "Read"));
        Assert.Contains("user/mapping table", ex.Message);
    }

    // ====================================================================== B13. custom-calendar TI
    [Fact]
    public void CustomCalendarTimeIntelligence_uses_integer_index_math_and_364_shift()
    {
        var specs = DaxGenerators.CustomCalendarTimeIntelligence("Sales", "Calendar445", "445", "Total Sales");
        var names = specs.Select(s => s.Name).ToList();
        Assert.Contains("Total Sales YTD (Custom)", names);
        Assert.Contains("Total Sales PY (Custom)", names);
        Assert.Contains("Total Sales PYTD (Custom)", names);
        Assert.Contains("Total Sales PW (Custom)", names);
        Assert.Contains("Total Sales MAT (Custom)", names);
        Assert.Contains("'Calendar445'[DayIndex]", specs.Single(s => s.Name == "Total Sales PY (Custom)").Dax);
        Assert.Contains("- 364", specs.Single(s => s.Name == "Total Sales PY (Custom)").Dax);
        Assert.Contains("- 7", specs.Single(s => s.Name == "Total Sales PW (Custom)").Dax);
    }

    // ====================================================================== PART C. field-param verify
    [Fact]
    public void AddFieldParameterCore_emits_metadata_sortby_and_groupby_bindings()
    {
        var model = NewModel();
        var table = ModelService.AddFieldParameterCore(model, "Field Choice",
            new[] { "Sales[Amount]", "Sales[Qty]" });

        var display = table.Columns.Find("Field Choice")!;
        var fields = table.Columns.Find("Field Choice Fields")!;
        var order = table.Columns.Find("Field Choice Order")!;

        // 1. ParameterMetadata {version:3,kind:2} on every projected column (incl. the hidden Fields column)
        foreach (var c in new[] { display, fields, order })
        {
            var ep = c.ExtendedProperties.Find("ParameterMetadata");
            Assert.NotNull(ep);
            Assert.Equal("{\"version\":3,\"kind\":2}", ((TOM.JsonExtendedProperty)ep!).Value);
        }
        Assert.True(fields.IsHidden);
        Assert.True(order.IsHidden);

        // 2. display column SortByColumn = Order
        Assert.Same(order, display.SortByColumn);

        // 3. display column GroupByColumns contains the hidden Fields column (else field param is non-functional)
        Assert.NotNull(display.RelatedColumnDetails);
        Assert.Single(display.RelatedColumnDetails.GroupByColumns);
        Assert.Same(fields, display.RelatedColumnDetails.GroupByColumns.Single().GroupingColumn);
    }

    [Fact]
    public void AddWhatIfParameterCore_emits_version0_metadata_and_value_measure()
    {
        var model = NewModel();
        var table = ModelService.AddWhatIfParameterCore(model, "Growth", 0, 1, 0.1, 0.5);
        var col = table.Columns.Find("Growth")!;
        var ep = col.ExtendedProperties.Find("ParameterMetadata");
        Assert.NotNull(ep);
        Assert.Equal("{\"version\":0}", ((TOM.JsonExtendedProperty)ep!).Value);
        Assert.True(table.Measures.Contains("Growth Value"));
        Assert.Contains("SELECTEDVALUE", table.Measures.Find("Growth Value")!.Expression);
    }
}
