using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive tests for ask-your-data. The LLM is the ONLY non-deterministic part, so it is replaced by an
/// injected stub decider (mirroring the SqlConnector.UseDialectForTests seam) - NO network call happens. Every
/// other part is the REAL engine code driven over CSV fixtures:
///   - context assembly: schema + metrics + a CAPPED sample, with NO secrets/tokens leaking in;
///   - computation execution: a known question -> a known computed evidence via the stubbed decision;
///   - result formatting: the { question, answer, evidence, usedQuery, grounded } contract;
///   - the honest grounded:false path when the data cannot answer it.
///
/// Fault-sensitivity: the asserted evidence VALUES (a filtered SUM, a distinct count, a grouped top-N) are pinned
/// to the exact arithmetic over the fixture, so a wrong aggregate or a wrong filter fails the test. The honesty
/// rule is proven: an unanswerable question, or a computation over a missing column, returns grounded:false with
/// an empty evidence array - never a fabricated number.
/// </summary>
public sealed class AskYourDataTests
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

    /// <summary>A stub decider that returns a fixed decision (the test seam: no LLM, no network).</summary>
    private static AskYourData.Decider Stub(AskYourData.AskDecision decision)
        => (ctx, q, ct) => Task.FromResult(decision);

    // a Sales table: 5 rows, two regions, an amount and an order id.
    private static (TempDir work, Dictionary<string, string> tables) SalesFixture()
    {
        var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales",
            "OrderId,Region,Amount",
            "1,North,100",
            "2,North,50",
            "3,South,30",
            "4,South,20",
            "5,North,25")));
        // North: 100+50+25 = 175 ; South: 30+20 = 50 ; total = 225 ; distinct OrderId = 5
        return (work, tables);
    }

    // ---- the headline: a filtered SUM is computed exactly over the fixture ----------------------

    [Fact]
    public async Task FilteredAggregate_ComputesExactSum_AndReturnsTheRowsItUsed()
    {
        var (work, tables) = SalesFixture();
        using (work)
        {
            // "total amount in the North region" -> SUM(Amount) WHERE Region = North
            var decision = new AskYourData.AskDecision
            {
                Op = AskYourData.OpAggregate, Table = "Sales", Column = "Amount", Aggregation = "SUM",
                FilterColumn = "Region", FilterValue = "North",
            };

            var result = await AskYourData.AskAsync(tables, null, "total amount in the North region", Stub(decision));

            Assert.True((bool?)result["grounded"]);
            // SUM over North rows = 100 + 50 + 25 = 175 (the load-bearing arithmetic; a wrong filter or agg differs).
            // Assert the EXACT "= 175." token so a sign flip or off-by-one (e.g. -175, 174) fails - a bare "175"
            // substring would be satisfied by "-175".
            Assert.Contains("= 175.", (string?)result["answer"]);
            Assert.DoesNotContain("-175", (string?)result["answer"]);
            Assert.Contains("WHERE Region = 'North'", (string?)result["usedQuery"]);

            // evidence is exactly the 3 North rows it used (not all 5)
            var evidence = (JsonArray)result["evidence"]!;
            Assert.Equal(3, evidence.Count);
            Assert.All(evidence, e => Assert.Equal("North", (string?)((JsonObject)e!)["Region"]));
        }
    }

    [Fact]
    public async Task DistinctCount_IsDistinctNotRowCount()
    {
        using var work = Fixtures.NewWorkDir();
        // 4 rows, 2 distinct CustomerId -> DISTINCTCOUNT must be 2, proving it is distinct, not a row count.
        var tables = Map(("Orders", Csv(work, "Orders",
            "CustomerId,Amount",
            "100,1",
            "100,2",
            "200,3",
            "200,4")));
        var decision = new AskYourData.AskDecision
        {
            Op = AskYourData.OpAggregate, Table = "Orders", Column = "CustomerId", Aggregation = "DISTINCTCOUNT",
        };

        var result = await AskYourData.AskAsync(tables, null, "how many customers", Stub(decision));

        Assert.True((bool?)result["grounded"]);
        Assert.Contains("2", (string?)result["answer"]);          // distinct {100,200} = 2, not 4
        Assert.Contains("DISTINCTCOUNT", (string?)result["usedQuery"]);
    }

    [Fact]
    public async Task GroupTopN_RollsUpAndOrders_HighestFirst()
    {
        var (work, tables) = SalesFixture();
        using (work)
        {
            // "top regions by total amount" -> SUM(Amount) grouped by Region, top 5
            var decision = new AskYourData.AskDecision
            {
                Op = AskYourData.OpGroupTopN, Table = "Sales", Column = "Amount", GroupBy = "Region",
                Aggregation = "SUM", TopN = 5,
            };

            var result = await AskYourData.AskAsync(tables, null, "top regions by amount", Stub(decision));

            Assert.True((bool?)result["grounded"]);
            var groups = (JsonArray)result["evidence"]!;
            // North (175) must come before South (50) - the ordering + the rollup arithmetic
            var first = (JsonObject)groups[0]!;
            Assert.Equal("North", (string?)first["group"]);
            Assert.Equal(175.0, (double?)first["value"]);
            var second = (JsonObject)groups[1]!;
            Assert.Equal("South", (string?)second["group"]);
            Assert.Equal(50.0, (double?)second["value"]);
        }
    }

    // ---- the cross-boundary evidence SHAPE the cloud parses (row objects keyed by column name) --

    [Fact]
    public async Task Evidence_IsAnArrayOfRowObjects_KeyedByEveryColumnName_WithStringValues()
    {
        var (work, tables) = SalesFixture();
        using (work)
        {
            // a filtered SUM -> the evidence is the rows it used, EACH a JSON object keyed by column name.
            // This is the EXACT shape the cloud's Ask::normalise_evidence keeps verbatim, so it is pinned here.
            var decision = new AskYourData.AskDecision
            {
                Op = AskYourData.OpAggregate, Table = "Sales", Column = "Amount", Aggregation = "SUM",
                FilterColumn = "Region", FilterValue = "North",
            };

            var result = await AskYourData.AskAsync(tables, null, "north total", Stub(decision));

            var evidence = (JsonArray)result["evidence"]!;
            Assert.Equal(3, evidence.Count);
            foreach (var e in evidence)
            {
                var row = Assert.IsType<JsonObject>(e);
                // keys are EXACTLY the column names, values are the raw cell strings
                foreach (var col in new[] { "OrderId", "Region", "Amount" })
                {
                    Assert.True(row.ContainsKey(col), $"evidence row is missing column '{col}'");
                    Assert.IsAssignableFrom<JsonValue>(row[col]!);   // a scalar string value, not a nested object
                }
                Assert.Equal("North", (string?)row["Region"]);
            }
        }
    }

    // ---- honesty: the grounded:false paths -----------------------------------------------------

    [Fact]
    public async Task UnanswerableDecision_ReturnsGroundedFalse_WithEmptyEvidence_NeverANumber()
    {
        var (work, tables) = SalesFixture();
        using (work)
        {
            // the model says the data cannot answer it
            var decision = new AskYourData.AskDecision
            {
                Op = AskYourData.OpUnanswerable, Reason = "there is no profit column in this data",
            };

            var result = await AskYourData.AskAsync(tables, null, "what was our profit margin", Stub(decision));

            Assert.False((bool?)result["grounded"]);
            Assert.Empty((JsonArray)result["evidence"]!);     // used nothing
            Assert.Null(result["usedQuery"]);                 // computed nothing
            Assert.Contains("cannot answer", (string?)result["answer"], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AggregateOverMissingColumn_ReturnsGroundedFalse_NotZero()
    {
        var (work, tables) = SalesFixture();
        using (work)
        {
            // the decision references a column that does not exist -> cannot be computed honestly
            var decision = new AskYourData.AskDecision
            {
                Op = AskYourData.OpAggregate, Table = "Sales", Column = "Profit", Aggregation = "SUM",
            };

            var result = await AskYourData.AskAsync(tables, null, "total profit", Stub(decision));

            Assert.False((bool?)result["grounded"]);          // omitted, NOT reported as 0
            Assert.Empty((JsonArray)result["evidence"]!);
            Assert.Contains("Profit", (string?)result["answer"]);
        }
    }

    [Fact]
    public async Task NumericAggregateOverNonNumericColumn_ReturnsGroundedFalse_NotZero()
    {
        var (work, tables) = SalesFixture();
        using (work)
        {
            // SUM over the text Region column has no numeric values -> grounded:false, never 0
            var decision = new AskYourData.AskDecision
            {
                Op = AskYourData.OpAggregate, Table = "Sales", Column = "Region", Aggregation = "SUM",
            };

            var result = await AskYourData.AskAsync(tables, null, "sum of region", Stub(decision));

            Assert.False((bool?)result["grounded"]);
        }
    }

    // ---- context assembly: schema + sample within caps, NO secrets -----------------------------

    [Fact]
    public void BuildContext_CarriesSchemaAndCappedSample_NeverTheWholeTable()
    {
        using var work = Fixtures.NewWorkDir();
        // 20 rows - the sample placed in the context must be capped well below this.
        var lines = new List<string> { "Id,Value" };
        for (int i = 1; i <= 20; i++) lines.Add($"{i},{i * 10}");
        var tables = Map(("Big", Csv(work, "Big", lines.ToArray())));

        var ctx = AskYourData.BuildContext(tables, null);

        // schema present with column names + inferred types
        var t0 = (JsonObject)ctx.Schema[0]!;
        Assert.Equal("Big", (string?)t0["name"]);
        var cols = (JsonArray)t0["columns"]!;
        Assert.Equal(2, cols.Count);
        Assert.Equal("Id", (string?)((JsonObject)cols[0]!)["name"]);

        // the SAMPLE rows are capped (the model never sees the whole table)
        var sample = (JsonArray)t0["sampleRows"]!;
        Assert.True(sample.Count <= AskYourData.MaxSampleRowsPerTable,
            $"sample {sample.Count} exceeds cap {AskYourData.MaxSampleRowsPerTable}");

        // but the FULL rows are loaded for the deterministic computation (20 rows)
        Assert.Single(ctx.Tables);
        Assert.Equal(20, ctx.Tables[0].Rows.Count);
    }

    [Fact]
    public void BuildContext_PromptText_ContainsNoSecretsOrTokens()
    {
        using var work = Fixtures.NewWorkDir();
        var tables = Map(("Sales", Csv(work, "Sales", "Region,Amount", "North,100", "South,50")));
        // a metrics snapshot grounds the context with headline numbers
        var metrics = MetricsSnapshot.Build(tables, null, work.Path);

        var ctx = AskYourData.BuildContext(tables, metrics);
        string prompt = ctx.ToPromptText();

        // the prompt carries the schema + headline numbers ...
        Assert.Contains("Sales", prompt);
        Assert.Contains("Region", prompt);
        // ... and nothing that looks like a secret/token/key (the access token never reaches this layer)
        foreach (var banned in new[] { "token", "api_key", "apikey", "x-connector-token", "secret", "password", "bearer" })
            Assert.DoesNotContain(banned, prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---- result formatting + the AskAsync guards -----------------------------------------------

    [Fact]
    public async Task EmptyQuestion_ReturnsGroundedFalse_WithoutCallingTheDecider()
    {
        var (work, tables) = SalesFixture();
        using (work)
        {
            bool deciderCalled = false;
            AskYourData.Decider spy = (c, q, ct) => { deciderCalled = true; return Task.FromResult(new AskYourData.AskDecision()); };

            var result = await AskYourData.AskAsync(tables, null, "   ", spy);

            Assert.False((bool?)result["grounded"]);
            Assert.False(deciderCalled, "an empty question must short-circuit before the LLM is asked");
        }
    }

    [Fact]
    public void Answer_HasExactContractShape()
    {
        var decision = new AskYourData.AskDecision
        {
            Op = AskYourData.OpAggregate, Table = "Sales", Column = "Amount", Aggregation = "SUM",
        };
        var evidence = new JsonArray { new JsonObject { ["Amount"] = "100" } };
        var answer = AskYourData.Answer("q", decision, JsonValue.Create(100.0), evidence, "SUM(Sales[Amount])");

        foreach (var key in new[] { "question", "answer", "evidence", "usedQuery", "grounded" })
            Assert.True(answer.ContainsKey(key), $"answer is missing '{key}'");
        Assert.True((bool?)answer["grounded"]);
        Assert.Equal("q", (string?)answer["question"]);
    }

    // ---- the production decider's JSON extraction (no network - just the parser) ----------------

    [Theory]
    [InlineData("{\"op\":\"count-rows\",\"table\":\"Sales\"}", "count-rows", "Sales")]
    [InlineData("Here is the decision:\n```json\n{\"op\":\"aggregate\",\"table\":\"Orders\"}\n```\nthanks", "aggregate", "Orders")]
    [InlineData("no json here", "unanswerable", "")]
    public void ExtractJson_ThenFromJson_ParsesTheDecisionFromAModelReply(string reply, string expectedOp, string expectedTable)
    {
        var decision = AskYourData.AskDecision.FromJson(AskYourData.ExtractJson(reply));
        Assert.Equal(expectedOp, decision.Op);
        Assert.Equal(expectedTable, decision.Table);
    }

    // ---- direct execution test over a real context (drives the real internals) -----------------

    [Fact]
    public void Execute_CountRows_WithFilter_CountsExactlyTheMatchingRows()
    {
        var (work, tables) = SalesFixture();
        using (work)
        {
            var ctx = AskYourData.BuildContext(tables, null);
            var decision = new AskYourData.AskDecision
            {
                Op = AskYourData.OpCount, Table = "Sales", FilterColumn = "Region", FilterValue = "South",
            };

            var (value, evidence, usedQuery, error) = AskYourData.Execute(ctx, decision);

            Assert.Null(error);
            Assert.Equal(2L, (long?)value);               // 2 South rows (OrderId 3 and 4)
            Assert.Equal(2, evidence!.Count);
            Assert.Contains("COUNT", usedQuery);
        }
    }
}
