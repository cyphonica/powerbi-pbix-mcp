using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Unit tests for the Tabular-model security (RLS/OLS) and advanced-calc tools. No live Analysis
/// Services server is needed: each test builds an in-memory <c>new TOM.Model()</c> with a couple of
/// tables/columns/measures, calls the *Core mutation helper, and asserts on the resulting object tree.
/// SaveChanges() (which requires a live connection) is deliberately not exercised here.
/// </summary>
public sealed class ModelSecurityAndCalcTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model();

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        model.Tables.Add(sales);

        var calendar = new TOM.Table { Name = "Calendar" };
        calendar.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name" });
        model.Tables.Add(calendar);

        return model;
    }

    // ------------------------------------------------------------------ roles
    [Fact]
    public void AddRole_creates_role_with_model_permission()
    {
        var model = NewModel();
        var role = ModelService.AddRoleCore(model, "Sales Reader", "ReadRefresh");

        Assert.True(model.Roles.Contains("Sales Reader"));
        Assert.Equal(TOM.ModelPermission.ReadRefresh, role.ModelPermission);
        Assert.Same(role, model.Roles.Find("Sales Reader"));
    }

    [Fact]
    public void AddRole_rejects_duplicate_name()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        Assert.Throws<InvalidOperationException>(() => ModelService.AddRoleCore(model, "Reader", "Read"));
    }

    [Fact]
    public void AddRole_rejects_invalid_permission()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.AddRoleCore(model, "Reader", "Bogus"));
    }

    [Fact]
    public void DeleteRole_removes_role_and_reports_existence()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");

        Assert.True(ModelService.DeleteRoleCore(model, "Reader"));
        Assert.False(model.Roles.Contains("Reader"));
        Assert.False(ModelService.DeleteRoleCore(model, "Reader"));   // already gone
    }

    // ------------------------------------------------------------------ RLS
    [Fact]
    public void SetRls_sets_filter_expression_on_table_permission()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        var tp = ModelService.SetRlsCore(model, "Reader", "Sales", "'Sales'[Region] = \"NZ\"");

        Assert.Equal("'Sales'[Region] = \"NZ\"", tp.FilterExpression);
        Assert.Equal("Sales", tp.Name);
        var role = model.Roles.Find("Reader")!;
        Assert.Single(role.TablePermissions);
    }

    [Fact]
    public void SetRls_updates_existing_table_permission_in_place()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        ModelService.SetRlsCore(model, "Reader", "Sales", "FALSE()");
        ModelService.SetRlsCore(model, "Reader", "Sales", "TRUE()");

        var role = model.Roles.Find("Reader")!;
        Assert.Single(role.TablePermissions);   // not duplicated
        Assert.Equal("TRUE()", role.TablePermissions.Find("Sales")!.FilterExpression);
    }

    [Fact]
    public void SetRls_throws_for_unknown_role()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetRlsCore(model, "Nope", "Sales", "TRUE()"));
    }

    // ------------------------------------------------------------------ members
    [Fact]
    public void AddRoleMember_adds_windows_member_by_default()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        var member = ModelService.AddRoleMemberCore(model, "Reader", "user@org.com", null);

        Assert.IsType<TOM.WindowsModelRoleMember>(member);
        Assert.Equal("user@org.com", member.MemberName);
        Assert.Single(model.Roles.Find("Reader")!.Members);
    }

    [Fact]
    public void AddRoleMember_adds_external_member_when_provider_given()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        var member = ModelService.AddRoleMemberCore(model, "Reader", "user@org.com", "AzureAD");

        var ext = Assert.IsType<TOM.ExternalModelRoleMember>(member);
        Assert.Equal("AzureAD", ext.IdentityProvider);
    }

    // ------------------------------------------------------------------ OLS
    [Fact]
    public void SetTableOls_sets_metadata_permission()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        var tp = ModelService.SetTableOlsCore(model, "Reader", "Calendar", "None");

        Assert.Equal(TOM.MetadataPermission.None, tp.MetadataPermission);
        Assert.Equal("Calendar", tp.Name);
    }

    [Fact]
    public void SetColumnOls_sets_metadata_permission_on_column()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        var cp = ModelService.SetColumnOlsCore(model, "Reader", "Sales", "Amount", "None");

        Assert.Equal(TOM.MetadataPermission.None, cp.MetadataPermission);
        var tp = model.Roles.Find("Reader")!.TablePermissions.Find("Sales")!;
        Assert.Same(cp, tp.ColumnPermissions.Find("Amount"));
    }

    [Fact]
    public void SetColumnOls_shares_table_permission_with_rls()
    {
        var model = NewModel();
        ModelService.AddRoleCore(model, "Reader", "Read");
        ModelService.SetRlsCore(model, "Reader", "Sales", "TRUE()");
        ModelService.SetColumnOlsCore(model, "Reader", "Sales", "Amount", "None");

        var role = model.Roles.Find("Reader")!;
        Assert.Single(role.TablePermissions);   // RLS + column OLS reuse one TablePermission for the table
        var tp = role.TablePermissions.Find("Sales")!;
        Assert.Equal("TRUE()", tp.FilterExpression);
        Assert.Single(tp.ColumnPermissions);
    }

    // ------------------------------------------------------------------ calculation groups
    [Fact]
    public void AddCalculationGroup_sets_calculation_group_on_table()
    {
        var model = NewModel();
        var cg = ModelService.AddCalculationGroupCore(model, "Calendar", precedence: 10);

        Assert.NotNull(model.Tables.Find("Calendar")!.CalculationGroup);
        Assert.Same(cg, model.Tables.Find("Calendar")!.CalculationGroup);
        Assert.Equal(10, cg.Precedence);
    }

    [Fact]
    public void AddCalculationGroup_rejects_second_group_on_same_table()
    {
        var model = NewModel();
        ModelService.AddCalculationGroupCore(model, "Calendar", null);
        Assert.Throws<InvalidOperationException>(() => ModelService.AddCalculationGroupCore(model, "Calendar", null));
    }

    [Fact]
    public void AddCalculationItem_adds_item_with_expression_ordinal_and_format()
    {
        var model = NewModel();
        ModelService.AddCalculationGroupCore(model, "Calendar", null);
        var item = ModelService.AddCalculationItemCore(model, "Calendar", "YTD",
            "CALCULATE(SELECTEDMEASURE(), DATESYTD('Calendar'[Date]))", ordinal: 1, formatStringExpression: "\"#,0\"");

        var cg = model.Tables.Find("Calendar")!.CalculationGroup!;
        Assert.True(cg.CalculationItems.Contains("YTD"));
        Assert.Equal("CALCULATE(SELECTEDMEASURE(), DATESYTD('Calendar'[Date]))", item.Expression);
        Assert.Equal(1, item.Ordinal);
        Assert.NotNull(item.FormatStringDefinition);
        Assert.Equal("\"#,0\"", item.FormatStringDefinition!.Expression);
    }

    [Fact]
    public void AddCalculationItem_requires_a_calculation_group()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddCalculationItemCore(model, "Calendar", "YTD", "SELECTEDMEASURE()", null, null));
    }

    // ------------------------------------------------------------------ KPI
    [Fact]
    public void SetKpi_attaches_kpi_to_measure()
    {
        var model = NewModel();
        var kpi = ModelService.SetKpiCore(model, "Sales", "Total Sales", "[Total Sales] * 1.1",
            "IF([Total Sales] >= [Total Sales] * 1.1, 1, -1)", null);

        var me = model.Tables.Find("Sales")!.Measures.Find("Total Sales")!;
        Assert.NotNull(me.KPI);
        Assert.Same(kpi, me.KPI);
        Assert.Equal("[Total Sales] * 1.1", me.KPI!.TargetExpression);
        Assert.Equal("Three Circles Colored", me.KPI!.StatusGraphic);   // default applied
    }

    [Fact]
    public void SetKpi_throws_for_unknown_measure()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetKpiCore(model, "Sales", "Nope", "1", "1", null));
    }

    // ------------------------------------------------------------------ detail rows
    [Fact]
    public void SetDetailRows_sets_definition_on_measure()
    {
        var model = NewModel();
        ModelService.SetDetailRowsCore(model, "Sales", "Total Sales", "SELECTCOLUMNS('Sales', \"Region\", 'Sales'[Region])");

        var me = model.Tables.Find("Sales")!.Measures.Find("Total Sales")!;
        Assert.NotNull(me.DetailRowsDefinition);
        Assert.Equal("SELECTCOLUMNS('Sales', \"Region\", 'Sales'[Region])", me.DetailRowsDefinition!.Expression);
    }

    [Fact]
    public void SetDetailRows_sets_table_default_when_measure_omitted()
    {
        var model = NewModel();
        ModelService.SetDetailRowsCore(model, "Sales", null, "'Sales'");

        var t = model.Tables.Find("Sales")!;
        Assert.NotNull(t.DefaultDetailRowsDefinition);
        Assert.Equal("'Sales'", t.DefaultDetailRowsDefinition!.Expression);
    }

    // ------------------------------------------------------------------ dynamic format string
    [Fact]
    public void SetDynamicFormatString_sets_on_measure()
    {
        var model = NewModel();
        ModelService.SetDynamicFormatStringCore(model, "Sales", "Total Sales", null, "\"$#,0\"");

        var me = model.Tables.Find("Sales")!.Measures.Find("Total Sales")!;
        Assert.NotNull(me.FormatStringDefinition);
        Assert.Equal("\"$#,0\"", me.FormatStringDefinition!.Expression);
    }

    [Fact]
    public void SetDynamicFormatString_sets_on_calculation_item()
    {
        var model = NewModel();
        ModelService.AddCalculationGroupCore(model, "Calendar", null);
        ModelService.AddCalculationItemCore(model, "Calendar", "Pct", "SELECTEDMEASURE()", null, null);
        ModelService.SetDynamicFormatStringCore(model, "Calendar", null, "Pct", "\"0.0%\"");

        var item = model.Tables.Find("Calendar")!.CalculationGroup!.CalculationItems.Find("Pct")!;
        Assert.NotNull(item.FormatStringDefinition);
        Assert.Equal("\"0.0%\"", item.FormatStringDefinition!.Expression);
    }

    [Fact]
    public void SetDynamicFormatString_requires_a_target()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetDynamicFormatStringCore(model, "Sales", null, null, "\"#,0\""));
    }
}
