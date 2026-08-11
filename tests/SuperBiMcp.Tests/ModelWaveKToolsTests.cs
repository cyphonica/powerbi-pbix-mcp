using System;
using System.Linq;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Unit tests for the audited TOM coverage-gap tools: relationship update/delete + cardinality,
/// column summarize-by / data type / flags, measure hide+rename+description, rename table, KPI
/// trend, hierarchy/level properties + delete, model settings + auto date-time off, role/perspective
/// removal, variation list/delete and the first-class data source. Each builds an in-memory
/// <c>new TOM.Model()</c>, calls the *Core mutation helper, and asserts on the resulting object tree -
/// no live Analysis Services server is needed. SaveChanges() is deliberately not exercised.
/// </summary>
public sealed class ModelWaveKToolsTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model", Culture = "en-US" };

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey" });
        sales.Columns.Add(new TOM.DataColumn { Name = "OrderDate", DataType = TOM.DataType.DateTime, SourceColumn = "OrderDate" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = #table({},{}) in Source" } });
        model.Tables.Add(sales);

        var customer = new TOM.Table { Name = "Customer" };
        customer.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey" });
        customer.Columns.Add(new TOM.DataColumn { Name = "Country", DataType = TOM.DataType.String, SourceColumn = "Country" });
        customer.Columns.Add(new TOM.DataColumn { Name = "City", DataType = TOM.DataType.String, SourceColumn = "City" });
        var hy = new TOM.Hierarchy { Name = "Geography" };
        hy.Levels.Add(new TOM.Level { Name = "Country", Ordinal = 0, Column = customer.Columns.Find("Country") });
        hy.Levels.Add(new TOM.Level { Name = "City", Ordinal = 1, Column = customer.Columns.Find("City") });
        customer.Hierarchies.Add(hy);
        model.Tables.Add(customer);

        return model;
    }

    private static TOM.SingleColumnRelationship AddSalesCustomerRel(TOM.Model model)
    {
        var rel = new TOM.SingleColumnRelationship
        {
            Name = "Sales_Customer",
            FromColumn = model.Tables.Find("Sales")!.Columns.Find("CustomerKey"),
            ToColumn = model.Tables.Find("Customer")!.Columns.Find("CustomerKey"),
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
        };
        model.Relationships.Add(rel);
        return rel;
    }

    // ================================================================ relationships
    [Fact]
    public void AddRelationshipCore_supports_many_to_many_and_both_directions()
    {
        var model = NewModel();
        var rel = ModelService.AddRelationshipCore(model, "Sales", "Region", "Customer", "Country",
            bothDirections: false, active: true,
            fromCardinality: "Many", toCardinality: "Many",
            crossFilteringBehavior: "BothDirections", securityFilteringBehavior: null, joinOnDateBehavior: null);

        Assert.Equal(TOM.RelationshipEndCardinality.Many, rel.FromCardinality);
        Assert.Equal(TOM.RelationshipEndCardinality.Many, rel.ToCardinality);
        Assert.Equal(TOM.CrossFilteringBehavior.BothDirections, rel.CrossFilteringBehavior);
        Assert.Contains(rel, model.Relationships.OfType<TOM.SingleColumnRelationship>());
    }

    [Fact]
    public void AddRelationshipCore_defaults_to_many_to_one_star_schema()
    {
        var model = NewModel();
        var rel = ModelService.AddRelationshipCore(model, "Sales", "CustomerKey", "Customer", "CustomerKey",
            bothDirections: false, active: true, null, null, null, null, null);

        Assert.Equal(TOM.RelationshipEndCardinality.Many, rel.FromCardinality);
        Assert.Equal(TOM.RelationshipEndCardinality.One, rel.ToCardinality);
        Assert.Equal(TOM.CrossFilteringBehavior.OneDirection, rel.CrossFilteringBehavior);
    }

    [Fact]
    public void UpdateRelationshipCore_round_trips_cardinality_security_and_join_on_date()
    {
        var model = NewModel();
        AddSalesCustomerRel(model);

        var rel = ModelService.UpdateRelationshipCore(model, "Sales_Customer", null, null, null, null,
            fromCardinality: "One", toCardinality: "One",
            crossFilteringBehavior: "Automatic", securityFilteringBehavior: "BothDirections",
            isActive: false, joinOnDateBehavior: "DatePartOnly");

        Assert.Equal(TOM.RelationshipEndCardinality.One, rel.FromCardinality);
        Assert.Equal(TOM.RelationshipEndCardinality.One, rel.ToCardinality);
        Assert.Equal(TOM.CrossFilteringBehavior.Automatic, rel.CrossFilteringBehavior);
        Assert.Equal(TOM.SecurityFilteringBehavior.BothDirections, rel.SecurityFilteringBehavior);
        Assert.False(rel.IsActive);
        Assert.Equal(TOM.DateTimeRelationshipBehavior.DatePartOnly, rel.JoinOnDateBehavior);
    }

    [Fact]
    public void UpdateRelationshipCore_by_column_pair_leaves_unspecified_unchanged()
    {
        var model = NewModel();
        var orig = AddSalesCustomerRel(model);
        orig.CrossFilteringBehavior = TOM.CrossFilteringBehavior.BothDirections;

        var rel = ModelService.UpdateRelationshipCore(model, null, "Sales", "CustomerKey", "Customer", "CustomerKey",
            null, null, null, null, isActive: false, null);

        Assert.Same(orig, rel);
        Assert.False(rel.IsActive);                                                       // changed
        Assert.Equal(TOM.CrossFilteringBehavior.BothDirections, rel.CrossFilteringBehavior);   // unchanged
    }

    [Fact]
    public void UpdateRelationshipCore_rejects_bad_cardinality()
    {
        var model = NewModel();
        AddSalesCustomerRel(model);
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.UpdateRelationshipCore(model, "Sales_Customer", null, null, null, null,
                "Several", null, null, null, null, null));
    }

    [Fact]
    public void DeleteRelationshipCore_removes_it()
    {
        var model = NewModel();
        AddSalesCustomerRel(model);
        ModelService.DeleteRelationshipCore(model, "Sales_Customer", null, null, null, null);
        Assert.Null(model.Relationships.Find("Sales_Customer"));
    }

    [Fact]
    public void ResolveRelationship_finds_by_name()
    {
        var model = NewModel();
        var rel = AddSalesCustomerRel(model);
        var found = ModelService.ResolveRelationship(model, "Sales_Customer", null, null, null, null);
        Assert.Same(rel, found);
    }

    [Fact]
    public void ResolveRelationship_finds_by_column_pair_either_order()
    {
        var model = NewModel();
        var rel = AddSalesCustomerRel(model);

        var forward = ModelService.ResolveRelationship(model, null, "Sales", "CustomerKey", "Customer", "CustomerKey");
        var reverse = ModelService.ResolveRelationship(model, null, "Customer", "CustomerKey", "Sales", "CustomerKey");
        Assert.Same(rel, forward);
        Assert.Same(rel, reverse);
    }

    [Fact]
    public void ResolveRelationship_throws_for_unknown_name()
    {
        var model = NewModel();
        AddSalesCustomerRel(model);
        Assert.Throws<InvalidOperationException>(() => ModelService.ResolveRelationship(model, "Nope", null, null, null, null));
    }

    [Fact]
    public void ResolveRelationship_requires_name_or_full_column_pair()
    {
        var model = NewModel();
        AddSalesCustomerRel(model);
        Assert.Throws<InvalidOperationException>(() => ModelService.ResolveRelationship(model, null, "Sales", "CustomerKey", null, null));
    }

    // ================================================================ summarize-by / data type / flags
    [Fact]
    public void SetSummarizeBy_sets_aggregate_function()
    {
        var model = NewModel();
        var c = ModelService.SetSummarizeByCore(model, "Sales", "CustomerKey", "None");
        Assert.Equal(TOM.AggregateFunction.None, c.SummarizeBy);

        var d = ModelService.SetSummarizeByCore(model, "Sales", "Amount", "Average");
        Assert.Equal(TOM.AggregateFunction.Average, d.SummarizeBy);
    }

    [Fact]
    public void SetSummarizeBy_rejects_invalid_value()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetSummarizeByCore(model, "Sales", "Amount", "Median"));
    }

    [Fact]
    public void SetColumnDataType_changes_type()
    {
        var model = NewModel();
        var c = ModelService.SetColumnDataTypeCore(model, "Sales", "Amount", "Decimal");
        Assert.Equal(TOM.DataType.Decimal, c.DataType);
    }

    [Fact]
    public void SetColumnDataType_rejects_invalid_type()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetColumnDataTypeCore(model, "Sales", "Amount", "Money"));
    }

    [Fact]
    public void SetColumnFlags_sets_each_flag_and_leaves_unspecified_unchanged()
    {
        var model = NewModel();
        var c = ModelService.SetColumnFlagsCore(model, "Customer", "CustomerKey",
            isKey: true, isNullable: false, isUnique: true, alignment: "Right", encodingHint: "Value");

        Assert.True(c.IsKey);
        Assert.False(c.IsNullable);
        Assert.True(c.IsUnique);
        Assert.Equal(TOM.Alignment.Right, c.Alignment);
        Assert.Equal(TOM.EncodingHintType.Value, c.EncodingHint);

        // a second call touching only one flag leaves the others as-is
        ModelService.SetColumnFlagsCore(model, "Customer", "CustomerKey", isKey: null, isNullable: null, isUnique: null, alignment: "Left", encodingHint: null);
        Assert.True(c.IsKey);                              // unchanged
        Assert.Equal(TOM.Alignment.Left, c.Alignment);    // changed
        Assert.Equal(TOM.EncodingHintType.Value, c.EncodingHint);   // unchanged
    }

    [Fact]
    public void SetColumnFlags_rejects_bad_alignment()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetColumnFlagsCore(model, "Sales", "Amount", null, null, null, "Justified", null));
    }

    [Fact]
    public void SetColumnFlags_rejects_bad_encoding_hint()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetColumnFlagsCore(model, "Sales", "Amount", null, null, null, null, "RLE"));
    }

    // ================================================================ measure properties
    [Fact]
    public void SetMeasureProperties_hides_describes_and_renames()
    {
        var model = NewModel();
        var me = ModelService.SetMeasurePropertiesCore(model, "Sales", "Total Sales",
            hidden: true, description: "Net sales", newName: "Sales Amount");

        Assert.True(me.IsHidden);
        Assert.Equal("Net sales", me.Description);
        Assert.Equal("Sales Amount", me.Name);
        Assert.True(model.Tables.Find("Sales")!.Measures.Contains("Sales Amount"));
        Assert.False(model.Tables.Find("Sales")!.Measures.Contains("Total Sales"));
    }

    [Fact]
    public void SetMeasureProperties_rename_collision_throws()
    {
        var model = NewModel();
        model.Tables.Find("Sales")!.Measures.Add(new TOM.Measure { Name = "Other", Expression = "1" });
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetMeasurePropertiesCore(model, "Sales", "Total Sales", null, null, "Other"));
    }

    [Fact]
    public void SetMeasureProperties_throws_for_unknown_measure()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetMeasurePropertiesCore(model, "Sales", "Nope", true, null, null));
    }

    // ================================================================ rename table
    [Fact]
    public void RenameTable_renames()
    {
        var model = NewModel();
        ModelService.RenameTableCore(model, "Sales", "Fact Sales");
        Assert.True(model.Tables.Contains("Fact Sales"));
        Assert.False(model.Tables.Contains("Sales"));
    }

    [Fact]
    public void RenameTable_collision_throws()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.RenameTableCore(model, "Sales", "Customer"));
    }

    // ================================================================ KPI trend
    [Fact]
    public void UpdateKpi_extends_existing_kpi_with_trend_and_descriptions()
    {
        var model = NewModel();
        ModelService.SetKpiCore(model, "Sales", "Total Sales", "[Total Sales] * 1.1", "1", null);
        var kpi = ModelService.UpdateKpiCore(model, "Sales", "Total Sales",
            trendExpression: "[Total Sales]", targetFormatString: "#,0", statusDescription: "stat",
            trendDescription: "trend", targetDescription: "target");

        Assert.Equal("[Total Sales]", kpi.TrendExpression);
        Assert.Equal("#,0", kpi.TargetFormatString);
        Assert.Equal("stat", kpi.StatusDescription);
        Assert.Equal("trend", kpi.TrendDescription);
        Assert.Equal("target", kpi.TargetDescription);
        // the original set_kpi target survives
        Assert.Equal("[Total Sales] * 1.1", kpi.TargetExpression);
    }

    [Fact]
    public void UpdateKpi_creates_kpi_when_none_exists()
    {
        var model = NewModel();
        var kpi = ModelService.UpdateKpiCore(model, "Sales", "Total Sales", "[Total Sales]", null, null, null, null);
        Assert.Same(kpi, model.Tables.Find("Sales")!.Measures.Find("Total Sales")!.KPI);
        Assert.Equal("[Total Sales]", kpi.TrendExpression);
    }

    [Fact]
    public void UpdateKpi_throws_for_unknown_measure()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.UpdateKpiCore(model, "Sales", "Nope", "1", null, null, null, null));
    }

    // ================================================================ hierarchy / level properties + delete
    [Fact]
    public void SetHierarchyProperties_sets_folder_hidden_and_hide_members()
    {
        var model = NewModel();
        var h = ModelService.SetHierarchyPropertiesCore(model, "Customer", "Geography",
            displayFolder: "Geo", hidden: true, hideMembers: "HideBlankMembers");

        Assert.Equal("Geo", h.DisplayFolder);
        Assert.True(h.IsHidden);
        Assert.Equal(TOM.HierarchyHideMembersType.HideBlankMembers, h.HideMembers);
    }

    [Fact]
    public void SetHierarchyProperties_rejects_bad_hide_members()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetHierarchyPropertiesCore(model, "Customer", "Geography", null, null, "HideAll"));
    }

    [Fact]
    public void SetLevelProperties_sets_ordinal_description_and_renames()
    {
        var model = NewModel();
        var lvl = ModelService.SetLevelPropertiesCore(model, "Customer", "Geography", "City",
            ordinal: 5, description: "the city", newName: "Town");

        Assert.Equal(5, lvl.Ordinal);
        Assert.Equal("the city", lvl.Description);
        Assert.Equal("Town", lvl.Name);
        Assert.True(model.Tables.Find("Customer")!.Hierarchies.Find("Geography")!.Levels.Contains("Town"));
    }

    [Fact]
    public void SetLevelProperties_throws_for_unknown_level()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetLevelPropertiesCore(model, "Customer", "Geography", "Nope", 1, null, null));
    }

    [Fact]
    public void DeleteHierarchy_removes_and_reports_existence()
    {
        var model = NewModel();
        Assert.True(ModelService.DeleteHierarchyCore(model, "Customer", "Geography"));
        Assert.False(model.Tables.Find("Customer")!.Hierarchies.Contains("Geography"));
        Assert.False(ModelService.DeleteHierarchyCore(model, "Customer", "Geography"));   // already gone
    }

    // ================================================================ model settings / auto date-time
    [Fact]
    public void SetModelSettings_sets_flags()
    {
        var model = NewModel();
        ModelService.SetModelSettingsCore(model, discourageImplicitMeasures: true, defaultMode: "DirectLake",
            directLakeBehavior: "DirectLakeOnly", culture: "mi-NZ");

        Assert.True(model.DiscourageImplicitMeasures);
        Assert.Equal(TOM.ModeType.DirectLake, model.DefaultMode);
        Assert.Equal(TOM.DirectLakeBehavior.DirectLakeOnly, model.DirectLakeBehavior);
        Assert.Equal("mi-NZ", model.Culture);
    }

    [Fact]
    public void SetModelSettings_rejects_bad_default_mode()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetModelSettingsCore(model, null, "Streaming", null, null));
    }

    [Fact]
    public void DisableAutoDateTime_sets_annotation_and_drops_auto_tables()
    {
        var model = NewModel();
        // simulate an auto date/time table + its relationship to a date column
        var auto = new TOM.Table { Name = "LocalDateTable_abc123" };
        auto.Columns.Add(new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime, SourceColumn = "Date" });
        model.Tables.Add(auto);
        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = "auto_rel",
            FromColumn = model.Tables.Find("Sales")!.Columns.Find("OrderDate"),
            ToColumn = auto.Columns.Find("Date"),
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
        });

        int dropped = ModelService.DisableAutoDateTimeCore(model);

        Assert.Equal(1, dropped);
        Assert.Equal("0", model.Annotations.Find("__PBI_TimeIntelligenceEnabled")!.Value);
        Assert.False(model.Tables.Contains("LocalDateTable_abc123"));
        Assert.Null(model.Relationships.Find("auto_rel"));
    }

    [Fact]
    public void DisableAutoDateTime_is_idempotent_on_the_annotation()
    {
        var model = NewModel();
        ModelService.DisableAutoDateTimeCore(model);
        ModelService.DisableAutoDateTimeCore(model);
        Assert.Single(model.Annotations, a => a.Name == "__PBI_TimeIntelligenceEnabled");
        Assert.Equal("0", model.Annotations.Find("__PBI_TimeIntelligenceEnabled")!.Value);
    }

    // ================================================================ role member / permission removal
    [Fact]
    public void RemoveRoleMember_removes_member()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        ModelService.AddRoleMemberCore(model, "Reader", "user@org.com", null);

        Assert.True(ModelService.RemoveRoleMemberCore(model, "Reader", "user@org.com"));
        Assert.Empty(model.Roles.Find("Reader")!.Members);
        Assert.False(ModelService.RemoveRoleMemberCore(model, "Reader", "user@org.com"));   // already gone
    }

    [Fact]
    public void SetRolePermission_changes_permission()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        var r = ModelService.SetRolePermissionCore(model, "Reader", "Administrator");
        Assert.Equal(TOM.ModelPermission.Administrator, r.ModelPermission);
    }

    [Fact]
    public void SetRolePermission_rejects_bad_permission()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        Assert.Throws<InvalidOperationException>(() => ModelService.SetRolePermissionCore(model, "Reader", "Bogus"));
    }

    // ================================================================ perspective removal
    [Fact]
    public void RemoveFromPerspective_removes_member_and_table()
    {
        var model = NewModel();
        ModelService.AddPerspectiveCore(model, "Finance");
        ModelService.AddToPerspectiveCore(model, "Finance", "Sales", "Total Sales");
        ModelService.AddToPerspectiveCore(model, "Finance", "Sales", "Region");

        Assert.True(ModelService.RemoveFromPerspectiveCore(model, "Finance", "measure", "Total Sales", "Sales"));
        var pt = model.Perspectives.Find("Finance")!.PerspectiveTables.Find("Sales")!;
        Assert.False(pt.PerspectiveMeasures.Contains("Total Sales"));
        Assert.True(pt.PerspectiveColumns.Contains("Region"));

        Assert.True(ModelService.RemoveFromPerspectiveCore(model, "Finance", "table", "Sales", null));
        Assert.False(model.Perspectives.Find("Finance")!.PerspectiveTables.Contains("Sales"));
    }

    [Fact]
    public void RemoveFromPerspective_returns_false_when_absent()
    {
        var model = NewModel();
        ModelService.AddPerspectiveCore(model, "Finance");
        ModelService.AddToPerspectiveCore(model, "Finance", "Sales", null);
        Assert.False(ModelService.RemoveFromPerspectiveCore(model, "Finance", "measure", "Nope", "Sales"));
    }

    [Fact]
    public void RemoveFromPerspective_rejects_bad_object_type()
    {
        var model = NewModel();
        ModelService.AddPerspectiveCore(model, "Finance");
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.RemoveFromPerspectiveCore(model, "Finance", "bogus", "x", null));
    }

    [Fact]
    public void DeletePerspective_removes_and_reports_existence()
    {
        var model = NewModel();
        ModelService.AddPerspectiveCore(model, "Finance");
        Assert.True(ModelService.DeletePerspectiveCore(model, "Finance"));
        Assert.False(model.Perspectives.Contains("Finance"));
        Assert.False(ModelService.DeletePerspectiveCore(model, "Finance"));
    }

    // ================================================================ variations
    [Fact]
    public void DeleteVariation_removes_variation()
    {
        var model = NewModel();
        var rel = AddSalesCustomerRel(model);
        // a variation needs a relationship + default hierarchy on the far table
        var v = ModelService.AddVariationCore(model, "Sales", "CustomerKey", "Sales_Customer", "Geography", true);
        var name = v.Name;

        Assert.True(ModelService.DeleteVariationCore(model, "Sales", "CustomerKey", name));
        Assert.False(model.Tables.Find("Sales")!.Columns.Find("CustomerKey")!.Variations.Contains(name));
        Assert.False(ModelService.DeleteVariationCore(model, "Sales", "CustomerKey", name));   // already gone
        _ = rel;
    }

    // ================================================================ data source
    [Fact]
    public void SetDataSource_creates_provider_data_source()
    {
        var model = NewModel();
        var (created, type) = ModelService.SetDataSourceCore(model, "DW", "Provider",
            connectionDetails: null, credential: null,
            connectionString: "Data Source=srv;Initial Catalog=db", provider: "System.Data.SqlClient",
            impersonation: "ImpersonateServiceAccount");

        Assert.True(created);
        Assert.Equal("Provider", type);
        var ds = (TOM.ProviderDataSource)model.DataSources.Find("DW")!;
        Assert.Equal("Data Source=srv;Initial Catalog=db", ds.ConnectionString);
        Assert.Equal("System.Data.SqlClient", ds.Provider);
        Assert.Equal(TOM.ImpersonationMode.ImpersonateServiceAccount, ds.ImpersonationMode);
    }

    [Fact]
    public void SetDataSource_creates_structured_data_source_from_json()
    {
        var model = NewModel();
        var (created, type) = ModelService.SetDataSourceCore(model, "PQ", "Structured",
            connectionDetails: "{\"protocol\":\"tds\",\"server\":\"srv\",\"database\":\"db\"}",
            credential: "{\"AuthenticationKind\":\"UsernamePassword\",\"Username\":\"u\"}",
            connectionString: null, provider: null, impersonation: null);

        Assert.True(created);
        Assert.Equal("Structured", type);
        var ds = (TOM.StructuredDataSource)model.DataSources.Find("PQ")!;
        Assert.NotNull(ds.ConnectionDetails);
        // values were set via the string indexer using the JSON keys; read them back the same way
        Assert.Equal("srv", ds.ConnectionDetails!["server"]?.ToString());
        Assert.Equal("db", ds.ConnectionDetails!["database"]?.ToString());
        Assert.NotNull(ds.Credential);
    }

    [Fact]
    public void SetDataSource_replaces_existing_in_place()
    {
        var model = NewModel();
        ModelService.SetDataSourceCore(model, "DW", "Provider", null, null, "cs1", "p", null);
        var (created, _) = ModelService.SetDataSourceCore(model, "DW", "Provider", null, null, "cs2", "p", null);

        Assert.False(created);   // replaced, not newly added
        Assert.Single(model.DataSources, d => d.Name == "DW");
        Assert.Equal("cs2", ((TOM.ProviderDataSource)model.DataSources.Find("DW")!).ConnectionString);
    }

    [Fact]
    public void SetDataSource_rejects_bad_kind()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetDataSourceCore(model, "X", "Cloud", null, null, null, null, null));
    }

    [Fact]
    public void SetDataSource_rejects_bad_impersonation()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetDataSourceCore(model, "X", "Provider", null, null, "cs", "p", "Bogus"));
    }
}
