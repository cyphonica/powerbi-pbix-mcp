using Microsoft.Data.Sqlite;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit tests for the engine-backed offline .pbix operations (<see cref="OfflinePbixService"/>):
/// the DAX/EVALUATE query construction (TOPN wrapping + table-name quoting), the ADOMD-reader -> result
/// serialisation (driven against a REAL in-memory SQLite reader through the very seam
/// <see cref="ModelService.SerialiseDaxReader"/> that run_dax uses), the model -> summary mapping over a
/// TOM model built in memory, and argument validation. No Power BI Desktop and no live engine - the
/// open-Desktop path itself is deliberately untested here (it needs a real Desktop, which no CI box has;
/// its one manual end-to-end check lives under examples/).
/// </summary>
public sealed class OfflinePbixServiceTests
{
    // ---------------- query construction ----------------

    [Fact]
    public void BuildTopNQuery_WrapsInTopN_AndSingleQuotesTheTable()
    {
        Assert.Equal("EVALUATE TOPN(1000, 'Sales')", OfflinePbixService.BuildTopNQuery("Sales", 1000));
        Assert.Equal("EVALUATE TOPN(5, 'Dim Date')", OfflinePbixService.BuildTopNQuery("Dim Date", 5));
    }

    [Fact]
    public void QuoteTableName_BareAndAlreadyQuoted_NormaliseIdentically()
    {
        Assert.Equal("'Sales'", OfflinePbixService.QuoteTableName("Sales"));
        Assert.Equal("'Sales'", OfflinePbixService.QuoteTableName("'Sales'"));
        Assert.Equal("'Sales'", OfflinePbixService.QuoteTableName("  Sales  "));
    }

    [Fact]
    public void QuoteTableName_DoublesAnEmbeddedSingleQuote()
    {
        // a table literally named Bob's must escape to 'Bob''s' so the query cannot be broken out of
        Assert.Equal("'Bob''s'", OfflinePbixService.QuoteTableName("Bob's"));
        Assert.Equal("'Bob''s'", OfflinePbixService.QuoteTableName("'Bob''s'"));
    }

    [Fact]
    public void QuoteTableName_EmptyIsRejected()
        => Assert.Throws<InvalidOperationException>(() => OfflinePbixService.QuoteTableName("   "));

    // ---------------- reader -> result serialisation (the run_dax seam, over a real SQLite reader) ----------------

    private static SqliteConnection SeededDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE t(a INTEGER, b TEXT);" +
                          "INSERT INTO t VALUES (1,'x'),(2,NULL),(3,'z');";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static (List<string> cols, List<object?[]> rows, bool truncated, int total) Read(
        SqliteConnection conn, int maxRows, bool countBeyondMax)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT a, b FROM t ORDER BY a";
        using var rdr = cmd.ExecuteReader();
        return ModelService.SerialiseDaxReader(rdr, maxRows, countBeyondMax);
    }

    [Fact]
    public void SerialiseDaxReader_CapturesColumns_Rows_AndMapsDbNullToNull()
    {
        using var conn = SeededDb();
        var (cols, rows, truncated, total) = Read(conn, maxRows: 100, countBeyondMax: false);

        Assert.Equal(new[] { "a", "b" }, cols);
        Assert.Equal(3, rows.Count);
        Assert.False(truncated);
        Assert.Equal(3, total);
        Assert.Equal(1L, Convert.ToInt64(rows[0][0]));
        Assert.Equal("x", rows[0][1]);
        Assert.Null(rows[1][1]);        // SQL NULL -> DBNull -> null, exactly as run_dax renders a blank
    }

    [Fact]
    public void SerialiseDaxReader_CapsAtMaxRows_AndFlagsTruncated()
    {
        using var conn = SeededDb();
        var (_, rows, truncated, total) = Read(conn, maxRows: 2, countBeyondMax: false);

        Assert.Equal(2, rows.Count);
        Assert.True(truncated);
        Assert.Equal(2, total);         // countBeyondMax false stops draining at the cap
    }

    [Fact]
    public void SerialiseDaxReader_CountBeyondMax_KeepsTrueCardinality_ButCapsStoredRows()
    {
        using var conn = SeededDb();
        var (_, rows, truncated, total) = Read(conn, maxRows: 2, countBeyondMax: true);

        Assert.Equal(2, rows.Count);    // stored rows still capped
        Assert.True(truncated);
        Assert.Equal(3, total);         // ...but total is the real row count
    }

    [Fact]
    public void SerialiseDaxReader_ZeroMaxRows_StoresNone()
    {
        using var conn = SeededDb();
        var (_, rows, truncated, _) = Read(conn, maxRows: 0, countBeyondMax: false);

        Assert.Empty(rows);
        Assert.True(truncated);
    }

    // ---------------- model -> summary mapping (over a TOM model built in memory) ----------------

    [Fact]
    public void MapModel_ShapesTables_MeasuresWithDax_AndRelationships()
    {
        var model = new TOM.Model { Name = "Sales Model" };

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "DateKey", DataType = TOM.DataType.Int64, SourceColumn = "DateKey" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount" });
        sales.Measures.Add(new TOM.Measure
        {
            Name = "Total Sales",
            Expression = "SUM(Sales[Amount])",
            FormatString = "#,0",
            DisplayFolder = "KPIs",
        });

        var date = new TOM.Table { Name = "Date" };
        date.Columns.Add(new TOM.DataColumn { Name = "DateKey", DataType = TOM.DataType.Int64, SourceColumn = "DateKey" });

        model.Tables.Add(sales);
        model.Tables.Add(date);
        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            FromColumn = sales.Columns["DateKey"],
            ToColumn = date.Columns["DateKey"],
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
        });

        // shape via JSON so the anonymous result is inspected exactly as a tool caller would see it
        var json = System.Text.Json.JsonSerializer.SerializeToNode(OfflinePbixService.MapModel(model))!.AsObject();

        Assert.Equal("Sales Model", (string?)json["name"]);
        Assert.Equal(2, (int?)json["tableCount"]);

        var tables = json["tables"]!.AsArray();
        var salesJson = tables.First(t => (string?)t!["name"] == "Sales")!.AsObject();
        var cols = salesJson["columns"]!.AsArray();
        Assert.Equal(2, cols.Count);
        Assert.Equal("Int64", (string?)cols.First(c => (string?)c!["name"] == "DateKey")!["dataType"]);

        var measures = salesJson["measures"]!.AsArray();
        Assert.Single(measures);
        Assert.Equal("Total Sales", (string?)measures[0]!["name"]);
        Assert.Equal("SUM(Sales[Amount])", (string?)measures[0]!["expression"]);   // measure DAX must be present
        Assert.Equal("#,0", (string?)measures[0]!["format"]);
        Assert.Equal("KPIs", (string?)measures[0]!["folder"]);

        var rels = json["relationships"]!.AsArray();
        Assert.Single(rels);
        Assert.Equal("Sales[DateKey]", (string?)rels[0]!["from"]);
        Assert.Equal("Date[DateKey]", (string?)rels[0]!["to"]);
    }

    // ---------------- argument validation ----------------

    [Fact]
    public void ValidatePbixPath_Missing_WrongExtension_AndAbsent_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => OfflinePbixService.ValidatePbixPath("   "));
        Assert.Throws<InvalidOperationException>(() => OfflinePbixService.ValidatePbixPath("D:\\daxtmp\\model.txt"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => OfflinePbixService.ValidatePbixPath("D:\\daxtmp\\does-not-exist-" + Guid.NewGuid().ToString("N") + ".pbix"));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTopN_RejectsNonPositive_AndCapsAtMax()
    {
        Assert.Throws<InvalidOperationException>(() => OfflinePbixService.ValidateTopN(0));
        Assert.Throws<InvalidOperationException>(() => OfflinePbixService.ValidateTopN(-5));
        Assert.Equal(1000, OfflinePbixService.ValidateTopN(1000));
        Assert.Equal(OfflinePbixService.MaxTopN, OfflinePbixService.ValidateTopN(OfflinePbixService.MaxTopN + 1));
    }

    [Fact]
    public void ClampTimeout_DefaultsWhenNonPositive_AndCapsAtCeiling()
    {
        Assert.Equal(OfflinePbixService.DefaultTimeoutSec, OfflinePbixService.ClampTimeout(0));
        Assert.Equal(OfflinePbixService.DefaultTimeoutSec, OfflinePbixService.ClampTimeout(-1));
        Assert.Equal(60, OfflinePbixService.ClampTimeout(60));
        Assert.Equal(OfflinePbixService.MaxTimeoutSec, OfflinePbixService.ClampTimeout(OfflinePbixService.MaxTimeoutSec + 100));
    }

    [Fact]
    public void EmptyDax_IsRejectedBeforeAnyDesktopLaunch()
    {
        // eval_dax_offline normalises the query first (the run_dax guard), so an empty DAX never reaches Desktop
        Assert.Throws<InvalidOperationException>(() => ModelService.NormaliseDaxQuery("   "));
    }
}
