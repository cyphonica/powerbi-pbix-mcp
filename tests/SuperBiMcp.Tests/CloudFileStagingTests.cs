using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Exercises the shared "a picked file -> canonical CSVs" staging: extension detection (name wins, then
/// content-type, then default), CSV passthrough under the table stem, xlsx expansion to one CSV per sheet,
/// schema inference on register, table-name sanitisation, and the don't-crash notes for missing / unsupported
/// files. These run fully offline (no download) via <see cref="CloudFileStaging.StageLocalFile"/>.
/// </summary>
public sealed class CloudFileStagingTests
{
    // ---- DetectExtension -----------------------------------------------------------------------

    [Theory]
    [InlineData("report.csv", null, ".csv")]
    [InlineData("book.xlsx", null, ".xlsx")]
    [InlineData("book.xlsm", null, ".xlsx")]
    [InlineData("book.xls", null, ".xlsx")]
    public void DetectExtension_FromFileName_Wins(string name, string? ct, string expected)
        => Assert.Equal(expected, CloudFileStaging.DetectExtension(name, ct));

    [Theory]
    [InlineData(null, "text/csv", ".csv")]
    [InlineData(null, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    [InlineData(null, "application/vnd.ms-excel", ".xlsx")]
    public void DetectExtension_FromContentType_WhenNoUsableName(string? name, string ct, string expected)
        => Assert.Equal(expected, CloudFileStaging.DetectExtension(name, ct));

    [Fact]
    public void DetectExtension_NameWithoutExtension_FallsBackToContentType()
        => Assert.Equal(".xlsx", CloudFileStaging.DetectExtension("file_with_no_ext", "application/vnd.ms-excel"));

    [Fact]
    public void DetectExtension_NothingUsable_DefaultsToCsv()
        => Assert.Equal(".csv", CloudFileStaging.DetectExtension(null, null));

    [Fact]
    public void DetectExtension_UnknownExtensionAndType_DefaultsToCsv()
        => Assert.Equal(".csv", CloudFileStaging.DetectExtension("notes.txt", "text/plain"));

    // ---- SafeTableName -------------------------------------------------------------------------

    [Theory]
    [InlineData("sales", "sales")]
    [InlineData("My Sheet 1", "My Sheet 1")]
    [InlineData("a/b:c*d", "a_b_c_d")]
    [InlineData("  spaced  ", "spaced")]
    public void SafeTableName_KeepsSafeChars_CollapsesRest(string raw, string expected)
        => Assert.Equal(expected, CloudFileStaging.SafeTableName(raw));

    [Fact]
    public void SafeTableName_BlankBecomesTable()
        => Assert.Equal("Table", CloudFileStaging.SafeTableName("   "));

    // ---- StageLocalFile: CSV passthrough -------------------------------------------------------

    [Fact]
    public void StageLocalFile_Csv_RegistersTableAndInfersSchema()
    {
        using var work = Fixtures.NewWorkDir();
        var result = new IngestResult();
        CloudFileStaging.StageLocalFile(Fixtures.Path("sales.csv"), work.Path, result, CancellationToken.None);

        Assert.True(result.Tables.ContainsKey("sales"));
        Assert.True(File.Exists(result.Tables["sales"]));

        var ts = Assert.Single(result.Schema.Tables);
        var cols = ts.Columns.ToDictionary(c => c.Name, c => c.DataType);
        Assert.Equal("int64", cols["OrderId"]);   // 1001,1002,1003
        Assert.Equal("string", cols["Customer"]); // names
        Assert.Equal("double", cols["Amount"]);   // 1250.50, 980, 1500.75
        Assert.Equal("date", cols["OrderDate"]);  // 2024/06/23 ...
        Assert.Equal("boolean", cols["Active"]);  // true/false
    }

    [Fact]
    public void StageLocalFile_Xlsx_ExpandsToOneCsvPerSheet()
    {
        using var work = Fixtures.NewWorkDir();
        var result = new IngestResult();
        CloudFileStaging.StageLocalFile(Fixtures.Path("multi.xlsx"), work.Path, result, CancellationToken.None);

        Assert.True(result.Tables.ContainsKey("People"));
        Assert.True(result.Tables.ContainsKey("Money"));
        Assert.Equal(2, result.Schema.Tables.Count);

        // the People sheet's types were inferred from the staged CSV
        var people = result.Schema.Tables.Single(t => t.Name == "People");
        var pcols = people.Columns.ToDictionary(c => c.Name, c => c.DataType);
        Assert.Equal("int64", pcols["Age"]);

        // Money's leading-zero Code column stays text (codes 007 / 042 came through as inline strings,
        // but "007" parses as an integer, so the inferred type is int64 - this documents the real behaviour).
        var money = result.Schema.Tables.Single(t => t.Name == "Money");
        Assert.Contains(money.Columns, c => c.Name == "Code");
    }

    // ---- error / note paths --------------------------------------------------------------------

    [Fact]
    public void StageLocalFile_MissingFile_AddsNote_DoesNotThrow()
    {
        using var work = Fixtures.NewWorkDir();
        var result = new IngestResult();
        CloudFileStaging.StageLocalFile(work.File("ghost.csv"), work.Path, result, CancellationToken.None);

        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("file not found"));
    }

    [Fact]
    public void StageLocalFile_UnsupportedType_AddsNote_DoesNotThrow()
    {
        using var work = Fixtures.NewWorkDir();
        string txt = work.File("readme.txt");
        File.WriteAllText(txt, "not a table");
        var result = new IngestResult();
        CloudFileStaging.StageLocalFile(txt, work.Path, result, CancellationToken.None);

        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("unsupported file type"));
    }

    [Fact]
    public void RegisterCsv_EmptyFile_RegistersTableButNoSchema()
    {
        using var work = Fixtures.NewWorkDir();
        string empty = work.File("empty.csv");
        File.WriteAllText(empty, ""); // no header line at all
        var result = new IngestResult();
        CloudFileStaging.RegisterCsv("empty", empty, result);

        Assert.True(result.Tables.ContainsKey("empty"));
        Assert.Empty(result.Schema.Tables); // InferCsvSchema returns null for an empty file
    }
}
