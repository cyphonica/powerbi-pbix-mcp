using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive tests for the visible verification report (the "did the build load real, refreshable
/// data?" moat). Each check is exercised with a small, hand-written CSV fixture in a throwaway work dir, so
/// the row-count / null-column / key-coverage / calendar checks run over real bytes, and the refresh-safety /
/// primary-measure checks run over a real model spec. The report shape is asserted against the contract:
///   { status, generatedAt, summary, tables[], checks[] }, status = worst of the checks.
///
/// Fault-sensitivity is proven both ways: a healthy dataset must be all-pass, and each deliberate fault
/// (empty table, all-null column, a hardcoded refresh-unsafe date, an orphan key) must flip exactly the
/// matching check to warn/fail AND drag the overall status down to that worst level.
/// </summary>
public sealed class VerificationReportTests
{
    // ---- fixtures ------------------------------------------------------------------------------

    /// <summary>Write a CSV (header + rows) into the work dir under "<name>.csv" and return its path.</summary>
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

    private static JsonArray Checks(JsonObject report) => (JsonArray)report["checks"]!;

    /// <summary>The single check whose id starts with the given prefix (the per-table/column checks carry the
    /// table name in the id), or null when no such check ran (an omitted, uncomputable check).</summary>
    private static JsonObject? Check(JsonObject report, string idPrefix)
        => Checks(report).OfType<JsonObject>()
            .FirstOrDefault(c => ((string?)c["id"])?.StartsWith(idPrefix, StringComparison.Ordinal) == true);

    private static string? StatusOf(JsonObject report, string idPrefix) => (string?)Check(report, idPrefix)?["status"];

    // a healthy two-table model with a calendar, a relationship and a refresh-safe measure.
    private static string HealthySpec() => """
    {
      "tables": [
        {
          "name": "Sales",
          "m": "let Source = Csv.Document(File.Contents(DataFolder & \"/Sales.csv\")) in Source",
          "columns": [
            { "name": "ProductKey", "dataType": "int64" },
            { "name": "OrderDate", "dataType": "date" },
            { "name": "Amount", "dataType": "double" }
          ],
          "measures": [
            { "name": "Total Sales", "dax": "SUM(Sales[Amount])" },
            { "name": "TY Sales", "dax": "CALCULATE([Total Sales], Calendar[Period] = \"This Year\")" }
          ]
        },
        {
          "name": "Product",
          "m": "let Source = Csv.Document(File.Contents(DataFolder & \"/Product.csv\")) in Source",
          "columns": [ { "name": "ProductKey", "dataType": "int64" }, { "name": "Brand", "dataType": "string" } ]
        },
        {
          "name": "Calendar",
          "m": "let Source = Csv.Document(File.Contents(DataFolder & \"/Calendar.csv\")) in Source",
          "columns": [ { "name": "OrderDate", "dataType": "date" }, { "name": "Period", "dataType": "string" } ]
        }
      ],
      "relationships": [
        { "fromTable": "Sales", "fromColumn": "ProductKey", "toTable": "Product", "toColumn": "ProductKey" }
      ]
    }
    """;

    // ---- the headline: a healthy dataset is all-pass --------------------------------------------

    [Fact]
    public void HealthyDataset_AllChecksPass_StatusPass()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(
            ("Sales", Csv(work, "Sales",
                "ProductKey,OrderDate,Amount",
                "1,2024-01-31,100.0",
                "2,2024-02-29,250.5",
                "1,2024-03-31,75.0")),
            ("Product", Csv(work, "Product",
                "ProductKey,Brand",
                "1,Acme",
                "2,Globex")),
            ("Calendar", Csv(work, "Calendar",
                "OrderDate,Period",
                "2024-01-31,This Year",
                "2024-02-29,This Year",
                "2024-03-31,This Year")));

        var report = VerificationReport.Build(tables, HealthySpec(), work.Path);

        Assert.Equal("pass", (string?)report["status"]);
        // every check that ran must be pass
        Assert.All(Checks(report), c => Assert.Equal("pass", (string?)c!["status"]));
        // contract fields present
        Assert.NotNull((string?)report["generatedAt"]);
        Assert.NotNull((string?)report["summary"]);
        Assert.Equal(3, ((JsonArray)report["tables"]!).Count);
        // the key checks actually ran (not silently omitted)
        Assert.Equal("pass", StatusOf(report, "rows-loaded:Sales"));
        Assert.Equal("pass", StatusOf(report, "refresh-safety"));
        Assert.Equal("pass", StatusOf(report, "calendar-continuity"));
        Assert.Equal("pass", StatusOf(report, "primary-measure"));
        Assert.Equal("pass", StatusOf(report, "rel-coverage:Sales"));
    }

    // ---- empty table -> a FAIL check, status fail ----------------------------------------------

    [Fact]
    public void EmptyTable_ProducesFailCheck_AndStatusFail()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "ProductKey,Amount"))); // header only, zero data rows

        var report = VerificationReport.Build(tables, null, work.Path);

        Assert.Equal("fail", (string?)report["status"]);
        var rows = Check(report, "rows-loaded:Sales");
        Assert.NotNull(rows);
        Assert.Equal("fail", (string?)rows!["status"]);
        Assert.Contains("0 rows", (string?)rows["detail"]);
        // the table summary reports 0 rows for it
        var t0 = (JsonObject)((JsonArray)report["tables"]!)[0]!;
        Assert.Equal(0, (int?)t0["rows"]);
    }

    [Fact]
    public void NonEmptyTable_RowsLoadedCheck_Passes()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Id,Amount", "1,10", "2,20")));

        var report = VerificationReport.Build(tables, null, work.Path);

        Assert.Equal("pass", StatusOf(report, "rows-loaded:Sales"));
        Assert.Equal(2, (int?)((JsonObject)((JsonArray)report["tables"]!)[0]!)["rows"]);
    }

    // ---- all-null column -> a WARN check, status warn ------------------------------------------

    [Fact]
    public void AllNullColumn_ProducesWarnCheck_AndStatusWarn()
    {
        using var work = Fixtures.NewWorkDir();
        // "Notes" is blank in every row
        var tables = Map(("Sales", Csv(work, "Sales",
            "Id,Amount,Notes",
            "1,10,",
            "2,20,",
            "3,30,")));

        var report = VerificationReport.Build(tables, null, work.Path);

        Assert.Equal("warn", (string?)report["status"]);     // worst check is the warn
        var nullCheck = Check(report, "all-null:Sales.Notes");
        Assert.NotNull(nullCheck);
        Assert.Equal("warn", (string?)nullCheck!["status"]);
        // a column WITH data must NOT raise the warn
        Assert.Null(Check(report, "all-null:Sales.Amount"));
        // rows still loaded, so that check passes
        Assert.Equal("pass", StatusOf(report, "rows-loaded:Sales"));
    }

    // ---- refresh-unsafe measure / query -> warn or fail ----------------------------------------

    [Fact]
    public void HardcodedDateInMeasure_IsRefreshUnsafe_Fail()
    {
        // a DATE() literal in a measure silently freezes on the next refresh -> FAIL
        string spec = """
        {
          "tables": [
            {
              "name": "Sales",
              "m": "let Source = Csv.Document(File.Contents(DataFolder & \"/Sales.csv\")) in Source",
              "columns": [ { "name": "Amount", "dataType": "double" } ],
              "measures": [
                { "name": "Sales To Date", "dax": "CALCULATE(SUM(Sales[Amount]), Sales[OrderDate] <= DATE(2024,1,1))" }
              ]
            }
          ]
        }
        """;
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Amount", "10", "20")));

        var report = VerificationReport.Build(tables, spec, work.Path);

        Assert.Equal("fail", (string?)report["status"]);
        Assert.Equal("fail", StatusOf(report, "refresh-safety"));
        Assert.Contains("Hardcoded date", (string?)Check(report, "refresh-safety")!["detail"]);
    }

    [Fact]
    public void HardcodedDateInQueryOnly_IsRefreshUnsafe_Warn()
    {
        // a hardcoded date in an M query filter (not a measure) is a WARN, not a FAIL
        string spec = """
        {
          "tables": [
            {
              "name": "Sales",
              "m": "let Source = Csv.Document(File.Contents(DataFolder & \"/Sales.csv\")), F = Table.SelectRows(Source, each [OrderDate] >= #date(2024,1,1)) in F",
              "columns": [ { "name": "Amount", "dataType": "double" } ],
              "measures": [ { "name": "Total", "dax": "SUM(Sales[Amount])" } ]
            }
          ]
        }
        """;
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Amount", "10")));

        var report = VerificationReport.Build(tables, spec, work.Path);

        Assert.Equal("warn", StatusOf(report, "refresh-safety"));
    }

    [Theory]
    [InlineData("CALCULATE([Total Sales], Calendar[Period] = \"This Year\")", false)] // text "This Year" is fine
    [InlineData("SUM(Sales[Amount])", false)]
    [InlineData("CALCULATE([X], Sales[Date] >= TODAY())", false)]                     // TODAY() moves with refresh
    [InlineData("CALCULATE([X], Sales[Date] >= NOW())", false)]
    [InlineData("CALCULATE([X], Sales[Date] >= DATE(2023,12,31))", true)]             // DATE() literal
    [InlineData("CALCULATE([X], Sales[Date] >= \"2023-12-31\")", true)]               // ISO literal
    [InlineData("// pinned to today\nSUM(Sales[Amount])", true)]                      // a bare "today" marker
    public void HasHardcodedDate_FlagsAbsoluteDatesNotMovingFunctions(string dax, bool expected)
        => Assert.Equal(expected, VerificationReport.HasHardcodedDate(dax));

    // ---- calendar continuity -------------------------------------------------------------------

    [Fact]
    public void CalendarWithGap_WarnsOnContinuity()
    {
        using var work = Fixtures.NewWorkDir();
        // monthly calendar missing 2024-03-31 (Feb -> Apr jump)
        var tables = Map(("Calendar", Csv(work, "Calendar",
            "OrderDate,Period",
            "2024-01-31,P1",
            "2024-02-29,P2",
            "2024-04-30,P4",
            "2024-05-31,P5")));
        string spec = """
        { "tables": [ { "name": "Calendar", "columns": [ { "name": "OrderDate", "dataType": "date" } ] } ] }
        """;

        var report = VerificationReport.Build(tables, spec, work.Path);

        Assert.Equal("warn", StatusOf(report, "calendar-continuity"));
        Assert.Contains("gap", (string?)Check(report, "calendar-continuity")!["detail"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoCalendarTable_ContinuityCheckIsOmitted_NotInvented()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Id,Amount", "1,10", "2,20")));

        var report = VerificationReport.Build(tables, null, work.Path);

        // the engine has no calendar - the check must be absent, never fabricated
        Assert.Null(Check(report, "calendar-continuity"));
    }

    // ---- relationship key coverage -------------------------------------------------------------

    [Fact]
    public void OrphanForeignKey_WarnsOnRelationshipCoverage()
    {
        using var work = Fixtures.NewWorkDir();
        // Sales references ProductKey 3, which does not exist in Product
        var tables = Map(
            ("Sales", Csv(work, "Sales", "ProductKey,Amount", "1,10", "3,30")),
            ("Product", Csv(work, "Product", "ProductKey,Brand", "1,Acme", "2,Globex")));
        string spec = """
        {
          "tables": [
            { "name": "Sales", "columns": [ { "name": "ProductKey", "dataType": "int64" } ] },
            { "name": "Product", "columns": [ { "name": "ProductKey", "dataType": "int64" } ] }
          ],
          "relationships": [
            { "fromTable": "Sales", "fromColumn": "ProductKey", "toTable": "Product", "toColumn": "ProductKey" }
          ]
        }
        """;

        var report = VerificationReport.Build(tables, spec, work.Path);

        Assert.Equal("warn", StatusOf(report, "rel-coverage:Sales"));
        Assert.Contains("no match", (string?)Check(report, "rel-coverage:Sales")!["detail"]!);
    }

    [Fact]
    public void RelationshipColumnNotLoaded_CoverageCheckOmitted()
    {
        using var work = Fixtures.NewWorkDir();
        // only Sales is loaded; the to-side (Product) is not, so coverage cannot be judged -> omitted
        var tables = Map(("Sales", Csv(work, "Sales", "ProductKey,Amount", "1,10")));
        string spec = """
        {
          "tables": [ { "name": "Sales", "columns": [ { "name": "ProductKey", "dataType": "int64" } ] } ],
          "relationships": [
            { "fromTable": "Sales", "fromColumn": "ProductKey", "toTable": "Product", "toColumn": "ProductKey" }
          ]
        }
        """;

        var report = VerificationReport.Build(tables, spec, work.Path);

        Assert.Null(Check(report, "rel-coverage:Sales"));
    }

    // ---- primary measure resolves --------------------------------------------------------------

    [Fact]
    public void PrimaryMeasureOverEmptyColumn_Warns()
    {
        using var work = Fixtures.NewWorkDir();
        // Amount is blank in every row, so SUM(Sales[Amount]) would be blank
        var tables = Map(("Sales", Csv(work, "Sales",
            "Id,Amount",
            "1,",
            "2,")));
        string spec = """
        {
          "tables": [
            {
              "name": "Sales",
              "columns": [ { "name": "Id", "dataType": "int64" }, { "name": "Amount", "dataType": "double" } ],
              "measures": [ { "name": "Total Sales", "dax": "SUM(Sales[Amount])" } ]
            }
          ]
        }
        """;

        var report = VerificationReport.Build(tables, spec, work.Path);

        Assert.Equal("warn", StatusOf(report, "primary-measure"));
    }

    [Theory]
    [InlineData("SUM(Sales[Amount])", true, "Sales", "Amount")]
    [InlineData("AVERAGE(Sales[Price])", true, "Sales", "Price")]
    [InlineData("DIVIDE([A], [B])", false, "", "")]                       // no column ref
    [InlineData("SUM(Sales[A]) + SUM(Sales[B])", false, "", "")]          // two refs -> ambiguous
    public void TryResolveAggColumn_ExtractsSingleAggregationColumn(string dax, bool ok, string table, string col)
    {
        bool got = VerificationReport.TryResolveAggColumn(dax, out string t, out string c);
        Assert.Equal(ok, got);
        if (ok) { Assert.Equal(table, t); Assert.Equal(col, c); }
    }

    // ---- status = worst check ------------------------------------------------------------------

    [Fact]
    public void Status_IsTheWorstCheck_FailBeatsWarnBeatsPass()
    {
        // a warn (all-null column) and a fail (empty table) in one report -> the fail wins
        using var work = Fixtures.NewWorkDir();
        var tables = Map(
            ("Empty", Csv(work, "Empty", "A,B")),                              // fail
            ("Warned", Csv(work, "Warned", "A,Blank", "1,", "2,")));           // warn (Blank all-null)

        var report = VerificationReport.Build(tables, null, work.Path);

        Assert.Equal("fail", (string?)report["status"]);
        Assert.Equal("fail", StatusOf(report, "rows-loaded:Empty"));
        Assert.Equal("warn", StatusOf(report, "all-null:Warned.Blank"));
    }

    [Fact]
    public void WorstStatus_NoChecks_IsWarn_NeverSilentPass()
    {
        // nothing could be proven -> warn, never a silent pass
        Assert.Equal("warn", VerificationReport.WorstStatus(new JsonArray()));
    }

    [Fact]
    public void WorstStatus_AllPass_IsPass()
    {
        var checks = new JsonArray
        {
            new JsonObject { ["status"] = "pass" },
            new JsonObject { ["status"] = "pass" },
        };
        Assert.Equal("pass", VerificationReport.WorstStatus(checks));
    }

    // ---- no checks could be computed: honest warn summary, never "passed with 0 warning(s)" ------

    [Fact]
    public void NoChecksComputable_SummaryIsHonestNoChecksMessage_NotPassedWithZeroWarnings()
    {
        // an empty table map with no spec yields zero checks (nothing was computable). WorstStatus warns,
        // but warned == 0 - the summary must NOT read "passed with 0 warning(s)" (a warn dressed as a pass).
        using var work = Fixtures.NewWorkDir();
        var report = VerificationReport.Build(Map(), null, work.Path);

        // nothing could be proven -> status warn, never a silent pass
        Assert.Equal("warn", (string?)report["status"]);
        // and there genuinely are no checks
        Assert.Empty(Checks(report));

        string summary = (string?)report["summary"] ?? "";
        // the headline must match the status, not contradict it
        Assert.DoesNotContain("passed with 0 warning", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Verification passed", summary, StringComparison.OrdinalIgnoreCase);
        // it must say, honestly, that nothing could be proven
        Assert.Contains("could not be fully proven", summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- persistence ---------------------------------------------------------------------------

    [Fact]
    public void BuildAndSave_WritesVerificationJson_NextToTheProject()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Id,Amount", "1,10")));
        string projDir = work.File("project");

        var report = VerificationReport.BuildAndSave(tables, null, work.Path, projDir);

        string file = Path.Combine(projDir, VerificationReport.FileName);
        Assert.True(File.Exists(file), "verification.json should be written into the project dir");
        var onDisk = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal((string?)report["status"], (string?)onDisk["status"]);
        Assert.Equal("verification.json", VerificationReport.FileName);
    }

    // ---- contract shape ------------------------------------------------------------------------

    [Fact]
    public void Report_HasExactContractShape()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Id,Amount", "1,10", "2,20")));

        var report = VerificationReport.Build(tables, null, work.Path);

        // top-level: status, generatedAt, summary, tables, checks
        foreach (var key in new[] { "status", "generatedAt", "summary", "tables", "checks" })
            Assert.True(report.ContainsKey(key), $"report is missing '{key}'");

        // each table entry: name, rows, columns
        foreach (var t in (JsonArray)report["tables"]!)
        {
            var to = (JsonObject)t!;
            Assert.True(to.ContainsKey("name") && to.ContainsKey("rows") && to.ContainsKey("columns"));
        }

        // each check entry: id, label, status, detail
        foreach (var c in (JsonArray)report["checks"]!)
        {
            var co = (JsonObject)c!;
            foreach (var key in new[] { "id", "label", "status", "detail" })
                Assert.True(co.ContainsKey(key), $"check is missing '{key}'");
            Assert.Contains((string?)co["status"], new[] { "pass", "warn", "fail" });
        }
    }
}
