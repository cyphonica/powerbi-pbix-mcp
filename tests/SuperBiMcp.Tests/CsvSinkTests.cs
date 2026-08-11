using System.Text;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Exercises the canonical CSV writer: RFC-4180 field quoting, short-row padding, long-row truncation,
/// the header-not-counted row tally, and the UTF-8 (no BOM) + CRLF byte contract the report pipeline's
/// Csv.Document M relies on.
/// </summary>
public sealed class CsvSinkTests
{
    // ---- Csv() field quoting (static, no file) -------------------------------------------------

    [Fact]
    public void Csv_PlainValue_IsUnquoted()
        => Assert.Equal("hello", CsvSink.Csv("hello"));

    [Fact]
    public void Csv_NullAndEmpty_BecomeEmptyString()
    {
        Assert.Equal("", CsvSink.Csv(null));
        Assert.Equal("", CsvSink.Csv(""));
    }

    [Fact]
    public void Csv_ValueWithComma_IsQuoted()
        => Assert.Equal("\"a,b\"", CsvSink.Csv("a,b"));

    [Fact]
    public void Csv_EmbeddedQuote_IsDoubledAndWrapped()
        => Assert.Equal("\"a\"\"b\"", CsvSink.Csv("a\"b"));

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\nline2")]
    [InlineData("trailing\r")]
    public void Csv_ValueWithNewline_IsQuoted(string input)
    {
        string got = CsvSink.Csv(input);
        Assert.StartsWith("\"", got);
        Assert.EndsWith("\"", got);
    }

    [Fact]
    public void Csv_LeadingZeroString_IsNotMangled()
        => Assert.Equal("007", CsvSink.Csv("007")); // the sink never re-types; padding is a later concern

    // ---- row padding / truncation --------------------------------------------------------------

    [Fact]
    public void WriteRow_ShortRow_IsPaddedToHeaderWidth()
    {
        using var tmp = Fixtures.NewWorkDir();
        string path = tmp.File("pad.csv");
        using (var sink = new CsvSink(path, new[] { "A", "B", "C" }))
            sink.WriteRow(new[] { "1" });               // only one field for a 3-col header

        string[] lines = ReadDataLines(path);
        Assert.Equal("1,,", lines[0]);                  // padded with two empty fields
    }

    [Fact]
    public void WriteRow_LongRow_IsTruncatedToHeaderWidth()
    {
        using var tmp = Fixtures.NewWorkDir();
        string path = tmp.File("trunc.csv");
        using (var sink = new CsvSink(path, new[] { "A", "B" }))
            sink.WriteRow(new[] { "1", "2", "3", "4" }); // four fields for a 2-col header

        string[] lines = ReadDataLines(path);
        Assert.Equal("1,2", lines[0]);                  // extra fields dropped
    }

    [Fact]
    public void WriteRow_QuotesFieldsThatNeedIt()
    {
        using var tmp = Fixtures.NewWorkDir();
        string path = tmp.File("quote.csv");
        using (var sink = new CsvSink(path, new[] { "Name", "Note" }))
            sink.WriteRow(new[] { "Smith, John", "he said \"hi\"" });

        string[] lines = ReadDataLines(path);
        Assert.Equal("\"Smith, John\",\"he said \"\"hi\"\"\"", lines[0]);
    }

    // ---- RowsWritten tally ---------------------------------------------------------------------

    [Fact]
    public void RowsWritten_CountsDataRowsOnly_NotHeader()
    {
        using var tmp = Fixtures.NewWorkDir();
        string path = tmp.File("count.csv");
        using var sink = new CsvSink(path, new[] { "A" });
        Assert.Equal(0, sink.RowsWritten);              // header alone => 0 data rows
        sink.WriteRow(new[] { "x" });
        sink.WriteRow(new[] { "y" });
        Assert.Equal(2, sink.RowsWritten);
    }

    // ---- byte-level contract: UTF-8 no BOM, CRLF, header first ---------------------------------

    [Fact]
    public void File_HasNoBom_AndUsesCrlf()
    {
        using var tmp = Fixtures.NewWorkDir();
        string path = tmp.File("bytes.csv");
        using (var sink = new CsvSink(path, new[] { "H1", "H2" }))
            sink.WriteRow(new[] { "v1", "v2" });

        byte[] bytes = File.ReadAllBytes(path);
        // no UTF-8 BOM (EF BB BF)
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        string text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("H1,H2\r\n", text);           // header first, CRLF terminated
        Assert.Contains("v1,v2\r\n", text);
        Assert.DoesNotContain("\n\n", text);
    }

    [Fact]
    public void File_RoundTripsUnicode()
    {
        using var tmp = Fixtures.NewWorkDir();
        string path = tmp.File("unicode.csv");
        using (var sink = new CsvSink(path, new[] { "City" }))
            sink.WriteRow(new[] { "Whanganui — éà" }); // an em dash + accents in DATA is fine to round-trip

        string text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("Whanganui", text);
        Assert.Contains("éà", text);
    }

    // a CSV produced here has the header on the first line; return the data lines after it.
    private static string[] ReadDataLines(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        // [0] header, trailing element after the final CRLF is empty
        return lines.Skip(1).Where(l => l.Length > 0).ToArray();
    }
}
