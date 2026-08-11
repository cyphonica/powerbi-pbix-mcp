using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

[McpServerToolType]
public static class SourceTools
{
    [McpServerTool(Name = "unpack_to_source")]
    [Description("Unpack a .pbix into a deterministic, text-only, git-committable source tree: Model/definition (TMDL from the live session model) + Report/ (legacy Layout canonicalised to Layout.json, or the full PBIR definition tree). Same inputs always produce byte-identical output, so the tree diffs cleanly under git. Refuses to overwrite a folder that is not empty or a previous unpack.")]
    public static string UnpackToSource(
        ModelService model,
        [Description("sessionId from connect_model (the live model supplies the TMDL half)")] string sessionId,
        [Description("path to the .pbix (supplies the report half)")] string pbixPath,
        [Description("output folder for the source tree (wiped only if empty or a previous unpack)")] string outputFolder)
        => J.Try(() => model.UnpackToSource(sessionId, pbixPath, outputFolder));

    [McpServerTool(Name = "pbix_diff")]
    [Description("Semantic diff between two .pbix files or two unpacked source trees (from unpack_to_source): files added/removed/changed, TMDL tables + measures (trees only), report pages + visual counts, and Power Query M queries (.pbix only). The 'semantic git diff' for client models - read-only.")]
    public static string PbixDiff(
        ReportService report,
        [Description("side A: a .pbix file or an unpacked source tree folder")] string pathA,
        [Description("side B: a .pbix file or an unpacked source tree folder")] string pathB)
        => J.Try(() => SourceTree.Diff(pathA, pathB, report));
}
