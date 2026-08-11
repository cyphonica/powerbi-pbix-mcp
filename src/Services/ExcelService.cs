using System.IO.Compression;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace SuperBiMcp.Services;

/// <summary>
/// Streams a large .xlsx worksheet straight to a CSV on disk WITHOUT loading the
/// workbook into memory. Power Query's Excel.Workbook() parses the whole workbook
/// into RAM and crashes the mashup container on big files (tens of MB / 100k+ rows);
/// a CSV produced here loads through Csv.Document() which streams and never crashes.
/// Only the shared-strings table is held in memory; the sheet itself is read with a
/// forward-only XmlReader and written row-by-row.
/// </summary>
public sealed class ExcelService
{
    private readonly ILogger<ExcelService> _log;
    public ExcelService(ILogger<ExcelService> log) => _log = log;

    public object XlsxToCsv(string xlsxPath, string? sheetName, string? outCsvPath)
    {
        if (!File.Exists(xlsxPath)) throw new FileNotFoundException($"xlsx not found: {xlsxPath}");
        outCsvPath ??= Path.ChangeExtension(xlsxPath, ".csv");

        using var zip = ZipFile.OpenRead(xlsxPath);

        // 1. resolve the worksheet part path from its name
        string sheetPart = ResolveSheetPart(zip, sheetName, out string resolvedName);

        // 2. shared strings into memory (unique strings only - bounded)
        var shared = LoadSharedStrings(zip);

        // 3. stream the sheet to CSV
        var sheetEntry = zip.GetEntry(sheetPart) ?? throw new InvalidOperationException($"worksheet part '{sheetPart}' missing.");
        long rows = 0; int maxCols = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outCsvPath))!);

        using (var outStream = new FileStream(outCsvPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var writer = new StreamWriter(outStream, new UTF8Encoding(false), 1 << 20))
        using (var sheetStream = sheetEntry.Open())
        using (var reader = XmlReader.Create(sheetStream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true }))
        {
            string?[] row = new string?[16];
            int curCol = -1; string? cellType = null;
            bool inRow = false;

            bool advanced = reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "dimension":
                        {
                            var r = reader.GetAttribute("ref");
                            int n = ColsFromDimension(r);
                            if (n > maxCols) { maxCols = n; }
                            advanced = reader.Read();
                            continue;
                        }
                        case "row":
                            inRow = true;
                            if (maxCols > row.Length) row = new string?[maxCols];
                            Array.Clear(row, 0, row.Length);
                            advanced = reader.Read();
                            continue;
                        case "c":
                        {
                            curCol = ColIndex(reader.GetAttribute("r"));
                            cellType = reader.GetAttribute("t");
                            if (curCol + 1 > maxCols) maxCols = curCol + 1;
                            if (curCol >= row.Length) Grow(ref row, curCol + 1);
                            advanced = reader.Read();
                            continue;
                        }
                        case "v":
                        {
                            string v = reader.ReadElementContentAsString();   // advances past </v>
                            Place(row, curCol, Resolve(v, cellType, shared));
                            advanced = true;   // do NOT Read() again - reader already moved
                            continue;
                        }
                        case "t" when cellType == "inlineStr" || cellType == "str":
                        {
                            string v = reader.ReadElementContentAsString();
                            Place(row, curCol, v);
                            advanced = true;
                            continue;
                        }
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && inRow)
                {
                    WriteRow(writer, row, Math.Max(maxCols, 1));
                    rows++;
                    inRow = false;
                }

                advanced = reader.Read();
                _ = advanced;
            }
        }

        var info = new FileInfo(outCsvPath);
        return new
        {
            ok = true,
            sheet = resolvedName,
            rows,
            columns = maxCols,
            outCsvPath,
            bytes = info.Length,
            note = "Load this CSV with Csv.Document() (Columns=" + maxCols + ") instead of Excel.Workbook().",
        };
    }

    /// <summary>
    /// Stream-unpivots a WIDE CSV (one column per week, e.g. "23/06/2024_SALES",
    /// "23/06/2024_VOLUME") into a TALL CSV (keys + WeekKey + one column per measure),
    /// processing one input row at a time. This moves the unpivot OUT of Power Query -
    /// the in-mashup Table.UnpivotOtherColumns on a 300MB+ file runs the mashup container
    /// out of memory ("Evaluation ran out of memory"); the tall CSV then loads through
    /// Csv.Document() with no transform. Optional 2-pass scope filter (keep only rows whose
    /// scopeColumn value appears for a given filterColumn=filterValue), matching the common
    /// "categories the target supplier sells in" pattern. Empty weeks (all measures blank)
    /// are skipped, which shrinks the output dramatically.
    /// </summary>
    public object UnpivotWeeklyCsv(string inCsv, string? outCsv, string keyColumns, string measures,
        string? filterColumn, string? filterValue, string? scopeColumn)
    {
        if (!File.Exists(inCsv)) throw new FileNotFoundException($"csv not found: {inCsv}");
        outCsv ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inCsv))!,
            Path.GetFileNameWithoutExtension(inCsv) + " (long).csv");

        // measures: "_SALES:Sales,_VOLUME:Volume"
        var meas = measures.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => { var p = m.Split(':', 2); return (suffix: p[0], outName: p.Length > 1 ? p[1] : p[0].TrimStart('_')); })
            .ToArray();
        var keys = keyColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string[] header = SplitCsvLine(File.ReadLines(inCsv).First());
        int Idx(string name) => Array.FindIndex(header, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        int[] keyIdx = keys.Select(Idx).ToArray();
        for (int i = 0; i < keys.Length; i++) if (keyIdx[i] < 0) throw new InvalidOperationException($"key column '{keys[i]}' not found.");
        int filterIdx = filterColumn != null ? Idx(filterColumn) : -1;
        int scopeIdx = scopeColumn != null ? Idx(scopeColumn) : -1;

        // week -> per-measure column index
        var weekCols = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        for (int c = 0; c < header.Length; c++)
            for (int m = 0; m < meas.Length; m++)
                if (header[c].EndsWith(meas[m].suffix, StringComparison.OrdinalIgnoreCase))
                {
                    string week = header[c][..^meas[m].suffix.Length];
                    if (!weekCols.TryGetValue(week, out var arr)) { arr = new int[meas.Length]; Array.Fill(arr, -1); weekCols[week] = arr; }
                    arr[m] = c;
                }
        if (weekCols.Count == 0) throw new InvalidOperationException($"no columns matched the measure suffixes ({measures}).");

        // pass 1: scope set
        HashSet<string>? scope = null;
        bool doFilter = filterIdx >= 0 && filterValue != null && scopeIdx >= 0;
        if (doFilter)
        {
            scope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool first = true;
            foreach (var line in File.ReadLines(inCsv))
            {
                if (first) { first = false; continue; }
                var f = SplitCsvLine(line);
                if (filterIdx < f.Length && string.Equals(f[filterIdx], filterValue, StringComparison.OrdinalIgnoreCase)
                    && scopeIdx < f.Length) scope.Add(f[scopeIdx]);
            }
        }

        // pass 2: emit
        long inRows = 0, outRows = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outCsv))!);
        using (var w = new StreamWriter(new FileStream(outCsv, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20), new UTF8Encoding(false), 1 << 20))
        {
            w.Write(string.Join(",", keys.Select(Csv)) + ",WeekKey," + string.Join(",", meas.Select(m => Csv(m.outName))) + "\r\n");
            bool first = true;
            foreach (var line in File.ReadLines(inCsv))
            {
                if (first) { first = false; continue; }
                var f = SplitCsvLine(line);
                if (doFilter && (scopeIdx >= f.Length || !scope!.Contains(f[scopeIdx]))) continue;
                inRows++;
                string keyPart = string.Join(",", keyIdx.Select(k => Csv(k < f.Length ? f[k] : "")));
                foreach (var (week, cols) in weekCols)
                {
                    bool any = false;
                    var vals = new string[meas.Length];
                    for (int m = 0; m < meas.Length; m++)
                    {
                        string v = cols[m] >= 0 && cols[m] < f.Length ? f[cols[m]] : "";
                        vals[m] = v;
                        if (!string.IsNullOrWhiteSpace(v)) any = true;
                    }
                    if (!any) continue;
                    w.Write(keyPart); w.Write(','); w.Write(Csv(week));
                    for (int m = 0; m < meas.Length; m++) { w.Write(','); w.Write(Csv(vals[m])); }
                    w.Write("\r\n");
                    outRows++;
                }
            }
        }
        var info = new FileInfo(outCsv);
        return new
        {
            ok = true, inRows, outRows, weeks = weekCols.Count, scopeKept = scope?.Count,
            outCsv, bytes = info.Length,
            note = "Load with Csv.Document() and parse WeekKey to a date - no unpivot needed.",
        };
    }

    // minimal RFC-4180 CSV line splitter (handles quoted fields with embedded commas/quotes)
    private static string[] SplitCsvLine(string line)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQ)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQ = false; }
                else sb.Append(c);
            }
            else
            {
                if (c == ',') { outp.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"') inQ = true;
                else sb.Append(c);
            }
        }
        outp.Add(sb.ToString());
        return outp.ToArray();
    }

    public object ListSheets(string xlsxPath)
    {
        if (!File.Exists(xlsxPath)) throw new FileNotFoundException($"xlsx not found: {xlsxPath}");
        using var zip = ZipFile.OpenRead(xlsxPath);
        var sheets = new List<object>();
        var wb = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("not an xlsx (no xl/workbook.xml).");
        using (var s = wb.Open())
        using (var r = XmlReader.Create(s))
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                    sheets.Add(new { name = r.GetAttribute("name"), sheetId = r.GetAttribute("sheetId") });
        return new { ok = true, xlsxPath, sheets };
    }

    /// <summary>
    /// Reads the workbook-level defined names (name + refers-to formula) from an .xlsx/.xlsm.
    /// A deleted source range serializes as e.g. "Sheet1!#REF!" in the refers-to text - callers scan
    /// for that to detect broken named ranges. Returns an empty list (never throws) when the file or
    /// the xl/workbook.xml entry is missing or unreadable, so lint rules can call this safely.
    /// </summary>
    public static List<(string Name, string RefersTo)> ReadDefinedNames(string xlsxPath)
    {
        var names = new List<(string Name, string RefersTo)>();
        try
        {
            if (!File.Exists(xlsxPath)) return names;
            using var zip = ZipFile.OpenRead(xlsxPath);
            var wb = zip.GetEntry("xl/workbook.xml");
            if (wb == null) return names;
            using var s = wb.Open();
            using var r = XmlReader.Create(s);
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "definedName")
                {
                    var name = r.GetAttribute("name") ?? "";
                    // ReadElementContentAsString advances PAST the end element itself - do not Read() again.
                    names.Add((name, r.ReadElementContentAsString()));
                }
                else if (!r.Read()) break;
            }
        }
        catch { /* corrupt/locked workbook -> whatever was read so far, never throw */ }
        return names;
    }

    // ---------------------------------------------------------------- helpers
    private static string ResolveSheetPart(ZipArchive zip, string? sheetName, out string resolvedName)
    {
        // name -> r:id (workbook.xml), r:id -> target (workbook.xml.rels)
        string? rid = null; resolvedName = sheetName ?? "";
        var wb = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("not an xlsx (no xl/workbook.xml).");
        using (var s = wb.Open())
        using (var r = XmlReader.Create(s))
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                {
                    var nm = r.GetAttribute("name");
                    var id = r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                             ?? r.GetAttribute("r:id");
                    if (sheetName == null) { rid = id; resolvedName = nm ?? ""; break; }   // first sheet
                    if (string.Equals(nm, sheetName, StringComparison.OrdinalIgnoreCase)) { rid = id; resolvedName = nm!; break; }
                }
        if (rid == null) throw new InvalidOperationException($"sheet '{sheetName}' not found in workbook.");

        string? target = null;
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidOperationException("workbook rels missing.");
        using (var s = rels.Open())
        using (var r = XmlReader.Create(s))
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") == rid)
                { target = r.GetAttribute("Target"); break; }
        if (target == null) throw new InvalidOperationException($"relationship '{rid}' has no target.");
        target = target.Replace('\\', '/').TrimStart('/');
        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
    }

    private static List<string> LoadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var e = zip.GetEntry("xl/sharedStrings.xml");
        if (e == null) return list;
        using var s = e.Open();
        using var r = XmlReader.Create(s, new XmlReaderSettings { IgnoreComments = true });
        var sb = new StringBuilder();
        bool inSi = false, inT = false;
        // Accumulate the Text children of every <t> inside an <si> (handles multi-run
        // rich text). Do NOT use ReadElementContentAsString here - mixing it with
        // while(Read()) skips the node after </t> and drops entries.
        while (r.Read())
        {
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                    if (r.LocalName == "si") { inSi = true; sb.Clear(); }
                    else if (r.LocalName == "t") inT = true;
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.SignificantWhitespace:
                    if (inT) sb.Append(r.Value);
                    break;
                case XmlNodeType.EndElement:
                    if (r.LocalName == "t") inT = false;
                    else if (r.LocalName == "si") { list.Add(sb.ToString()); inSi = false; }
                    break;
            }
        }
        _ = inSi;
        return list;
    }

    private static string Resolve(string v, string? type, List<string> shared)
    {
        if (type == "s" && int.TryParse(v, out int idx) && idx >= 0 && idx < shared.Count) return shared[idx];
        if (type == "b") return v == "1" ? "TRUE" : "FALSE";
        return v;
    }

    private static void Place(string?[] row, int col, string val)
    {
        if (col < 0) return;
        if (col >= row.Length) Grow(ref row, col + 1);
        row[col] = val;
    }

    private static void Grow(ref string?[] row, int size)
    {
        int n = row.Length; while (n < size) n *= 2;
        Array.Resize(ref row, n);
    }

    private static void WriteRow(StreamWriter w, string?[] row, int cols)
    {
        for (int i = 0; i < cols; i++)
        {
            if (i > 0) w.Write(',');
            w.Write(Csv(row[i]));
        }
        w.Write("\r\n");
    }

    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    private static int ColIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;
        int n = 0;
        foreach (char c in cellRef)
        {
            if (c >= 'A' && c <= 'Z') n = n * 26 + (c - 'A' + 1);
            else if (c >= 'a' && c <= 'z') n = n * 26 + (c - 'a' + 1);
            else break;
        }
        return n - 1;   // 0-based
    }

    private static int ColsFromDimension(string? @ref)
    {
        if (string.IsNullOrEmpty(@ref)) return 0;
        int colon = @ref.IndexOf(':');
        string end = colon >= 0 ? @ref[(colon + 1)..] : @ref;
        return ColIndex(end) + 1;
    }
}
