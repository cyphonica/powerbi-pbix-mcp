using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Streams the committed multi-sheet xlsx fixture to CSV through the real <see cref="ExcelService"/> and
/// asserts the canonical shape: sheet resolved by name, shared strings and inline strings resolved,
/// numbers passed through, a leading-zero inline string kept as TEXT (not coerced to a number), and the
/// missing-sheet / missing-file error paths.
/// </summary>
public sealed class ExcelServiceTests
{
    private static ExcelService NewService() => new(NullLogger<ExcelService>.Instance);

    [Fact]
    public void ListSheets_ReturnsBothSheetsInOrder()
    {
        var svc = NewService();
        object result = svc.ListSheets(Fixtures.Path("multi.xlsx"));
        // shape is { ok, xlsxPath, sheets:[{name, sheetId}] }; read names via JSON to avoid the anon type.
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(result));
        var names = (node!["sheets"] as System.Text.Json.Nodes.JsonArray)!
            .Select(n => (string?)n!["name"]).ToList();
        Assert.Equal(new[] { "People", "Money" }, names);
    }

    [Fact]
    public void XlsxToCsv_PeopleSheet_ResolvesSharedAndInlineStrings()
    {
        using var tmp = Fixtures.NewWorkDir();
        var svc = NewService();
        string outCsv = tmp.File("People.csv");
        svc.XlsxToCsv(Fixtures.Path("multi.xlsx"), "People", outCsv);

        string[] lines = Lines(outCsv);
        Assert.Equal("Name,City,Age", lines[0]);          // shared (Name,City) + inline (Age)
        Assert.Equal("Alice,Auckland,30", lines[1]);      // shared strings + number
        Assert.Equal("Bob,Wellington,41", lines[2]);      // inline strings + number
    }

    [Fact]
    public void XlsxToCsv_MoneySheet_KeepsLeadingZeroCodeAsText()
    {
        using var tmp = Fixtures.NewWorkDir();
        var svc = NewService();
        string outCsv = tmp.File("Money.csv");
        svc.XlsxToCsv(Fixtures.Path("multi.xlsx"), "Money", outCsv);

        string[] lines = Lines(outCsv);
        Assert.Equal("Code,Amount", lines[0]);
        // "007" was written as an inline string in the workbook, so it MUST stay "007", not "7".
        Assert.Equal("007,1250.5", lines[1]);
        Assert.Equal("042,980", lines[2]);
    }

    [Fact]
    public void XlsxToCsv_DefaultsToFirstSheet_WhenSheetNameNull()
    {
        using var tmp = Fixtures.NewWorkDir();
        var svc = NewService();
        string outCsv = tmp.File("first.csv");
        // null sheet name => first sheet ("People")
        svc.XlsxToCsv(Fixtures.Path("multi.xlsx"), null, outCsv);
        Assert.Equal("Name,City,Age", Lines(outCsv)[0]);
    }

    [Fact]
    public void XlsxToCsv_FileWrittenWithoutBom()
    {
        using var tmp = Fixtures.NewWorkDir();
        var svc = NewService();
        string outCsv = tmp.File("nobom.csv");
        svc.XlsxToCsv(Fixtures.Path("multi.xlsx"), "People", outCsv);
        byte[] bytes = File.ReadAllBytes(outCsv);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    // ---- error paths ---------------------------------------------------------------------------

    [Fact]
    public void XlsxToCsv_MissingSheet_Throws()
    {
        using var tmp = Fixtures.NewWorkDir();
        var svc = NewService();
        var ex = Assert.Throws<InvalidOperationException>(
            () => svc.XlsxToCsv(Fixtures.Path("multi.xlsx"), "NoSuchSheet", tmp.File("x.csv")));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void XlsxToCsv_MissingFile_ThrowsFileNotFound()
    {
        using var tmp = Fixtures.NewWorkDir();
        var svc = NewService();
        Assert.Throws<FileNotFoundException>(
            () => svc.XlsxToCsv(tmp.File("does-not-exist.xlsx"), "People", tmp.File("x.csv")));
    }

    private static string[] Lines(string path)
        => File.ReadAllText(path, Encoding.UTF8)
               .Split("\r\n", StringSplitOptions.None)
               .Where(l => l.Length > 0).ToArray();
}
