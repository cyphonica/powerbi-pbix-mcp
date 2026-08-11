using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// Synthesises a model.spec.json (the format <see cref="Headless.GenerateProject"/> consumes) from a
/// discovered schema, one flat table per CSV. This is the no-Solution "AI auto-shape" path: when a
/// connector pulls data that does not map onto a known Solution, the pipeline builds a flat-table model
/// from the inferred schema and the existing AI prompter infers measures and relationships on top.
///
/// The output matches the canonical shape exactly (verified against solutions/retail-fmcg/model.spec.json):
///   - a single DataFolder M parameter carrying the {{DATA_FOLDER}} token the bake step substitutes;
///   - one table per CSV whose M is
///       Csv.Document(File.Contents(DataFolder &amp; "/&lt;file&gt;.csv"),
///         [Delimiter=",", Encoding=65001, QuoteStyle=QuoteStyle.Csv])
///       |&gt; Table.PromoteHeaders(PromoteAllScalars=true)
///       |&gt; Table.TransformColumnTypes({{"Col", &lt;M type&gt;}, ...});
///   - a columns[] array with the spec data-type tokens.
/// No relationships or measures are emitted (the AI prompter adds those); a Solution-mapped ingest uses the
/// Solution's hand-authored model.spec.json instead and never calls this.
/// </summary>
public static class FlatSpecBuilder
{
    /// <summary>Build a model-spec JSON string from a discovered schema, with the DataFolder parameter set
    /// to the literal token <c>{{DATA_FOLDER}}</c> (the bake/materialise step substitutes the real path) so
    /// the output is consistent with the Solution specs.</summary>
    public static string Build(SchemaDiscovery schema)
    {
        var spec = new JsonObject
        {
            ["_comment"] = "Auto-shaped flat-table model synthesised from a connector's inferred schema. " +
                           "{{DATA_FOLDER}} is replaced by the bake step with the folder holding the staged CSVs.",
            ["expressions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "DataFolder",
                    ["expression"] = "\"{{DATA_FOLDER}}\" meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]",
                },
            },
        };

        var tables = new JsonArray();
        foreach (var t in schema.Tables)
        {
            string file = t.Name + ".csv";
            var columns = new JsonArray();
            foreach (var c in t.Columns)
                columns.Add(new JsonObject { ["name"] = c.Name, ["dataType"] = c.DataType });

            tables.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["m"] = BuildM(file, t.Columns),
                ["columns"] = columns,
            });
        }
        spec["tables"] = tables;
        return spec.ToJsonString(Cli.Pretty);
    }

    /// <summary>The Power Query expression for one flat CSV table, identical in shape to the Solution
    /// specs: read with Csv.Document, promote headers, then type the columns.</summary>
    private static string BuildM(string csvFile, IReadOnlyList<ColumnSchema> columns)
    {
        var sb = new StringBuilder();
        sb.Append("let Source = Csv.Document(File.Contents(DataFolder & \"/").Append(csvFile)
          .Append("\"),[Delimiter=\",\",Encoding=65001,QuoteStyle=QuoteStyle.Csv]), ");
        sb.Append("Prom = Table.PromoteHeaders(Source,[PromoteAllScalars=true])");

        if (columns.Count > 0)
        {
            sb.Append(", Typed = Table.TransformColumnTypes(Prom,{");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"").Append(EscapeM(columns[i].Name)).Append("\",").Append(MType(columns[i].DataType)).Append('}');
            }
            sb.Append("}) in Typed");
        }
        else
        {
            sb.Append(" in Prom");
        }
        return sb.ToString();
    }

    /// <summary>Map a spec data-type token to the Power Query type literal used in TransformColumnTypes.</summary>
    private static string MType(string? dataType) => (dataType ?? "string").ToLowerInvariant() switch
    {
        "int64" or "int" or "integer" or "whole" => "Int64.Type",
        "double" or "real" or "float" or "number" => "type number",
        "decimal" or "currency" or "fixed" => "Currency.Type",
        "date" => "type date",
        "datetime" or "time" => "type datetime",
        "boolean" or "bool" or "logical" => "type logical",
        _ => "type text",
    };

    /// <summary>Escape a column name for embedding inside an M double-quoted string.</summary>
    private static string EscapeM(string name) => name.Replace("\"", "\"\"");
}
