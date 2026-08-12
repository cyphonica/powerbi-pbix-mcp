using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SuperBiMcp.Services;

/// <summary>
/// OFFLINE template-model editing: read and edit the model of a CLOSED .pbit (Power BI template) with NO
/// Power BI Desktop and NO running engine. A .pbit is an OPC ZIP whose model lives in a single, uncompressed,
/// unencrypted "DataModelSchema" part - TMSL JSON of the shape
/// <c>{ "name":.., "compatibilityLevel":.., "model": { "tables":[...], "relationships":[...] } }</c>. A
/// template carries no data (no XPress9, no VertiPaq, no SQLite), so the plain-JSON route is the right one -
/// this complements <see cref="OfflinePbixService"/>'s engine-backed edit_measure_offline, which needs a
/// Desktop window and a model with real data.
///
/// Reuse, not duplication:
///   - the ZIP rewrite discipline is <see cref="ReportService"/>'s Repack: re-emit ONLY the changed part
///     (DataModelSchema), copy every other part through verbatim (its decompressed content is preserved
///     byte-for-byte), and stage through a temp file then move into place;
///   - the part inventory / encoding treatment mirrors <see cref="PbixDoctor"/> (which already knows a
///     DataModelSchema part is a template's schema-only model) and <see cref="ReportService"/>'s
///     BOM-aware Report/Layout decode;
///   - the JSON is navigated with System.Text.Json.Nodes exactly as ReportService / PbixDoctor edit their
///     JSON parts. There is no TOM/engine round-trip: a surgical JsonNode edit preserves every property the
///     template carries (annotations, extended properties, partitions) that a full TOM re-serialise would
///     drop, and it is fully unit-testable headless.
///
/// Every edit tool writes the .pbit back in place with a .bak guard; <see cref="SaveTemplateModel"/> is the
/// explicit save-as (or in-place re-materialise). Pure JSON throughout, so the whole surface is exercised
/// headless in <c>TemplateModelServiceTests</c> against a synthetic .pbit - no Desktop, no engine, no CI box
/// requirement.
/// </summary>
public sealed class TemplateModelService
{
    /// <summary>The OPC part name that holds a template's TMSL model. A .pbix stores a binary "DataModel";
    /// a .pbit stores this plain-JSON "DataModelSchema" instead.</summary>
    internal const string SchemaPart = "DataModelSchema";

    private readonly ILogger<TemplateModelService>? _log;

    public TemplateModelService(ILogger<TemplateModelService>? log = null) => _log = log;

    // ---------------------------------------------------------------- open (read-only summary)

    /// <summary>Unzip a closed .pbit, read its DataModelSchema part and return a structured summary: every
    /// table with its columns (name + data type + key properties) and measures (name + DAX expression +
    /// display folder), plus the relationships (from/to). A .pbit with no DataModelSchema part - a
    /// live-connection or PBIR-only template - returns ok:false with a clear note rather than throwing.</summary>
    public object OpenTemplateModel(string pbitPath)
    {
        ValidatePbitPath(pbitPath);
        var part = ReadSchemaPart(pbitPath);
        if (part is null)
            return new
            {
                ok = false,
                pbitPath,
                note = $"This .pbit has no {SchemaPart} part - it carries no editable JSON model "
                     + "(a live-connection or PBIR-only template). Nothing to read offline.",
            };
        var root = ParseModel(part.Value.Text);
        return new { ok = true, pbitPath, model = Summarise(root) };
    }

    // ---------------------------------------------------------------- measures

    /// <summary>Add a measure to the named table's measures array in the DataModelSchema. Fails if a measure
    /// of that name already exists on the table (add is collision-checked). Writes the .pbit back in place.</summary>
    public object AddTemplateMeasure(string pbitPath, string table, string name,
        string expression, string? formatString = null, string? displayFolder = null)
    {
        RequireName(name, "measure");
        if (string.IsNullOrWhiteSpace(expression))
            throw new InvalidOperationException($"A DAX expression is required to add measure '{table}[{name}]'.");

        var (part, root) = LoadForEdit(pbitPath);
        var t = FindTable(ModelNode(root), table);
        var measures = EnsureArray(t, "measures");
        if (FindByName(measures, name) is not null)
            throw new InvalidOperationException(
                $"Measure '{table}[{name}]' already exists - use update_template_measure to change it.");

        var me = new JsonObject { ["name"] = name, ["expression"] = expression };
        if (!string.IsNullOrWhiteSpace(formatString)) me["formatString"] = formatString;
        if (!string.IsNullOrWhiteSpace(displayFolder)) me["displayFolder"] = displayFolder;
        measures.Add(me);

        string outPath = SaveEdited(pbitPath, part, root);
        return new
        {
            ok = true,
            pbitPath,
            action = "added",
            measure = $"{table}[{name}]",
            after = MeasureView(me),
            persistedToDisk = true,
            backupPath = outPath + ".bak",
        };
    }

    /// <summary>Update an existing measure's expression, format string and/or display folder. Any omitted
    /// field is left unchanged. Fails if the measure does not exist. Writes the .pbit back in place.</summary>
    public object UpdateTemplateMeasure(string pbitPath, string table, string name,
        string? expression = null, string? formatString = null, string? displayFolder = null)
    {
        RequireName(name, "measure");
        var (part, root) = LoadForEdit(pbitPath);
        var t = FindTable(ModelNode(root), table);
        var measures = t["measures"] as JsonArray;
        var me = measures is null ? null : FindByName(measures, name);
        if (me is null)
            throw new InvalidOperationException(
                $"Measure '{table}[{name}]' does not exist - use add_template_measure to create it.");

        object before = MeasureView(me);
        if (expression != null) me["expression"] = expression;
        if (formatString != null) me["formatString"] = formatString;
        if (displayFolder != null) me["displayFolder"] = displayFolder;

        string outPath = SaveEdited(pbitPath, part, root);
        return new
        {
            ok = true,
            pbitPath,
            action = "updated",
            measure = $"{table}[{name}]",
            before,
            after = MeasureView(me),
            persistedToDisk = true,
            backupPath = outPath + ".bak",
        };
    }

    /// <summary>Delete a measure from the named table. Fails if the measure does not exist. Writes the
    /// .pbit back in place.</summary>
    public object DeleteTemplateMeasure(string pbitPath, string table, string name)
    {
        RequireName(name, "measure");
        var (part, root) = LoadForEdit(pbitPath);
        var t = FindTable(ModelNode(root), table);
        var measures = t["measures"] as JsonArray;
        int idx = measures is null ? -1 : IndexOfName(measures, name);
        if (idx < 0)
            throw new InvalidOperationException($"Measure '{table}[{name}]' does not exist - nothing to delete.");

        object removed = MeasureView((JsonObject)measures![idx]!);
        measures.RemoveAt(idx);

        string outPath = SaveEdited(pbitPath, part, root);
        return new
        {
            ok = true,
            pbitPath,
            action = "deleted",
            measure = $"{table}[{name}]",
            removed,
            persistedToDisk = true,
            backupPath = outPath + ".bak",
        };
    }

    // ---------------------------------------------------------------- columns

    /// <summary>Patch properties of an existing column in the named table: data type, format string, hidden
    /// flag, sort-by column and default summarisation. Any omitted property is left unchanged. Fails if the
    /// column does not exist. Writes the .pbit back in place.</summary>
    public object SetTemplateColumn(string pbitPath, string table, string column,
        string? dataType = null, string? formatString = null, bool? isHidden = null,
        string? sortByColumn = null, string? summarizeBy = null)
    {
        RequireName(column, "column");
        var (part, root) = LoadForEdit(pbitPath);
        var t = FindTable(ModelNode(root), table);
        var columns = t["columns"] as JsonArray;
        var c = columns is null ? null : FindByName(columns, column);
        if (c is null)
            throw new InvalidOperationException($"Column '{table}[{column}]' does not exist in the template model.");

        object before = ColumnView(c);
        if (dataType != null) c["dataType"] = dataType;
        if (formatString != null) c["formatString"] = formatString;
        if (isHidden != null) c["isHidden"] = isHidden.Value;
        if (sortByColumn != null) c["sortByColumn"] = sortByColumn;
        if (summarizeBy != null) c["summarizeBy"] = summarizeBy;

        string outPath = SaveEdited(pbitPath, part, root);
        return new
        {
            ok = true,
            pbitPath,
            column = $"{table}[{column}]",
            before,
            after = ColumnView(c),
            persistedToDisk = true,
            backupPath = outPath + ".bak",
        };
    }

    // ---------------------------------------------------------------- relationships

    /// <summary>Add a relationship to the model's relationships array. The endpoints must exist (both tables
    /// and both columns are validated). A fresh GUID name is generated. Fails if an identical from/to
    /// relationship already exists. Writes the .pbit back in place.</summary>
    public object AddTemplateRelationship(string pbitPath, string fromTable, string fromColumn,
        string toTable, string toColumn, string? crossFilteringBehavior = null, bool isActive = true)
    {
        RequireName(fromTable, "fromTable"); RequireName(fromColumn, "fromColumn");
        RequireName(toTable, "toTable"); RequireName(toColumn, "toColumn");
        string? cfb = NormaliseCrossFilter(crossFilteringBehavior);

        var (part, root) = LoadForEdit(pbitPath);
        var model = ModelNode(root);
        // both endpoints must resolve to a real table + column, so a typo cannot silently write a dead relationship
        RequireColumn(model, fromTable, fromColumn);
        RequireColumn(model, toTable, toColumn);

        var rels = EnsureArray(model, "relationships");
        if (FindRelationship(rels, fromTable, fromColumn, toTable, toColumn) is not null)
            throw new InvalidOperationException(
                $"A relationship {fromTable}[{fromColumn}] -> {toTable}[{toColumn}] already exists.");

        string relName = Guid.NewGuid().ToString();
        var rel = new JsonObject
        {
            ["name"] = relName,
            ["fromTable"] = fromTable,
            ["fromColumn"] = fromColumn,
            ["toTable"] = toTable,
            ["toColumn"] = toColumn,
        };
        if (cfb != null) rel["crossFilteringBehavior"] = cfb;
        if (!isActive) rel["isActive"] = false;   // TMSL defaults isActive to true; only write the non-default
        rels.Add(rel);

        string outPath = SaveEdited(pbitPath, part, root);
        return new
        {
            ok = true,
            pbitPath,
            action = "added",
            relationship = new
            {
                name = relName,
                from = $"{fromTable}[{fromColumn}]",
                to = $"{toTable}[{toColumn}]",
                crossFilteringBehavior = cfb,
                isActive,
            },
            persistedToDisk = true,
            backupPath = outPath + ".bak",
        };
    }

    /// <summary>Delete the relationship whose endpoints match from/to. Fails if no such relationship exists.
    /// Writes the .pbit back in place.</summary>
    public object DeleteTemplateRelationship(string pbitPath, string fromTable, string fromColumn,
        string toTable, string toColumn)
    {
        RequireName(fromTable, "fromTable"); RequireName(fromColumn, "fromColumn");
        RequireName(toTable, "toTable"); RequireName(toColumn, "toColumn");

        var (part, root) = LoadForEdit(pbitPath);
        var rels = ModelNode(root)["relationships"] as JsonArray;
        int idx = rels is null ? -1 : IndexOfRelationship(rels, fromTable, fromColumn, toTable, toColumn);
        if (idx < 0)
            throw new InvalidOperationException(
                $"No relationship {fromTable}[{fromColumn}] -> {toTable}[{toColumn}] found - nothing to delete.");

        string? removedName = (string?)((JsonObject)rels![idx]!)["name"];
        rels.RemoveAt(idx);

        string outPath = SaveEdited(pbitPath, part, root);
        return new
        {
            ok = true,
            pbitPath,
            action = "deleted",
            relationship = new { name = removedName, from = $"{fromTable}[{fromColumn}]", to = $"{toTable}[{toColumn}]" },
            persistedToDisk = true,
            backupPath = outPath + ".bak",
        };
    }

    // ---------------------------------------------------------------- save (explicit rewrite / save-as)

    /// <summary>Rewrite the .pbit ZIP with the current DataModelSchema, preserving every other part
    /// byte-for-byte. With no outPath it re-materialises in place (guarded by a .bak of the original); with
    /// an outPath it writes an edited copy there and leaves the source untouched. The edit tools persist on
    /// each call, so this is the explicit save-as (or an integrity re-pack).</summary>
    public object SaveTemplateModel(string pbitPath, string? outPath = null)
    {
        ValidatePbitPath(pbitPath);
        var part = ReadSchemaPart(pbitPath)
            ?? throw new InvalidOperationException(
                $"This .pbit has no {SchemaPart} part - there is no template model to save.");

        bool saveAs = !string.IsNullOrWhiteSpace(outPath);
        string dest = saveAs ? outPath! : pbitPath;
        if (saveAs && !dest.EndsWith(".pbit", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"outPath must be a .pbit file: '{dest}'.");

        // re-encode the (unchanged) schema in its original charset/BOM so a plain save is a faithful round-trip
        byte[] schemaBytes = EncodeTextPart(part.Charset, part.HasBom, part.Text);
        string written = RewriteZipWithSchema(pbitPath, dest, schemaBytes);

        return new
        {
            ok = true,
            pbitPath,
            outPath = written,
            savedInPlace = !saveAs,
            backupPath = saveAs ? null : written + ".bak",
            schemaBytes = schemaBytes.Length,
            note = saveAs
                ? "Wrote an edited copy; the source .pbit was left untouched."
                : "Re-packed in place; the original was copied to the .bak beside it first.",
        };
    }

    // ---------------------------------------------------------------- edit plumbing

    /// <summary>Read + parse the DataModelSchema for an edit: returns the part (for its charset/BOM on save)
    /// and the parsed TMSL root object. Throws a clear error when the template has no model part, so an edit
    /// against a live-connection template fails loudly rather than silently.</summary>
    private (TextPart Part, JsonObject Root) LoadForEdit(string pbitPath)
    {
        ValidatePbitPath(pbitPath);
        var part = ReadSchemaPart(pbitPath)
            ?? throw new InvalidOperationException(
                $"This .pbit has no {SchemaPart} part - it carries no editable model to change.");
        return (part, ParseModel(part.Text));
    }

    /// <summary>Serialise the edited root and re-pack the .pbit in place (charset/BOM preserved), returning
    /// the written path. In-place writes are .bak-guarded by <see cref="RewriteZipWithSchema"/>.</summary>
    private string SaveEdited(string pbitPath, TextPart part, JsonObject root)
    {
        byte[] schemaBytes = EncodeTextPart(part.Charset, part.HasBom, SerialiseModel(root));
        return RewriteZipWithSchema(pbitPath, pbitPath, schemaBytes);
    }

    // ---------------------------------------------------------------- ZIP read / rewrite (ReportService discipline)

    /// <summary>Read the DataModelSchema part out of a .pbit and decode it to text (charset + BOM detected).
    /// Returns null when the part is absent.</summary>
    internal static TextPart? ReadSchemaPart(string pbitPath)
    {
        using var zip = ZipFile.OpenRead(pbitPath);
        var entry = FindEntry(zip, SchemaPart);
        if (entry is null) return null;
        byte[] raw;
        using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); raw = ms.ToArray(); }
        return DecodeTextPart(raw);
    }

    /// <summary>
    /// Re-pack <paramref name="srcPbit"/> to <paramref name="destPath"/> replacing ONLY the DataModelSchema
    /// part with <paramref name="newSchemaBytes"/> and copying every other part through verbatim - its
    /// decompressed content is preserved byte-for-byte (Report/Layout, [Content_Types].xml, Version, etc.).
    /// The one exception: SecurityBindings (the package signature) is DROPPED, because an edited model no
    /// longer matches it - Desktop re-signs on next open, exactly as ReportService does on a .pbix rewrite.
    /// This is ReportService.Repack's discipline: stage through a temp file,
    /// then move into place. An in-place write (dest == src) first copies the original to a .bak beside it.
    /// DataModelSchema is (re)written uncompressed, matching how a real template stores it.
    /// </summary>
    private string RewriteZipWithSchema(string srcPbit, string destPath, byte[] newSchemaBytes)
    {
        bool inPlace = PathsEqual(srcPbit, destPath);
        string finalPath = inPlace ? srcPbit : Path.GetFullPath(destPath);
        string tmp = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var oic = StringComparison.OrdinalIgnoreCase;
        bool replaced = false;

        var destDir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        using (var src = ZipFile.OpenRead(srcPbit))
        using (var dstStream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var dst = new ZipArchive(dstStream, ZipArchiveMode.Create))
        {
            foreach (var e in src.Entries)
            {
                // preserve any directory-marker entries as-is (real OPC packages are flat, but stay robust)
                if (e.FullName.EndsWith("/", StringComparison.Ordinal)) { dst.CreateEntry(e.FullName); continue; }

                // an edited model invalidates the package signature - drop SecurityBindings so Desktop
                // re-signs on next open (same discipline as ReportService's .pbix rewrite)
                if (string.Equals(e.FullName, "SecurityBindings", oic)) continue;

                if (string.Equals(e.FullName, SchemaPart, oic))
                {
                    var ne = dst.CreateEntry(e.FullName, CompressionLevel.NoCompression);
                    using var s = ne.Open(); s.Write(newSchemaBytes, 0, newSchemaBytes.Length);
                    replaced = true;
                }
                else
                {
                    // CopyTo inflates then deflates: the compressed bytes may differ, the CONTENT is identical
                    var ne = dst.CreateEntry(e.FullName, CompressionLevel.Optimal);
                    using var os = e.Open(); using var ns = ne.Open(); os.CopyTo(ns);
                }
            }
        }

        if (!replaced)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw new InvalidOperationException($"This .pbit has no {SchemaPart} part to rewrite.");
        }

        if (inPlace)
        {
            File.Copy(srcPbit, srcPbit + ".bak", overwrite: true);   // safety net before we replace the original
            File.Delete(srcPbit);
            File.Move(tmp, srcPbit);
            Log("REPACK in-place " + srcPbit);
            return srcPbit;
        }
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmp, finalPath);
        Log("REPACK save-as " + finalPath);
        return finalPath;
    }

    private void Log(string m)
    {
        if (_log is { } l) l.LogInformation("{Message}", m);
    }

    // ---------------------------------------------------------------- TMSL JSON navigation (System.Text.Json.Nodes)

    /// <summary>Parse a DataModelSchema text blob into its TMSL root object.</summary>
    internal static JsonObject ParseModel(string text) =>
        JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("DataModelSchema is not a JSON object - not a TMSL model document.");

    /// <summary>Serialise a TMSL root back to text. Indented (TMSL convention) and with relaxed escaping so
    /// DAX comparison operators (&lt; &gt; &amp;) stay readable; the output round-trips through any JSON parser.</summary>
    internal static string SerialiseModel(JsonObject root) => root.ToJsonString(SerialiseOpts);

    private static readonly JsonSerializerOptions SerialiseOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The model object under the TMSL root.</summary>
    private static JsonObject ModelNode(JsonObject root) =>
        root["model"] as JsonObject
            ?? throw new InvalidOperationException("DataModelSchema has no 'model' object - not a TMSL database document.");

    /// <summary>Find a table by name (case-insensitive, matching Power BI's name semantics).</summary>
    private static JsonObject FindTable(JsonObject model, string table)
    {
        RequireName(table, "table");
        var tables = model["tables"] as JsonArray
            ?? throw new InvalidOperationException("The template model has no 'tables' array.");
        return FindByName(tables, table)
            ?? throw new InvalidOperationException($"Table '{table}' not found in the template model.");
    }

    private static void RequireColumn(JsonObject model, string table, string column)
    {
        var t = FindTable(model, table);
        var columns = t["columns"] as JsonArray;
        if (columns is null || FindByName(columns, column) is null)
            throw new InvalidOperationException($"Column '{table}[{column}]' not found in the template model.");
    }

    private static JsonArray EnsureArray(JsonObject parent, string prop)
    {
        if (parent[prop] is JsonArray a) return a;
        var arr = new JsonArray();
        parent[prop] = arr;
        return arr;
    }

    private static JsonObject? FindByName(JsonArray arr, string name)
    {
        int i = IndexOfName(arr, name);
        return i < 0 ? null : (JsonObject)arr[i]!;
    }

    private static int IndexOfName(JsonArray arr, string name)
    {
        for (int i = 0; i < arr.Count; i++)
            if (arr[i] is JsonObject o && string.Equals((string?)o["name"], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static JsonObject? FindRelationship(JsonArray rels, string fromT, string fromC, string toT, string toC)
    {
        int i = IndexOfRelationship(rels, fromT, fromC, toT, toC);
        return i < 0 ? null : (JsonObject)rels[i]!;
    }

    private static int IndexOfRelationship(JsonArray rels, string fromT, string fromC, string toT, string toC)
    {
        for (int i = 0; i < rels.Count; i++)
        {
            if (rels[i] is not JsonObject r) continue;
            if (Eq(r["fromTable"], fromT) && Eq(r["fromColumn"], fromC)
                && Eq(r["toTable"], toT) && Eq(r["toColumn"], toC))
                return i;
        }
        return -1;
    }

    private static bool Eq(JsonNode? node, string value) =>
        string.Equals((string?)node, value, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- summary + view shaping

    /// <summary>Shape the parsed TMSL into the open_template_model summary.</summary>
    internal static object Summarise(JsonObject root)
    {
        var model = ModelNode(root);
        var tables = new List<object>();
        foreach (var tn in (model["tables"] as JsonArray) ?? new JsonArray())
        {
            if (tn is not JsonObject t) continue;
            var columns = new List<object>();
            foreach (var cn in (t["columns"] as JsonArray) ?? new JsonArray())
                if (cn is JsonObject c) columns.Add(ColumnView(c));
            var measures = new List<object>();
            foreach (var mn in (t["measures"] as JsonArray) ?? new JsonArray())
                if (mn is JsonObject me) measures.Add(MeasureView(me));
            tables.Add(new
            {
                name = (string?)t["name"],
                isHidden = (bool?)t["isHidden"] ?? false,
                columnCount = columns.Count,
                measureCount = measures.Count,
                columns,
                measures,
            });
        }
        var relationships = new List<object>();
        foreach (var rn in (model["relationships"] as JsonArray) ?? new JsonArray())
            if (rn is JsonObject r) relationships.Add(RelationshipView(r));

        return new
        {
            name = (string?)root["name"],
            compatibilityLevel = (int?)root["compatibilityLevel"],
            tableCount = tables.Count,
            relationshipCount = relationships.Count,
            tables,
            relationships,
        };
    }

    private static object ColumnView(JsonObject c) => new
    {
        name = (string?)c["name"],
        dataType = (string?)c["dataType"],
        isHidden = (bool?)c["isHidden"] ?? false,
        formatString = (string?)c["formatString"],
        sortByColumn = (string?)c["sortByColumn"],
        summarizeBy = (string?)c["summarizeBy"],
    };

    private static object MeasureView(JsonObject me) => new
    {
        name = (string?)me["name"],
        expression = MeasureExpressionText(me["expression"]),
        formatString = (string?)me["formatString"],
        displayFolder = (string?)me["displayFolder"],
    };

    private static object RelationshipView(JsonObject r) => new
    {
        name = (string?)r["name"],
        fromTable = (string?)r["fromTable"],
        fromColumn = (string?)r["fromColumn"],
        toTable = (string?)r["toTable"],
        toColumn = (string?)r["toColumn"],
        crossFilteringBehavior = (string?)r["crossFilteringBehavior"],
        isActive = (bool?)r["isActive"] ?? true,
    };

    /// <summary>A TMSL measure expression is a single string OR an array of line strings; render either to
    /// one text value (lines joined with newlines) so the summary and before/after views read the same.</summary>
    internal static string? MeasureExpressionText(JsonNode? expr)
    {
        if (expr is null) return null;
        if (expr is JsonArray arr) return string.Join("\n", arr.Select(x => (string?)x ?? string.Empty));
        return (string?)expr;
    }

    // ---------------------------------------------------------------- text encoding (BOM-aware, like Report/Layout)

    internal enum PartCharset { Utf8, Utf16Le, Utf16Be }

    /// <summary>A decoded text part: the charset + whether it carried a BOM (so a save re-emits it exactly),
    /// plus the decoded text.</summary>
    internal readonly struct TextPart
    {
        public PartCharset Charset { get; init; }
        public bool HasBom { get; init; }
        public string Text { get; init; }
    }

    /// <summary>Decode a text part's bytes: honour a UTF-8 / UTF-16 BOM if present, else sniff UTF-16 from the
    /// interleaved-null pattern of ASCII-heavy JSON, else fall back to UTF-8. Mirrors the BOM-aware decode
    /// ReportService uses for Report/Layout (which PbixDoctor flags as "UTF-16-LE, BOM optional").</summary>
    internal static TextPart DecodeTextPart(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return new TextPart { Charset = PartCharset.Utf8, HasBom = true, Text = new UTF8Encoding(false).GetString(raw, 3, raw.Length - 3) };
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return new TextPart { Charset = PartCharset.Utf16Le, HasBom = true, Text = new UnicodeEncoding(false, false).GetString(raw, 2, raw.Length - 2) };
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            return new TextPart { Charset = PartCharset.Utf16Be, HasBom = true, Text = new UnicodeEncoding(true, false).GetString(raw, 2, raw.Length - 2) };
        // no BOM - sniff: JSON always starts with an ASCII byte ('{' or whitespace), so a following 0x00 => UTF-16 LE
        if (raw.Length >= 2 && raw[0] != 0 && raw[1] == 0)
            return new TextPart { Charset = PartCharset.Utf16Le, HasBom = false, Text = new UnicodeEncoding(false, false).GetString(raw) };
        if (raw.Length >= 2 && raw[0] == 0 && raw[1] != 0)
            return new TextPart { Charset = PartCharset.Utf16Be, HasBom = false, Text = new UnicodeEncoding(true, false).GetString(raw) };
        return new TextPart { Charset = PartCharset.Utf8, HasBom = false, Text = new UTF8Encoding(false).GetString(raw) };
    }

    /// <summary>Re-encode text in the SAME charset/BOM it was read in, so an unchanged part round-trips
    /// byte-for-byte and an edited part stays in the template's original encoding.</summary>
    internal static byte[] EncodeTextPart(PartCharset charset, bool hasBom, string text)
    {
        Encoding enc = charset switch
        {
            PartCharset.Utf16Le => new UnicodeEncoding(false, false),
            PartCharset.Utf16Be => new UnicodeEncoding(true, false),
            _ => new UTF8Encoding(false),
        };
        byte[] body = enc.GetBytes(text);
        if (!hasBom) return body;
        byte[] bom = charset switch
        {
            PartCharset.Utf16Le => new byte[] { 0xFF, 0xFE },
            PartCharset.Utf16Be => new byte[] { 0xFE, 0xFF },
            _ => new byte[] { 0xEF, 0xBB, 0xBF },
        };
        var outBytes = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, outBytes, 0, bom.Length);
        Buffer.BlockCopy(body, 0, outBytes, bom.Length, body.Length);
        return outBytes;
    }

    // ---------------------------------------------------------------- validation + small helpers

    /// <summary>A .pbit path must be non-empty, carry the .pbit extension and exist on disk.</summary>
    internal static void ValidatePbitPath(string pbitPath)
    {
        if (string.IsNullOrWhiteSpace(pbitPath)) throw new InvalidOperationException("A .pbit path is required.");
        if (!pbitPath.EndsWith(".pbit", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Not a .pbit file: '{pbitPath}'.");
        if (!File.Exists(pbitPath)) throw new InvalidOperationException($"File not found: '{pbitPath}'.");
    }

    private static void RequireName(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"A {what} name is required.");
    }

    /// <summary>Normalise a cross-filtering value to its TMSL token, or null (the default, single-direction).</summary>
    internal static string? NormaliseCrossFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        if (v.Equals("bothDirections", StringComparison.OrdinalIgnoreCase) || v.Equals("both", StringComparison.OrdinalIgnoreCase))
            return "bothDirections";
        if (v.Equals("oneDirection", StringComparison.OrdinalIgnoreCase) || v.Equals("single", StringComparison.OrdinalIgnoreCase)
            || v.Equals("one", StringComparison.OrdinalIgnoreCase))
            return "oneDirection";
        if (v.Equals("automatic", StringComparison.OrdinalIgnoreCase))
            return "automatic";
        throw new InvalidOperationException(
            $"crossFilteringBehavior must be oneDirection, bothDirections or automatic (got '{value}').");
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
