using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp;

/// <summary>
/// Headless model authoring - the cloud-native lever. Builds a Tabular model IN MEMORY from a JSON spec
/// (no live Power BI Desktop / Analysis Services engine), serialises it to TMDL with Microsoft's official
/// serializer (so the syntax is always correct), and scaffolds a complete PBIP project that Power BI
/// Desktop / Fabric open and Refresh to load data. This is what takes the whole pipeline - data -&gt;
/// model -&gt; report - off the desktop and into a cloud service.
///
///   SuperBiMcp scaffold &lt;spec.json&gt; &lt;outputFolder&gt; [projectName]
/// </summary>
public static class Headless
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: SuperBiMcp scaffold <spec.json> <outputFolder> [name]"); return 2; }
            if (!File.Exists(args[1])) { Console.Error.WriteLine($"spec not found: {args[1]}"); return 2; }
            string name = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : "Model";
            var result = GenerateProject(File.ReadAllText(args[1]), args[2], name);
            Console.WriteLine(JsonSerializer.Serialize(result, Cli.Pretty));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
    }

    /// <summary>Build a TOM model in memory from the spec, serialize to TMDL, and scaffold a PBIP project.</summary>
    public static object GenerateProject(string specJson, string outputFolder, string name)
    {
        var spec = JsonNode.Parse(specJson) as JsonObject ?? throw new InvalidOperationException("spec is not a JSON object.");
        var model = new TOM.Model();
        // resolve the DataFolder parameter's substituted value so partition M that reads CSVs from it can be
        // inlined as base64 (self-contained + bakeable on standalone SSAS - see InlineData).
        string? dataFolder = ExtractDataFolder(spec);

        // shared expressions / parameters (e.g. a DataFolder parameter the M reads)
        if (spec["expressions"] is JsonArray exprs)
            foreach (var e in exprs)
                if (e is JsonObject eo && (string?)eo["name"] is string en)
                    model.Expressions.Add(new TOM.NamedExpression { Name = en, Kind = TOM.ExpressionKind.M, Expression = (string?)eo["expression"] ?? "" });

        if (spec["tables"] is not JsonArray tables || tables.Count == 0)
            throw new InvalidOperationException("spec needs a non-empty \"tables\" array.");

        foreach (var tn in tables)
        {
            if (tn is not JsonObject to) continue;
            string tname = (string?)to["name"] ?? throw new InvalidOperationException("each table needs a \"name\".");
            var t = new TOM.Table { Name = tname };
            t.Partitions.Add(new TOM.Partition
            {
                Name = tname,
                Source = new TOM.MPartitionSource { Expression = InlineData((string?)to["m"] ?? "let Source = #table({},{}) in Source", dataFolder) },
            });
            if (to["columns"] is JsonArray cols)
                foreach (var cn in cols)
                    if (cn is JsonObject co && (string?)co["name"] is string colName)
                        t.Columns.Add(new TOM.DataColumn
                        {
                            Name = colName,
                            DataType = MapType((string?)co["dataType"]),
                            SourceColumn = (string?)co["sourceColumn"] ?? colName,
                        });
            if (to["measures"] is JsonArray meas)
                foreach (var mn in meas)
                    if (mn is JsonObject mo && (string?)mo["name"] is string measName)
                    {
                        var m = new TOM.Measure { Name = measName, Expression = (string?)mo["dax"] ?? "0" };
                        if ((string?)mo["format"] is string fmt && fmt.Length > 0) m.FormatString = fmt;
                        if ((string?)mo["displayFolder"] is string df && df.Length > 0) m.DisplayFolder = df;
                        t.Measures.Add(m);
                    }
            if ((bool?)to["dateTable"] == true) t.DataCategory = "Time";
            model.Tables.Add(t);
        }

        // relationships (columns must already exist on the tables added above)
        if (spec["relationships"] is JsonArray rels)
            foreach (var rn in rels)
            {
                if (rn is not JsonObject ro) continue;
                var ft = model.Tables.Find((string?)ro["fromTable"]); var tt = model.Tables.Find((string?)ro["toTable"]);
                if (ft == null || tt == null) continue;
                var fc = ft.Columns.Find((string?)ro["fromColumn"]); var tc = tt.Columns.Find((string?)ro["toColumn"]);
                if (fc == null || tc == null) continue;
                model.Relationships.Add(new TOM.SingleColumnRelationship
                {
                    FromColumn = fc,
                    ToColumn = tc,
                    FromCardinality = TOM.RelationshipEndCardinality.Many,
                    ToCardinality = TOM.RelationshipEndCardinality.One,
                    CrossFilteringBehavior = (bool?)ro["bothDirections"] == true ? TOM.CrossFilteringBehavior.BothDirections : TOM.CrossFilteringBehavior.OneDirection,
                    IsActive = true,
                });
            }

        // ---- write the PBIP project (TMDL model + a minimal report), all text, no engine ----
        var utf8 = new UTF8Encoding(false);
        if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);
        string sm = Path.Combine(outputFolder, name + ".SemanticModel");
        string rp = Path.Combine(outputFolder, name + ".Report");
        Directory.CreateDirectory(sm);
        Directory.CreateDirectory(rp);

        TOM.TmdlSerializer.SerializeModelToFolder(model, Path.Combine(sm, "definition"));
        File.WriteAllText(Path.Combine(sm, "definition.pbism"), "{\n  \"version\": \"4.0\",\n  \"settings\": {}\n}\n", utf8);

        string blankReport =
            "{\"id\":0,\"resourcePackages\":[],\"sections\":[{\"name\":\"ReportSection1\",\"displayName\":\"Page 1\"," +
            "\"filters\":\"[]\",\"visualContainers\":[],\"config\":\"{}\",\"width\":1280,\"height\":720,\"displayOption\":1}]," +
            "\"config\":\"{}\",\"layoutOptimization\":0}";
        File.WriteAllText(Path.Combine(rp, "report.json"), blankReport, utf8);
        File.WriteAllText(Path.Combine(rp, "definition.pbir"),
            "{\n  \"$schema\": \"https://developer.microsoft.com/json-schemas/fabric/item/report/definitionProperties/1.0.0/schema.json\",\n" +
            "  \"version\": \"1.0\",\n  \"datasetReference\": {\n    \"byPath\": { \"path\": \"../" + name + ".SemanticModel\" }\n  }\n}\n", utf8);

        File.WriteAllText(Path.Combine(outputFolder, name + ".pbip"),
            "{\n  \"$schema\": \"https://developer.microsoft.com/json-schemas/fabric/pbip/pbipProperties/1.0.0/schema.json\",\n" +
            "  \"version\": \"1.0\",\n  \"artifacts\": [ { \"report\": { \"path\": \"" + name + ".Report\" } } ],\n  \"settings\": { \"enableAutoRecovery\": true }\n}\n", utf8);
        File.WriteAllText(Path.Combine(outputFolder, ".gitignore"), "**/.pbi/localSettings.json\n**/.pbi/cache.abf\n", utf8);

        var tmdl = Directory.GetFiles(Path.Combine(sm, "definition"), "*.tmdl", System.IO.SearchOption.AllDirectories);
        return new
        {
            ok = true,
            generated = name + ".pbip",
            outputFolder,
            authoredHeadless = true,
            tables = model.Tables.Count,
            measures = model.Tables.Sum(t => t.Measures.Count),
            relationships = model.Relationships.Count,
            tmdlFiles = tmdl.Length,
            tmdlSample = tmdl.Select(f => Path.GetRelativePath(outputFolder, f)).OrderBy(x => x).Take(12).ToArray(),
            note = $"Built a Tabular model in memory and serialised it to TMDL with NO Power BI Desktop / engine. Open {name}.pbip in Desktop or Fabric and Refresh to load data via the M partitions.",
        };
    }

    private static TOM.DataType MapType(string? s) => (s ?? "string").ToLowerInvariant() switch
    {
        "int64" or "int" or "integer" or "whole" => TOM.DataType.Int64,
        "double" or "real" or "float" or "number" => TOM.DataType.Double,
        "decimal" or "currency" or "fixed" => TOM.DataType.Decimal,
        "datetime" or "date" or "time" => TOM.DataType.DateTime,
        "boolean" or "bool" or "logical" => TOM.DataType.Boolean,
        _ => TOM.DataType.String,
    };

    // --- self-contained data: inline the staged/sample CSVs into the partition M as base64. Power BI Desktop
    // tolerates M that reaches an undeclared external file, but a standalone Analysis Services engine refuses it
    // ("An M partition uses a data function which results in access to a data source different from those defined
    // in the model"). Binary.FromText is NOT a data-access function, so it bakes on SSAS AND keeps the delivered
    // .pbix self-contained (no dependency on the build host's file paths). Proven against SSAS 2022 Tabular.
    private static readonly System.Text.RegularExpressions.Regex FileContentsRx = new(
        @"File\.Contents\(\s*DataFolder\s*&\s*""/(?<f>[^""]+)""\s*\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string InlineData(string m, string? dataFolder)
    {
        if (string.IsNullOrEmpty(dataFolder)) return m;
        return FileContentsRx.Replace(m, mt =>
        {
            string path = Path.Combine(dataFolder, mt.Groups["f"].Value);
            if (!File.Exists(path)) return mt.Value; // data not staged here - leave the file read in place
            string b64 = Convert.ToBase64String(File.ReadAllBytes(path));
            return $"Binary.FromText(\"{b64}\", BinaryEncoding.Base64)";
        });
    }

    /// <summary>Pull the substituted folder path out of the DataFolder shared expression (the value the
    /// {{DATA_FOLDER}} token was replaced with), e.g. expression DataFolder = "C:/.../sample" meta [...].</summary>
    private static string? ExtractDataFolder(JsonObject spec)
    {
        if (spec["expressions"] is JsonArray exprs)
            foreach (var e in exprs)
                if (e is JsonObject eo && (string?)eo["name"] == "DataFolder")
                {
                    string expr = (string?)eo["expression"] ?? "";
                    int q1 = expr.IndexOf('"');
                    int q2 = q1 >= 0 ? expr.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1) return expr.Substring(q1 + 1, q2 - q1 - 1);
                }
        return null;
    }
}
