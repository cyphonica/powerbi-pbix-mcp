using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive tests for the data-profile / data-quality report. The builder is driven over small,
/// hand-written CSV fixtures in a throwaway work dir, so every per-column statistic and every quality flag is
/// computed over real bytes - not a mock.
///
/// The profile shape is asserted against the contract:
///   { generatedAt, tables: [ { name, rows, columns: [ { name, type, nullCount, nullPct, distinctCount, min?,
///     max?, mean?, topValues: [ { value, count } ], flags: [..] } ] } ] }.
///
/// Fault-sensitivity is the whole point: the per-column counts (null count, distinct count, numeric min/max/mean)
/// are pinned to the exact arithmetic over the fixture, and EACH quality flag is proven both ways - a high-null
/// column flags AND a clean column does not; a constant column flags; an id column flags; a mixed-type column
/// flags; a fully-empty column flags. The honesty rule is proven too: numeric stats are OMITTED for a non-numeric
/// column rather than faked.
/// </summary>
public sealed class DataProfileTests
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

    private static JsonObject Table0(JsonObject profile) => (JsonObject)((JsonArray)profile["tables"]!)[0]!;

    /// <summary>The column profile object for a given column name on the first table (or null if absent).</summary>
    private static JsonObject? Col(JsonObject profile, string columnName)
        => ((JsonArray)Table0(profile)["columns"]!).OfType<JsonObject>()
            .FirstOrDefault(c => string.Equals((string?)c["name"], columnName, StringComparison.Ordinal));

    private static string[] FlagsOf(JsonObject profile, string columnName)
        => ((JsonArray)Col(profile, columnName)!["flags"]!).Select(f => (string)f!).ToArray();

    // ---- the headline: a clean dataset has accurate stats and NO quality flags -----------------

    [Fact]
    public void CleanColumns_HaveAccurateStats_AndNoFlags()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales",
            "OrderId,Region,Amount",
            "1,North,10",
            "2,South,20",
            "3,North,30",
            "4,South,40")));

        var profile = DataProfile.Build(tables, null, work.Path);

        // table-level
        Assert.Equal("Sales", (string?)Table0(profile)["name"]);
        Assert.Equal(4, (int?)Table0(profile)["rows"]);

        // Amount: numeric -> min/max/mean computed exactly, no flags
        var amount = Col(profile, "Amount")!;
        Assert.Equal("int64", (string?)amount["type"]);
        Assert.Equal(0, (int?)amount["nullCount"]);
        Assert.Equal(4, (int?)amount["distinctCount"]);
        Assert.Equal(10.0, (double?)amount["min"]);
        Assert.Equal(40.0, (double?)amount["max"]);
        Assert.Equal(25.0, (double?)amount["mean"]);   // (10+20+30+40)/4 = 25 - the load-bearing arithmetic
        Assert.Empty(FlagsOf(profile, "Amount"));

        // Region: a clean low-cardinality text column - NOT constant, NOT high-null, NOT an id, NOT mixed
        Assert.Empty(FlagsOf(profile, "Region"));
        Assert.Equal(2, (int?)Col(profile, "Region")!["distinctCount"]);   // {North, South}
    }

    // ---- high-null flag ------------------------------------------------------------------------

    [Fact]
    public void HighNullColumn_RaisesHighNullFlag_CleanColumnDoesNot()
    {
        using var work = Fixtures.NewWorkDir();
        // Notes is blank in 3 of 4 rows (75% null >= 50%) -> high-null. Amount is fully populated -> no flag.
        var tables = Map(("Sales", Csv(work, "Sales",
            "Amount,Notes",
            "10,hello",
            "20,",
            "30,",
            "40,")));

        var profile = DataProfile.Build(tables, null, work.Path);

        Assert.Contains("high-null", FlagsOf(profile, "Notes"));
        Assert.Equal(3, (int?)Col(profile, "Notes")!["nullCount"]);
        Assert.Equal(0.75, (double?)Col(profile, "Notes")!["nullPct"]);

        // the clean column must NOT raise high-null (the both-ways proof)
        Assert.DoesNotContain("high-null", FlagsOf(profile, "Amount"));
    }

    // ---- constant / single-value flag ----------------------------------------------------------

    [Fact]
    public void ConstantColumn_RaisesConstantFlag()
    {
        using var work = Fixtures.NewWorkDir();
        // Currency holds the SAME value in every row -> constant (single distinct value, >1 row)
        var tables = Map(("Sales", Csv(work, "Sales",
            "Amount,Currency",
            "10,NZD",
            "20,NZD",
            "30,NZD")));

        var profile = DataProfile.Build(tables, null, work.Path);

        Assert.Contains("constant", FlagsOf(profile, "Currency"));
        Assert.Equal(1, (int?)Col(profile, "Currency")!["distinctCount"]);
        // a column that varies must NOT be flagged constant
        Assert.DoesNotContain("constant", FlagsOf(profile, "Amount"));
    }

    // ---- all-unique id flag --------------------------------------------------------------------

    [Fact]
    public void AllUniqueIdColumn_RaisesIdFlag_NonIdUniqueColumnDoesNot()
    {
        using var work = Fixtures.NewWorkDir();
        // OrderId is named like an id AND every value is distinct -> all-unique-id.
        // Amount is also all-distinct here, but it is NOT named like an id -> must NOT be flagged (the name guard).
        var tables = Map(("Sales", Csv(work, "Sales",
            "OrderId,Amount",
            "1001,10",
            "1002,20",
            "1003,30",
            "1004,40")));

        var profile = DataProfile.Build(tables, null, work.Path);

        Assert.Contains("all-unique-id", FlagsOf(profile, "OrderId"));
        // a plain all-distinct numeric measure column is NOT an id (it lacks an id-ish name token)
        Assert.DoesNotContain("all-unique-id", FlagsOf(profile, "Amount"));
    }

    // ---- mixed-type flag -----------------------------------------------------------------------

    [Fact]
    public void MixedTypeColumn_RaisesMixedTypeFlag_AndOmitsNumericStats()
    {
        using var work = Fixtures.NewWorkDir();
        // Score mixes numbers and words -> infers as string, flagged mixed-type, and gets NO min/max/mean (honest).
        var tables = Map(("Test", Csv(work, "Test",
            "Score",
            "10",
            "20",
            "n/a",
            "30")));

        var profile = DataProfile.Build(tables, null, work.Path);

        Assert.Contains("mixed-type", FlagsOf(profile, "Score"));
        var score = Col(profile, "Score")!;
        Assert.Equal("string", (string?)score["type"]);
        // numeric stats are OMITTED for a non-numeric column - never faked
        Assert.False(score.ContainsKey("min"), "mixed-type column must not report a min");
        Assert.False(score.ContainsKey("max"), "mixed-type column must not report a max");
        Assert.False(score.ContainsKey("mean"), "mixed-type column must not report a mean");

        // a clean numeric column is NOT mixed-type
        var tables2 = Map(("T2", Csv(work, "T2", "Clean", "1", "2", "3")));
        var p2 = DataProfile.Build(tables2, null, work.Path);
        Assert.DoesNotContain("mixed-type", FlagsOf(p2, "Clean"));
    }

    // ---- fully-empty flag ----------------------------------------------------------------------

    [Fact]
    public void FullyEmptyColumn_RaisesFullyEmptyFlag_NotHighNull()
    {
        using var work = Fixtures.NewWorkDir();
        // Spare is blank in EVERY row -> fully-empty (the stronger flag), and NOT additionally high-null.
        var tables = Map(("Sales", Csv(work, "Sales",
            "Amount,Spare",
            "10,",
            "20,",
            "30,")));

        var profile = DataProfile.Build(tables, null, work.Path);

        var flags = FlagsOf(profile, "Spare");
        Assert.Contains("fully-empty", flags);
        Assert.DoesNotContain("high-null", flags);   // fully-empty supersedes high-null
        Assert.Equal(3, (int?)Col(profile, "Spare")!["nullCount"]);
        Assert.Equal(0, (int?)Col(profile, "Spare")!["distinctCount"]);
    }

    // ---- top values are counted exactly --------------------------------------------------------

    [Fact]
    public void TopValues_AreCountedExactly_HighestFirst()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales",
            "Region",
            "North",
            "North",
            "North",
            "South",
            "South",
            "East")));

        var profile = DataProfile.Build(tables, null, work.Path);

        var top = (JsonArray)Col(profile, "Region")!["topValues"]!;
        var first = (JsonObject)top[0]!;
        Assert.Equal("North", (string?)first["value"]);
        Assert.Equal(3, (int?)first["count"]);          // North appears 3 times - the most common
        var second = (JsonObject)top[1]!;
        Assert.Equal("South", (string?)second["value"]);
        Assert.Equal(2, (int?)second["count"]);
    }

    // ---- direct unit tests of the pure flag + id decision (fault-sensitivity sentinels) --------

    [Theory]
    // columnName, type, rows, nonNull, nulls, distinct, mixed  ->  expected single flag (or none)
    [InlineData("Notes", "string", 4, 1, 3, 1, false, "high-null")]
    [InlineData("Currency", "string", 3, 3, 0, 1, false, "constant")]
    [InlineData("Spare", "string", 3, 0, 3, 0, false, "fully-empty")]
    [InlineData("Amount", "int64", 4, 4, 0, 4, false, "")]        // clean numeric -> no flag
    public void Flags_PureDecision_RaisesExactlyTheExpectedFlag(
        string col, string type, int rows, int nonNull, int nulls, int distinct, bool mixed, string expected)
    {
        var flags = DataProfile.Flags(col, type, rows, nonNull, nulls, distinct, mixed)
            .Select(f => (string)f!).ToArray();
        if (expected.Length == 0) Assert.Empty(flags);
        else Assert.Contains(expected, flags);
    }

    [Theory]
    [InlineData("OrderId", "int64", true)]
    [InlineData("customer_id", "int64", true)]
    [InlineData("ProductKey", "int64", true)]
    [InlineData("SKU", "string", true)]
    [InlineData("Amount", "double", false)]    // a measure column is not an id even when all-distinct
    [InlineData("Region", "string", false)]
    public void LooksLikeId_RequiresAnIdNameToken(string col, string type, bool expected)
        => Assert.Equal(expected, DataProfile.LooksLikeId(col, type));

    // ---- contract shape + persistence ----------------------------------------------------------

    [Fact]
    public void Profile_HasExactContractShape()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Id,Amount", "1,10", "2,20")));

        var profile = DataProfile.Build(tables, null, work.Path);

        foreach (var key in new[] { "generatedAt", "tables" })
            Assert.True(profile.ContainsKey(key), $"profile is missing '{key}'");
        Assert.NotNull((string?)profile["generatedAt"]);

        var t0 = Table0(profile);
        foreach (var key in new[] { "name", "rows", "columns" })
            Assert.True(t0.ContainsKey(key), $"table is missing '{key}'");

        foreach (var c in (JsonArray)t0["columns"]!)
        {
            var co = (JsonObject)c!;
            foreach (var key in new[] { "name", "type", "nullCount", "nullPct", "distinctCount", "topValues", "flags" })
                Assert.True(co.ContainsKey(key), $"column is missing '{key}'");
        }
    }

    [Fact]
    public void BuildAndSave_WritesProfileJson_NextToTheProject()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Id,Amount", "1,10")));
        string projDir = work.File("project");

        var profile = DataProfile.BuildAndSave(tables, null, work.Path, projDir);

        string file = Path.Combine(projDir, DataProfile.FileName);
        Assert.True(File.Exists(file), "profile.json should be written into the project dir");
        Assert.Equal("profile.json", DataProfile.FileName);
        var onDisk = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal((string?)profile["generatedAt"], (string?)onDisk["generatedAt"]);
    }

    // ---- a read failure becomes a table entry, never an exception ------------------------------

    [Fact]
    public void MissingFile_ProducesReadErrorEntry_NeverThrows()
    {
        var tables = Map(("Ghost", Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".csv")));

        var profile = DataProfile.Build(tables, null, null);

        var t0 = Table0(profile);
        Assert.Equal("Ghost", (string?)t0["name"]);
        Assert.NotNull((string?)t0["readError"]);
        Assert.Empty((JsonArray)t0["columns"]!);
    }
}
