using System.Linq;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Unit tests for the remaining Tabular-model parity tools (parameters, perspectives, cultures,
/// aggregations, incremental refresh, variations, data category, display folders, date table,
/// query groups). Each builds an in-memory <c>new TOM.Model()</c>, calls the *Core mutation helper,
/// and asserts on the resulting object tree. SaveChanges() (which needs a live server) is not run.
/// </summary>
public sealed class ModelParityToolsTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model" };

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Qty", DataType = TOM.DataType.Int64, SourceColumn = "Qty" });
        sales.Columns.Add(new TOM.DataColumn { Name = "OrderDate", DataType = TOM.DataType.DateTime, SourceColumn = "OrderDate" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = #table({},{}) in Source" } });
        model.Tables.Add(sales);

        var calendar = new TOM.Table { Name = "Calendar" };
        calendar.Columns.Add(new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime, SourceColumn = "Date" });
        calendar.Columns.Add(new TOM.DataColumn { Name = "Year", DataType = TOM.DataType.Int64, SourceColumn = "Year" });
        calendar.Columns.Add(new TOM.DataColumn { Name = "Month", DataType = TOM.DataType.String, SourceColumn = "Month" });
        var hy = new TOM.Hierarchy { Name = "Calendar Hierarchy" };
        hy.Levels.Add(new TOM.Level { Name = "Year", Ordinal = 0, Column = calendar.Columns.Find("Year") });
        hy.Levels.Add(new TOM.Level { Name = "Month", Ordinal = 1, Column = calendar.Columns.Find("Month") });
        calendar.Hierarchies.Add(hy);
        model.Tables.Add(calendar);

        return model;
    }

    // ------------------------------------------------------------------ field parameter
    [Fact]
    public void AddFieldParameter_builds_calc_table_with_nameof_rows_and_metadata()
    {
        var model = NewModel();
        var t = ModelService.AddFieldParameterCore(model, "Selector", new[] { "Sales[Amount]", "Sales[Qty]" });

        Assert.True(model.Tables.Contains("Selector"));
        var src = (TOM.CalculatedPartitionSource)t.Partitions[0].Source;
        Assert.Contains("NAMEOF('Sales'[Amount])", src.Expression);
        Assert.Contains("NAMEOF('Sales'[Qty])", src.Expression);
        Assert.Contains("(\"Amount\", NAMEOF('Sales'[Amount]), 0)", src.Expression);
        // three projected columns, all carrying the ParameterMetadata extended property
        Assert.Equal(3, t.Columns.Count(c => c.Type != TOM.ColumnType.RowNumber));
        foreach (var c in t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber))
            Assert.NotNull(c.ExtendedProperties.Find("ParameterMetadata"));
    }

    [Fact]
    public void AddFieldParameter_rejects_bad_field_ref()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.AddFieldParameterCore(model, "Bad", new[] { "JustAColumn" }));
    }

    // ------------------------------------------------------------------ what-if parameter
    [Fact]
    public void AddWhatIfParameter_builds_generateseries_table_and_selectedvalue_measure()
    {
        var model = NewModel();
        var t = ModelService.AddWhatIfParameterCore(model, "Growth %", 0, 1, 0.1, 0.2);

        var src = (TOM.CalculatedPartitionSource)t.Partitions[0].Source;
        Assert.Equal("GENERATESERIES(0, 1, 0.1)", src.Expression);
        var me = t.Measures.Find("Growth % Value");
        Assert.NotNull(me);
        Assert.Equal("SELECTEDVALUE('Growth %'[Growth %], 0.2)", me!.Expression);
        Assert.NotNull(t.Columns.Find("Growth %")!.ExtendedProperties.Find("ParameterMetadata"));
    }

    [Fact]
    public void AddWhatIfParameter_rejects_zero_increment()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.AddWhatIfParameterCore(model, "P", 0, 10, 0, null));
    }

    // ------------------------------------------------------------------ perspectives
    [Fact]
    public void AddPerspective_and_add_table_then_members()
    {
        var model = NewModel();
        ModelService.AddPerspectiveCore(model, "Finance");
        ModelService.AddToPerspectiveCore(model, "Finance", "Sales", null);             // whole table
        ModelService.AddToPerspectiveCore(model, "Finance", "Sales", "Total Sales");    // measure
        ModelService.AddToPerspectiveCore(model, "Finance", "Sales", "Region");         // column
        ModelService.AddToPerspectiveCore(model, "Finance", "Calendar", "Calendar Hierarchy"); // hierarchy

        var p = model.Perspectives.Find("Finance")!;
        var pt = p.PerspectiveTables.Find("Sales")!;
        Assert.True(pt.PerspectiveMeasures.Contains("Total Sales"));
        Assert.True(pt.PerspectiveColumns.Contains("Region"));
        Assert.True(p.PerspectiveTables.Find("Calendar")!.PerspectiveHierarchies.Contains("Calendar Hierarchy"));
    }

    [Fact]
    public void AddToPerspective_throws_for_unknown_child()
    {
        var model = NewModel();
        ModelService.AddPerspectiveCore(model, "P");
        Assert.Throws<InvalidOperationException>(() => ModelService.AddToPerspectiveCore(model, "P", "Sales", "Nope"));
    }

    // ------------------------------------------------------------------ cultures / translations
    [Fact]
    public void SetTranslation_sets_caption_on_column()
    {
        var model = NewModel();
        ModelService.AddCultureCore(model, "fr-FR");
        var tr = ModelService.SetTranslationCore(model, "fr-FR", "column", "Region", "Caption", "Région", "Sales");

        Assert.Equal(TOM.TranslatedProperty.Caption, tr.Property);
        Assert.Equal("Région", tr.Value);
        var c = model.Cultures.Find("fr-FR")!;
        Assert.Same(model.Tables.Find("Sales")!.Columns.Find("Region"), tr.Object);
    }

    [Fact]
    public void SetTranslation_updates_existing_in_place()
    {
        var model = NewModel();
        ModelService.AddCultureCore(model, "fr-FR");
        ModelService.SetTranslationCore(model, "fr-FR", "measure", "Total Sales", "Caption", "Ventes", "Sales");
        ModelService.SetTranslationCore(model, "fr-FR", "measure", "Total Sales", "Caption", "Ventes totales", "Sales");

        var me = model.Tables.Find("Sales")!.Measures.Find("Total Sales")!;
        var c = model.Cultures.Find("fr-FR")!;
        var tr = c.ObjectTranslations.First(o => ReferenceEquals(o.Object, me) && o.Property == TOM.TranslatedProperty.Caption);
        Assert.Equal("Ventes totales", tr.Value);
    }

    [Fact]
    public void SetTranslation_throws_for_unknown_culture()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetTranslationCore(model, "fr-FR", "table", "Sales", "Caption", "Ventes", null));
    }

    // ------------------------------------------------------------------ aggregations
    [Fact]
    public void SetAggregation_groupby_sets_alternateof_base_column()
    {
        var model = NewModel();
        var alt = ModelService.SetAggregationCore(model, "Sales", "Region", "Calendar[Year]", "GroupBy");

        Assert.Equal(TOM.SummarizationType.GroupBy, alt.Summarization);
        Assert.Same(model.Tables.Find("Calendar")!.Columns.Find("Year"), alt.BaseColumn);
        Assert.Same(alt, model.Tables.Find("Sales")!.Columns.Find("Region")!.AlternateOf);
    }

    [Fact]
    public void SetAggregation_sum_against_base_table()
    {
        var model = NewModel();
        var alt = ModelService.SetAggregationCore(model, "Sales", "Amount", "Calendar", "Sum");

        Assert.Equal(TOM.SummarizationType.Sum, alt.Summarization);
        Assert.Same(model.Tables.Find("Calendar"), alt.BaseTable);
    }

    [Fact]
    public void SetAggregation_rejects_bad_summarization()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetAggregationCore(model, "Sales", "Amount", "Calendar", "Average"));
    }

    // ------------------------------------------------------------------ incremental refresh
    [Fact]
    public void SetIncrementalRefresh_sets_policy_and_creates_range_params()
    {
        var model = NewModel();
        bool created = ModelService.SetIncrementalRefreshCore(model, "Sales", "Year", 5, "Month", 2, null);

        Assert.True(created);
        Assert.NotNull(model.Expressions.Find("RangeStart"));
        Assert.NotNull(model.Expressions.Find("RangeEnd"));
        var policy = (TOM.BasicRefreshPolicy)model.Tables.Find("Sales")!.RefreshPolicy!;
        Assert.Equal(TOM.RefreshGranularityType.Year, policy.RollingWindowGranularity);
        Assert.Equal(5, policy.RollingWindowPeriods);
        Assert.Equal(TOM.RefreshGranularityType.Month, policy.IncrementalGranularity);
        Assert.Equal(2, policy.IncrementalPeriods);
    }

    [Fact]
    public void SetIncrementalRefresh_reuses_existing_range_params()
    {
        var model = NewModel();
        ModelService.SetIncrementalRefreshCore(model, "Sales", "Year", 3, "Day", 10, null);
        bool created = ModelService.SetIncrementalRefreshCore(model, "Calendar", "Year", 2, "Day", 5, null);

        Assert.False(created);   // RangeStart/RangeEnd already exist from the first call
    }

    // ------------------------------------------------------------------ variations
    [Fact]
    public void AddVariation_adds_variation_via_relationship_to_default_hierarchy()
    {
        var model = NewModel();
        var rel = new TOM.SingleColumnRelationship
        {
            Name = "Sales_Calendar",
            FromColumn = model.Tables.Find("Sales")!.Columns.Find("OrderDate"),
            ToColumn = model.Tables.Find("Calendar")!.Columns.Find("Date"),
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
        };
        model.Relationships.Add(rel);

        var v = ModelService.AddVariationCore(model, "Sales", "OrderDate", "Sales_Calendar", "Calendar Hierarchy", true);

        Assert.Same(rel, v.Relationship);
        Assert.Same(model.Tables.Find("Calendar")!.Hierarchies.Find("Calendar Hierarchy"), v.DefaultHierarchy);
        Assert.True(v.IsDefault);
        Assert.Single(model.Tables.Find("Sales")!.Columns.Find("OrderDate")!.Variations);
    }

    [Fact]
    public void AddVariation_throws_for_unknown_relationship()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddVariationCore(model, "Sales", "OrderDate", "NoSuchRel", "Calendar Hierarchy", true));
    }

    // ------------------------------------------------------------------ column data category
    [Fact]
    public void SetColumnDataCategory_sets_category()
    {
        var model = NewModel();
        var c = ModelService.SetColumnDataCategoryCore(model, "Sales", "Region", "StateOrProvince");
        Assert.Equal("StateOrProvince", c.DataCategory);
    }

    // ------------------------------------------------------------------ display folders
    [Fact]
    public void SetDisplayFolder_on_measure_and_column()
    {
        var model = NewModel();
        ModelService.SetDisplayFolderCore(model, "Total Sales", "Key Measures", "Sales");
        ModelService.SetDisplayFolderCore(model, "Region", "Attributes", "Sales");

        Assert.Equal("Key Measures", model.Tables.Find("Sales")!.Measures.Find("Total Sales")!.DisplayFolder);
        Assert.Equal("Attributes", model.Tables.Find("Sales")!.Columns.Find("Region")!.DisplayFolder);
    }

    [Fact]
    public void SetDisplayFolder_throws_for_unknown_target()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetDisplayFolderCore(model, "Nope", "Folder", "Sales"));
    }

    // ------------------------------------------------------------------ mark as date table
    [Fact]
    public void MarkAsDateTable_sets_time_category_and_key()
    {
        var model = NewModel();
        var c = ModelService.MarkAsDateTableCore(model, "Calendar", "Date");

        Assert.Equal("Time", model.Tables.Find("Calendar")!.DataCategory);
        Assert.True(c.IsKey);
    }

    [Fact]
    public void MarkAsDateTable_rejects_non_datetime_key()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.MarkAsDateTableCore(model, "Calendar", "Year"));
    }

    // ------------------------------------------------------------------ query groups
    [Fact]
    public void AddQueryGroup_is_idempotent_by_folder()
    {
        var model = NewModel();
        var a = ModelService.AddQueryGroupCore(model, "Staging");
        var b = ModelService.AddQueryGroupCore(model, "Staging");
        Assert.Same(a, b);
        Assert.Single(model.QueryGroups);
    }

    [Fact]
    public void SetObjectQueryGroup_assigns_partition_to_group()
    {
        var model = NewModel();
        ModelService.SetObjectQueryGroupCore(model, "partition", "Sales", "Staging");
        var qg = model.QueryGroups.First(q => q.Folder == "Staging");
        Assert.Same(qg, model.Tables.Find("Sales")!.Partitions[0].QueryGroup);
    }

    [Fact]
    public void SetObjectQueryGroup_assigns_expression_to_group()
    {
        var model = NewModel();
        model.Expressions.Add(new TOM.NamedExpression { Name = "P", Kind = TOM.ExpressionKind.M, Expression = "1" });
        ModelService.SetObjectQueryGroupCore(model, "expression", "P", "Parameters");
        var qg = model.QueryGroups.First(q => q.Folder == "Parameters");
        Assert.Same(qg, model.Expressions.Find("P")!.QueryGroup);
    }

    [Fact]
    public void SetObjectQueryGroup_rejects_bad_object_type()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetObjectQueryGroupCore(model, "bogus", "X", "F"));
    }
}
