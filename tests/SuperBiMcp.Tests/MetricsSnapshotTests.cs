using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive tests for the metrics / KPI snapshot (the dashboard's headline numbers the cloud uses for the
/// build-summary email and for alert evaluation). The builder is driven over small, hand-written CSV fixtures in a
/// throwaway work dir, so every KPI is computed over real bytes - not a mock.
///
/// The snapshot shape is asserted against the contract:
///   { generatedAt, kpis: [ { id, label, value, format } ], tables: [ { name, rows } ] }.
///
/// Fault-sensitivity is the whole point: the asserted KPI VALUES (a SUM of an amount column, a DISTINCT COUNT of an
/// id column) are pinned to the exact arithmetic over the fixture, so a wrong aggregate (e.g. SUM where DISTINCT
/// COUNT was meant, or an off-by-one) fails the test. The honesty rule is proven too: a KPI that cannot be computed
/// (its column is missing or empty) is OMITTED, never reported as a fabricated 0.
/// </summary>
public sealed class MetricsSnapshotTests
{
    // ---- fixtures ------------------------------------------------------------------------------

    private static string Csv(TempDir work, string name, params string[] lines)
    {
        string path = work.File(name + ".csv");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static Dictionary<string, string> Map(params (string name, string path)[] tables)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, p) in tables) d[n] = p;
        return d;
    }

    private static JsonArray Kpis(JsonObject snap) => (JsonArray)snap["kpis"]!;

    /// <summary>The single KPI whose id starts with the given prefix, or null when no such KPI was emitted (an
    /// omitted, uncomputable KPI).</summary>
    private static JsonObject? Kpi(JsonObject snap, string idPrefix)
        => Kpis(snap).OfType<JsonObject>()
            .FirstOrDefault(k => ((string?)k["id"])?.StartsWith(idPrefix, StringComparison.Ordinal) == true);

    private static double? ValueOf(JsonObject snap, string idPrefix) => (double?)Kpi(snap, idPrefix)?["value"];
    private static string? FormatOf(JsonObject snap, string idPrefix) => (string?)Kpi(snap, idPrefix)?["format"];

    // a fact table with an amount + an id, and a model whose measures sum the amount and count the id.
    private const string FactSpec = """
    {
      "tables": [
        {
          "name": "Sales",
          "m": "let Source = Csv.Document(File.Contents(DataFolder & \"/Sales.csv\")) in Source",
          "columns": [
            { "name": "OrderId", "dataType": "int64" },
            { "name": "CustomerId", "dataType": "int64" },
            { "name": "Amount", "dataType": "double" }
          ],
          "measures": [
            { "name": "Total Revenue", "dax": "SUM(Sales[Amount])", "format": "\\$#,0" },
            { "name": "Orders", "dax": "DISTINCTCOUNT(Sales[OrderId])", "format": "#,0" }
          ]
        }
      ]
    }
    """;

    private static (TempDir work, Dictionary<string, string> tables) FactFixture()
    {
        var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales",
            "OrderId,CustomerId,Amount",
            "1,100,10.50",
            "2,100,4.50",
            "3,200,5.00",
            "1,300,0.00")));   // OrderId 1 repeats -> 3 distinct order ids; amounts sum to 20.00
        return (work, tables);
    }

    // ---- the headline: KPI values are computed exactly from the fixture -------------------------

    [Fact]
    public void FactTable_SumAndDistinctCount_HaveExactValuesAndFormats()
    {
        var (work, tables) = FactFixture();
        using (work)
        {
            var snap = MetricsSnapshot.Build(tables, FactSpec, work.Path);

            // SUM(Amount) = 10.50 + 4.50 + 5.00 + 0.00 = 20.00 (the load-bearing arithmetic - a wrong aggregate
            // would not equal this).
            Assert.Equal(20.0, ValueOf(snap, "measure:Total Revenue"));
            Assert.Equal("currency", FormatOf(snap, "measure:Total Revenue"));   // from the "\$#,0" format string

            // DISTINCTCOUNT(OrderId) = {1,2,3} = 3 distinct (NOT 4 rows - this is what distinguishes the right
            // aggregate from a plain row count).
            Assert.Equal(3.0, ValueOf(snap, "measure:Orders"));
            Assert.Equal("number", FormatOf(snap, "measure:Orders"));            // from the "#,0" format string
        }
    }

    [Fact]
    public void PerTable_RowCount_IsTheWholeFile()
    {
        var (work, tables) = FactFixture();
        using (work)
        {
            var snap = MetricsSnapshot.Build(tables, FactSpec, work.Path);

            var t0 = (JsonObject)((JsonArray)snap["tables"]!)[0]!;
            Assert.Equal("Sales", (string?)t0["name"]);
            Assert.Equal(4, (int?)t0["rows"]);     // 4 data rows, not 3 distinct
        }
    }

    // ---- honesty: an uncomputable KPI is OMITTED, never faked to zero --------------------------

    [Fact]
    public void MeasureOverMissingColumn_IsOmitted_NotZeroFaked()
    {
        using var work = Fixtures.NewWorkDir();
        // the loaded CSV has NO "Amount" column, so SUM(Sales[Amount]) cannot be computed.
        var tables = Map(("Sales", Csv(work, "Sales", "OrderId", "1", "2", "3")));
        string spec = """
        {
          "tables": [
            {
              "name": "Sales",
              "columns": [ { "name": "OrderId", "dataType": "int64" } ],
              "measures": [
                { "name": "Total Revenue", "dax": "SUM(Sales[Amount])", "format": "\\$#,0" },
                { "name": "Orders", "dax": "DISTINCTCOUNT(Sales[OrderId])", "format": "#,0" }
              ]
            }
          ]
        }
        """;

        var snap = MetricsSnapshot.Build(tables, spec, work.Path);

        // the revenue KPI must be ABSENT (its column is gone) - not present with value 0.
        Assert.Null(Kpi(snap, "measure:Total Revenue"));
        // the computable one is still emitted, with the right value.
        Assert.Equal(3.0, ValueOf(snap, "measure:Orders"));
    }

    [Fact]
    public void MeasureOverEmptyAmountColumn_IsOmitted_NotZeroFaked()
    {
        using var work = Fixtures.NewWorkDir();
        // Amount exists but every value is blank -> no parseable number -> SUM cannot be computed -> omitted.
        var tables = Map(("Sales", Csv(work, "Sales",
            "OrderId,Amount",
            "1,",
            "2,")));
        string spec = """
        {
          "tables": [
            {
              "name": "Sales",
              "columns": [ { "name": "OrderId", "dataType": "int64" }, { "name": "Amount", "dataType": "double" } ],
              "measures": [ { "name": "Total Revenue", "dax": "SUM(Sales[Amount])", "format": "\\$#,0" } ]
            }
          ]
        }
        """;

        var snap = MetricsSnapshot.Build(tables, spec, work.Path);

        Assert.Null(Kpi(snap, "measure:Total Revenue"));   // omitted, NOT value 0
    }

    [Fact]
    public void CompositeMeasure_NotEvaluableFromRawData_IsOmitted()
    {
        using var work = Fixtures.NewWorkDir();
        // neutral column names (no amount/id word-token) so nothing triggers the fallback - this isolates the
        // "composite measures are omitted, never guessed" behaviour.
        var tables = Map(("Sales", Csv(work, "Sales", "Region,Qty", "North,10", "South,20")));
        // a CALCULATE / DIVIDE / time-intelligence measure is not a bare single aggregation; the scaffold build
        // path has no DAX engine, so it is omitted rather than guessed.
        string spec = """
        {
          "tables": [
            {
              "name": "Sales",
              "columns": [ { "name": "Region", "dataType": "string" }, { "name": "Qty", "dataType": "int64" } ],
              "measures": [
                { "name": "TY Qty", "dax": "CALCULATE(SUM(Sales[Qty]), Calendar[Period] = \"This Year\")", "format": "#,0" },
                { "name": "Avg Qty", "dax": "DIVIDE(SUM(Sales[Qty]), DISTINCTCOUNT(Sales[Region]))", "format": "#,0" }
              ]
            }
          ]
        }
        """;

        var snap = MetricsSnapshot.Build(tables, spec, work.Path);

        Assert.Null(Kpi(snap, "measure:TY Qty"));
        Assert.Null(Kpi(snap, "measure:Avg Qty"));
        Assert.Empty(Kpis(snap));   // neither measure is evaluable, and no column is an obvious amount/id fallback
    }

    // ---- the no-measure fallback: obvious aggregates over the columns --------------------------

    [Fact]
    public void NoMeasures_FallsBackToAmountSum_AndIdDistinctCount()
    {
        var (work, tables) = FactFixture();
        using (work)
        {
            // no spec at all -> the column-name fallback must supply the headline numbers.
            var snap = MetricsSnapshot.Build(tables, null, work.Path);

            // SUM of the "Amount" column = 20.00
            Assert.Equal(20.0, ValueOf(snap, "total:Sales.Amount"));
            Assert.Equal("currency", FormatOf(snap, "total:Sales.Amount"));
            // DISTINCT COUNT of an id-like column = 3 distinct OrderId.
            Assert.Equal(3.0, ValueOf(snap, "distinct:Sales.OrderId"));
            Assert.Equal("number", FormatOf(snap, "distinct:Sales.OrderId"));
        }
    }

    [Fact]
    public void Fallback_DistinctCount_IsDistinctNotRowCount()
    {
        using var work = Fixtures.NewWorkDir();
        // 4 rows but only 2 distinct CustomerId values -> the distinct-count KPI must be 2, proving it is a
        // DISTINCT count and not a row/non-blank count (the deliberate-break sentinel for the count aggregate).
        var tables = Map(("Orders", Csv(work, "Orders",
            "CustomerId,Amount",
            "100,1",
            "100,2",
            "200,3",
            "200,4")));

        var snap = MetricsSnapshot.Build(tables, null, work.Path);

        Assert.Equal(2.0, ValueOf(snap, "distinct:Orders.CustomerId"));   // distinct {100,200} = 2, not 4
        Assert.Equal(10.0, ValueOf(snap, "total:Orders.Amount"));         // 1+2+3+4 = 10
    }

    // ---- aggregation parsing + direct evaluation (drives the real internals) -------------------

    [Theory]
    [InlineData("SUM(Sales[Amount])", true, "SUM", "Sales", "Amount")]
    [InlineData("AVERAGE(Sales[Price])", true, "AVERAGE", "Sales", "Price")]
    [InlineData("DISTINCTCOUNT(Sales[OrderId])", true, "DISTINCTCOUNT", "Sales", "OrderId")]
    [InlineData("COUNT(Sales[Id])", true, "COUNT", "Sales", "Id")]
    [InlineData("DIVIDE([A], [B])", false, "", "", "")]                          // not an aggregation
    [InlineData("CALCULATE(SUM(Sales[Amount]), Cal[P]=\"TY\")", false, "", "", "")] // wrapped + 2 refs
    [InlineData("SUM(Sales[A]) + SUM(Sales[B])", false, "", "", "")]             // two refs -> ambiguous
    public void TryResolveAggregation_ExtractsBareSingleAggregation(
        string dax, bool ok, string agg, string table, string col)
    {
        bool got = MetricsSnapshot.TryResolveAggregation(dax, out string a, out string t, out string c);
        Assert.Equal(ok, got);
        if (ok) { Assert.Equal(agg, a); Assert.Equal(table, t); Assert.Equal(col, c); }
    }

    [Fact]
    public void TryEvaluate_ComputesEachAggregateExactly_OverARealColumn()
    {
        using var work = Fixtures.NewWorkDir();
        string path = Csv(work, "Sales",
            "Amount",
            "10",
            "20",
            "30",
            "20");   // sum=80, avg=20, min=10, max=30, count=4, distinct=3
        var t = MetricsSnapshot.LoadedTable.Read("Sales", path);
        int ci = t.IndexOf("Amount");

        Assert.True(MetricsSnapshot.TryEvaluate("SUM", t, ci, out double sum));
        Assert.Equal(80.0, sum);
        Assert.True(MetricsSnapshot.TryEvaluate("AVERAGE", t, ci, out double avg));
        Assert.Equal(20.0, avg);
        Assert.True(MetricsSnapshot.TryEvaluate("MIN", t, ci, out double min));
        Assert.Equal(10.0, min);
        Assert.True(MetricsSnapshot.TryEvaluate("MAX", t, ci, out double max));
        Assert.Equal(30.0, max);
        Assert.True(MetricsSnapshot.TryEvaluate("COUNT", t, ci, out double count));
        Assert.Equal(4.0, count);
        Assert.True(MetricsSnapshot.TryEvaluate("DISTINCTCOUNT", t, ci, out double distinct));
        Assert.Equal(3.0, distinct);
    }

    [Fact]
    public void TryEvaluate_NumericAggregateOverNonNumericColumn_ReturnsFalse_NotZero()
    {
        using var work = Fixtures.NewWorkDir();
        string path = Csv(work, "Sales", "Name", "Acme", "Globex");
        var t = MetricsSnapshot.LoadedTable.Read("Sales", path);
        int ci = t.IndexOf("Name");

        // SUM over a text column cannot be computed -> false (so the KPI is omitted), value is NOT silently 0.
        Assert.False(MetricsSnapshot.TryEvaluate("SUM", t, ci, out _));
    }

    [Theory]
    [InlineData("\\$#,0", "currency")]
    [InlineData("$#,##0.00", "currency")]
    [InlineData("0.0%", "percent")]
    [InlineData("0.0%;-0.0%;0.0%", "percent")]
    [InlineData("#,0", null)]          // plain number format -> no override, caller falls back
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FormatFromString_MapsPowerBiFormatToContractToken(string? format, string? expected)
        => Assert.Equal(expected, MetricsSnapshot.FormatFromString(format));

    // ---- contract shape ------------------------------------------------------------------------

    [Fact]
    public void Snapshot_HasExactContractShape()
    {
        var (work, tables) = FactFixture();
        using (work)
        {
            var snap = MetricsSnapshot.Build(tables, FactSpec, work.Path);

            // top-level: generatedAt, kpis, tables
            foreach (var key in new[] { "generatedAt", "kpis", "tables" })
                Assert.True(snap.ContainsKey(key), $"snapshot is missing '{key}'");
            Assert.NotNull((string?)snap["generatedAt"]);

            // each KPI entry: id, label, value (a number), format (one of the three tokens)
            Assert.NotEmpty(Kpis(snap));
            foreach (var k in Kpis(snap))
            {
                var ko = (JsonObject)k!;
                foreach (var key in new[] { "id", "label", "value", "format" })
                    Assert.True(ko.ContainsKey(key), $"kpi is missing '{key}'");
                Assert.NotNull((double?)ko["value"]);                       // a real number, not null/string
                Assert.Contains((string?)ko["format"], new[] { "number", "currency", "percent" });
            }

            // each table entry: name, rows
            foreach (var t in (JsonArray)snap["tables"]!)
            {
                var to = (JsonObject)t!;
                Assert.True(to.ContainsKey("name") && to.ContainsKey("rows"));
            }
        }
    }

    // ---- persistence ---------------------------------------------------------------------------

    [Fact]
    public void BuildAndSave_WritesMetricsJson_NextToTheProject()
    {
        var (work, tables) = FactFixture();
        using (work)
        {
            string projDir = work.File("project");

            var snap = MetricsSnapshot.BuildAndSave(tables, FactSpec, work.Path, projDir);

            string file = Path.Combine(projDir, MetricsSnapshot.FileName);
            Assert.True(File.Exists(file), "metrics.json should be written into the project dir");
            Assert.Equal("metrics.json", MetricsSnapshot.FileName);

            var onDisk = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
            // the persisted copy carries the same KPI values as the returned object.
            Assert.Equal(ValueOf(snap, "measure:Total Revenue"),
                         (double?)Kpi(onDisk, "measure:Total Revenue")?["value"]);
            Assert.Equal(20.0, (double?)Kpi(onDisk, "measure:Total Revenue")?["value"]);
        }
    }
}
