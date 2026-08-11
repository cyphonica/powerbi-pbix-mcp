using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

[McpServerToolType]
public static class DataTools
{
    [McpServerTool(Name = "list_excel_sheets")]
    [Description("List the worksheet names in an .xlsx/.xlsm file (so you can pick the one to stage).")]
    public static string ListExcelSheets(ExcelService excel,
        [Description("absolute path to the .xlsx/.xlsm")] string xlsxPath)
        => J.Try(() => excel.ListSheets(xlsxPath));

    [McpServerTool(Name = "stage_excel_to_csv")]
    [Description("Stream a large Excel worksheet to a CSV on disk (memory-bounded - does NOT load the workbook into RAM). ALWAYS use this for big Excel files (tens of MB / 100k+ rows) and then load the CSV with Csv.Document(), because Power Query's Excel.Workbook() parses the whole workbook into memory and crashes the mashup engine. Returns row/column counts and the output path.")]
    public static string StageExcelToCsv(ExcelService excel,
        [Description("absolute path to the source .xlsx/.xlsm")] string xlsxPath,
        [Description("worksheet name (omit for the first sheet)")] string? sheetName = null,
        [Description("output .csv path (omit to write next to the source)")] string? outCsvPath = null)
        => J.Try(() => excel.XlsxToCsv(xlsxPath, sheetName, outCsvPath));

    [McpServerTool(Name = "unpivot_weekly_csv")]
    [Description("Stream-unpivot a WIDE CSV (one column per week, e.g. '23/06/2024_SALES','23/06/2024_VOLUME') into a TALL CSV (keys + WeekKey + one column per measure), one row at a time. USE THIS when a CSV has hundreds of period columns and Power Query's Table.UnpivotOtherColumns runs the mashup OUT OF MEMORY on big files - the tall CSV then loads with Csv.Document() and no transform. Optional 2-pass scope filter keeps only rows whose scopeColumn value appears for filterColumn=filterValue (e.g. 'the categories the supplier sells in'). Empty weeks are skipped.")]
    public static string UnpivotWeeklyCsv(ExcelService excel,
        [Description("absolute path to the wide source .csv")] string inCsv,
        [Description("comma-separated key columns to keep, e.g. \"PRODUCT_CODE,STORE_CODE\"")] string keyColumns,
        [Description("measures as suffix:outName, comma-separated, e.g. \"_SALES:Sales,_VOLUME:Volume\"")] string measures,
        [Description("output .csv path (omit to write '<name> (long).csv' next to source)")] string? outCsv = null,
        [Description("optional filter column for the scope pass, e.g. \"Supplier Name\"")] string? filterColumn = null,
        [Description("optional filter value to keep (the value within the filter column)")] string? filterValue = null,
        [Description("optional scope column to keep in-scope rows, e.g. \"National Merchandise Category\"")] string? scopeColumn = null)
        => J.Try(() => excel.UnpivotWeeklyCsv(inCsv, outCsv, keyColumns, measures, filterColumn, filterValue, scopeColumn));
}
