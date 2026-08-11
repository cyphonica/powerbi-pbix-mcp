using System.Text;

namespace SuperBiMcp.Integrations;

/// <summary>
/// Writes rows to a CSV in the exact shape the report pipeline consumes: UTF-8 (no BOM), CRLF line
/// endings, RFC-4180 quoting, a header row first. The generated model.spec M reads these with
/// <c>Csv.Document(File.Contents(...), [Delimiter=",", Encoding=65001, QuoteStyle=QuoteStyle.Csv])</c> then
/// <c>Table.PromoteHeaders</c>, so the contract is "a comma-delimited UTF-8 file whose first row is the
/// header." The quoting rule matches <see cref="SuperBiMcp.Services.ExcelService"/> exactly so a CSV from a
/// connector is indistinguishable from one staged from Excel.
///
/// Every connector writes its tables through this sink so the canonical shape is produced in one place.
/// </summary>
public sealed class CsvSink : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly int _columns;
    private bool _headerWritten;

    /// <summary>Open a CSV sink at <paramref name="path"/> and immediately write the header row.</summary>
    public CsvSink(string path, IReadOnlyList<string> header)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20),
            new UTF8Encoding(false), 1 << 20);
        _columns = header.Count;
        WriteRow(header);
        _headerWritten = true;
        RowsWritten = 0;
    }

    /// <summary>Number of DATA rows written (the header is not counted).</summary>
    public long RowsWritten { get; private set; }

    /// <summary>Write one data row. Fields are emitted in column order; short rows are padded, long rows
    /// truncated, so every line has the same field count as the header.</summary>
    public void WriteRow(IReadOnlyList<string?> fields)
    {
        for (int i = 0; i < _columns; i++)
        {
            if (i > 0) _writer.Write(',');
            _writer.Write(Csv(i < fields.Count ? fields[i] : null));
        }
        _writer.Write("\r\n");
        if (_headerWritten) RowsWritten++;
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>RFC-4180 field quoting, identical to ExcelService.Csv: empty stays empty; a field
    /// containing a comma, quote, CR or LF is wrapped in quotes with embedded quotes doubled.</summary>
    public static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
