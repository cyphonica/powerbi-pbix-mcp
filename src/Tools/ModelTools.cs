using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

[McpServerToolType]
public static class ModelTools
{
    [McpServerTool(Name = "add_calculated_column")]
    [Description("Add a DAX calculated column to a table.")]
    public static string AddCalculatedColumn(ModelService model, string sessionId, string table, string name,
        [Description("DAX expression for the column")] string dax)
        => J.Try(() => model.AddCalculatedColumn(sessionId, table, name, dax));

    [McpServerTool(Name = "delete_table")]
    [Description("Delete a table from the model (and any relationships that touch it). Use to remove stale/broken tables before a clean rebuild.")]
    public static string DeleteTable(ModelService model, string sessionId, string name)
        => J.Try(() => model.DeleteTable(sessionId, name));

    [McpServerTool(Name = "add_data_column")]
    [Description("Add a data column's metadata to an import (M) table so relationships and bindings resolve. Needed after add_table_from_m because TOM does not auto-infer columns. Refresh afterwards to populate values.")]
    public static string AddDataColumn(ModelService model, string sessionId, string table, string name,
        [Description("String|Int64|Double|DateTime|Boolean|Decimal")] string dataType,
        [Description("source column name in the M output (defaults to name)")] string? sourceColumn = null)
        => J.Try(() => model.AddDataColumn(sessionId, table, name, dataType, sourceColumn));

    [McpServerTool(Name = "add_relationship")]
    [Description("Create a relationship between two columns. Defaults to a star-schema Many->One (from = many/fact side, to = one/dimension side), but the cardinality is overridable to support 1:1, M:1, 1:M and M:M. fromCardinality/toCardinality = One | Many. crossFilteringBehavior = OneDirection | BothDirections | Automatic (overrides the bothDirections flag). securityFilteringBehavior = OneDirection | BothDirections | None. joinOnDateBehavior = DateAndTime | DatePartOnly.")]
    public static string AddRelationship(ModelService model, string sessionId,
        [Description("from (many/fact) side table")] string fromTable,
        [Description("from-side column")] string fromColumn,
        [Description("to (one/dimension) side table")] string toTable,
        [Description("to-side column")] string toColumn,
        [Description("bi-directional cross filter shortcut (default false = single direction). crossFilteringBehavior overrides this.")] bool bothDirections = false,
        [Description("make the relationship active (default true)")] bool active = true,
        [Description("from-side cardinality: One | Many (default Many)")] string? fromCardinality = null,
        [Description("to-side cardinality: One | Many (default One)")] string? toCardinality = null,
        [Description("OneDirection | BothDirections | Automatic (overrides bothDirections)")] string? crossFilteringBehavior = null,
        [Description("OneDirection | BothDirections | None")] string? securityFilteringBehavior = null,
        [Description("DateAndTime | DatePartOnly (for date-based joins)")] string? joinOnDateBehavior = null)
        => J.Try(() => model.AddRelationship(sessionId, fromTable, fromColumn, toTable, toColumn, bothDirections, active,
            fromCardinality, toCardinality, crossFilteringBehavior, securityFilteringBehavior, joinOnDateBehavior));

    [McpServerTool(Name = "update_relationship")]
    [Description("Update an existing relationship's cardinality, filtering behaviour, active flag or join-on-date behaviour. Identify it by name, or by all four of fromTable/fromColumn/toTable/toColumn (order-insensitive). fromCardinality/toCardinality = One | Many (to switch 1:1 / M:1 / 1:M / M:M). crossFilteringBehavior = OneDirection | BothDirections | Automatic. securityFilteringBehavior = OneDirection | BothDirections | None. joinOnDateBehavior = DateAndTime | DatePartOnly. Any omitted property is left unchanged.")]
    public static string UpdateRelationship(ModelService model, string sessionId,
        [Description("relationship name (omit to identify by the column pair)")] string? name = null,
        [Description("from-side table (with the other three for column-pair lookup)")] string? fromTable = null,
        [Description("from-side column")] string? fromColumn = null,
        [Description("to-side table")] string? toTable = null,
        [Description("to-side column")] string? toColumn = null,
        [Description("from-side cardinality: One | Many")] string? fromCardinality = null,
        [Description("to-side cardinality: One | Many")] string? toCardinality = null,
        [Description("OneDirection | BothDirections | Automatic")] string? crossFilteringBehavior = null,
        [Description("OneDirection | BothDirections | None")] string? securityFilteringBehavior = null,
        [Description("set active/inactive")] bool? isActive = null,
        [Description("DateAndTime | DatePartOnly")] string? joinOnDateBehavior = null)
        => J.Try(() => model.UpdateRelationship(sessionId, name, fromTable, fromColumn, toTable, toColumn,
            fromCardinality, toCardinality, crossFilteringBehavior, securityFilteringBehavior, isActive, joinOnDateBehavior));

    [McpServerTool(Name = "delete_relationship")]
    [Description("Delete a relationship. Identify it by name, or by all four of fromTable/fromColumn/toTable/toColumn (order-insensitive).")]
    public static string DeleteRelationship(ModelService model, string sessionId,
        [Description("relationship name (omit to identify by the column pair)")] string? name = null,
        [Description("from-side table")] string? fromTable = null,
        [Description("from-side column")] string? fromColumn = null,
        [Description("to-side table")] string? toTable = null,
        [Description("to-side column")] string? toColumn = null)
        => J.Try(() => model.DeleteRelationship(sessionId, name, fromTable, fromColumn, toTable, toColumn));

    [McpServerTool(Name = "set_partition_m")]
    [Description("Replace a table's Power Query (M) partition expression - e.g. to repoint a table at new source data.")]
    public static string SetPartitionM(ModelService model, string sessionId, string table,
        [Description("the full M let-expression")] string m,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SetPartitionM(sessionId, table, m, partitionName));

    [McpServerTool(Name = "create_csv_table")]
    [Description("Create a fully-typed import table from a (staged) CSV in ONE call: reads the header + samples rows to infer each column's type (Int64/Double/String), builds the Csv.Document M, declares EVERY column automatically, and refreshes. Removes the add_data_column-per-column grind. Pairs with stage_excel_to_csv / unpivot_weekly_csv. Pass pathExpression to control how the M references the file (e.g. 'DataFolder & \"file.csv\"'); omit to embed the absolute path.")]
    public static string CreateCsvTable(ModelService model, string sessionId,
        [Description("new table name")] string table,
        [Description("absolute path to the CSV (read for header + type inference)")] string csvPath,
        [Description("M File.Contents expression, e.g. DataFolder & \"file.csv\" (omit to embed the absolute path)")] string? pathExpression = null,
        [Description("rows to sample for type inference (default 200)")] int sampleRows = 200)
        => J.Try(() => model.CreateCsvTable(sessionId, table, csvPath, pathExpression, sampleRows));

    [McpServerTool(Name = "add_table_from_m")]
    [Description("Create a new import table from a Power Query (M) expression. Run refresh_table afterwards to populate columns.")]
    public static string AddTableFromM(ModelService model, string sessionId, string name,
        [Description("the full M let-expression")] string m)
        => J.Try(() => model.AddTableFromM(sessionId, name, m));

    // ---------------------------------------------------------------- community generators / folding / dataflow

    [McpServerTool(Name = "generate_calendar_table_m")]
    [Description("Generate a full date/calendar table as a new query: List.Dates over [startExpr..endExpr] with ~20 part columns (Year, Quarter, QuarterName, MonthNo, MonthName, MonthShort, YearMonthNo, YearMonth, ISOWeek, ISOYear, WeekdayNo, DayName, DayNo, DayOfYear, StartOfMonth, EndOfMonth, IsWeekend). startExpr/endExpr are RAW M, e.g. \"#date(2020,1,1)\" or a parameter reference. Pass fiscalYearEndMonth (1..12) to add FiscalYear/FiscalMonthNo/FiscalQuarter. Pass locale (e.g. en-NZ) to render month/day names in that culture. Refresh afterwards.")]
    public static string GenerateCalendarTableM(ModelService model, string sessionId, string name,
        [Description("start date as raw M, e.g. #date(2020,1,1)")] string startExpr,
        [Description("end date as raw M, e.g. #date(2025,12,31)")] string endExpr,
        [Description("fiscal year-end month 1..12 (optional; adds fiscal columns)")] int? fiscalYearEndMonth = null,
        [Description("locale/culture for month/day names, e.g. en-NZ (optional)")] string? locale = null)
        => J.Try(() => model.GenerateCalendarTable(sessionId, name, startExpr, endExpr, fiscalYearEndMonth, locale));

    [McpServerTool(Name = "generate_445_calendar_m")]
    [Description("Generate a retail 4-4-5 / 4-5-4 / 5-4-4 calendar table as a new query, one row per week with RetailYear, PeriodOfYear, WeekOfPeriod, WeekOfYear, WeekIndex, WeekStart, WeekEnd. startDate is raw M, e.g. #date(2024,1,29). weeksPattern = 445 | 454 | 544. periodsPerYear = 12 (standard) or 13 (every period 4 weeks). Refresh afterwards.")]
    public static string Generate445CalendarM(ModelService model, string sessionId, string name,
        [Description("retail year start date as raw M, e.g. #date(2024,1,29)")] string startDate,
        [Description("445 | 454 | 544")] int weeksPattern = 445,
        [Description("12 or 13")] int periodsPerYear = 12,
        [Description("number of retail years to generate (default 3)")] int yearsToGenerate = 3)
        => J.Try(() => model.Generate445Calendar(sessionId, name, startDate, weeksPattern, periodsPerYear, yearsToGenerate));

    [McpServerTool(Name = "paginated_rest_source")]
    [Description("Generate a paginated REST source as a new query: a List.Generate page loop that accumulates pages then Table.Combine. mode = offset (uses pageParam+sizeParam, stops on an empty page) or cursor (reads the next token from nextField, stops when null). The base URL stays STATIC and the page key folds through Web.Contents [Query=...]; each page is List.Buffer-ed. dataPath is the JSON field holding the records array (e.g. \"value\" or \"data\"; empty = the body IS the array). Refresh afterwards.")]
    public static string PaginatedRestSource(ModelService model, string sessionId, string name,
        [Description("the static base URL")] string baseUrl,
        [Description("offset | cursor")] string mode = "offset",
        [Description("JSON field holding the records array, e.g. value or data (empty = body is the array)")] string dataPath = "value",
        [Description("offset/page/cursor query parameter name (default offset for offset mode, cursor for cursor mode)")] string? pageParam = null,
        [Description("page-size query parameter name (offset mode, default limit)")] string? sizeParam = null,
        [Description("page size (offset mode, default 100)")] int pageSize = 100,
        [Description("JSON field holding the next cursor/token (REQUIRED for cursor mode)")] string? nextField = null,
        [Description("optional raw M for Table.FromRecords' second argument (column list/type), e.g. type table [Id=Int64.Type]")] string? recordFieldsExpr = null)
        => J.Try(() => model.PaginatedRestSource(sessionId, name, baseUrl, mode, dataPath, pageParam, sizeParam,
            pageSize, nextField, recordFieldsExpr));

    [McpServerTool(Name = "combine_folder_files")]
    [Description("Generate a robust combine-files-from-folder query as a new query: Folder.Files -> filter by extension -> per-file parse -> Table.Combine. Schema-drift safe: it does NOT use Power Query's default 'expand with a sample file' (which silently drops columns only present in later files). fileType = csv | excel. keepFilename adds Source.Name and Source.Folder Path columns; skipErrors wraps each file in try..otherwise so one bad file does not break the refresh. Refresh afterwards.")]
    public static string CombineFolderFiles(ModelService model, string sessionId, string name,
        [Description("the folder path")] string folderPath,
        [Description("csv | excel")] string fileType = "csv",
        [Description("CSV delimiter (default ,)")] string? delimiter = null,
        [Description("rows to skip before headers (default 0)")] int skipRows = 0,
        [Description("promote the first row to headers (default true)")] bool promoteHeaders = true,
        [Description("add Source.Name and Source.Folder Path columns (default false)")] bool keepFilename = false,
        [Description("wrap each file in try..otherwise to skip parse errors (default false)")] bool skipErrors = false)
        => J.Try(() => model.CombineFolderFiles(sessionId, name, folderPath, fileType, delimiter, skipRows,
            promoteHeaders, keepFilename, skipErrors));

    [McpServerTool(Name = "rename_columns_from_mapping")]
    [Description("Rename columns from a CONTROL TABLE: Table.RenameColumns(prev, Table.ToRows(...), MissingField.Ignore). Drives renames from a maintained {oldCol, newCol} mapping query; MissingField.Ignore skips columns that are absent. Appends one step to the table's M query.")]
    public static string RenameColumnsFromMapping(ModelService model, string sessionId, string table,
        [Description("the mapping query/table name")] string mappingTable,
        [Description("the mapping column holding the OLD names")] string oldCol,
        [Description("the mapping column holding the NEW names")] string newCol,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RenameColumnsFromMapping(sessionId, table, mappingTable, oldCol, newCol, partitionName));

    [McpServerTool(Name = "transform_all_column_names")]
    [Description("Bulk-transform every column NAME with Table.TransformColumnNames (schema-agnostic). transform = snakeToSpace | toUpper | toLower | trim | prefix (needs arg) | camelSplit (insert a space before each interior capital). Appends one step to the table's M query.")]
    public static string TransformAllColumnNames(ModelService model, string sessionId, string table,
        [Description("snakeToSpace | toUpper | toLower | trim | prefix | camelSplit")] string transform,
        [Description("the prefix text (only for transform=prefix)")] string? arg = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.TransformAllColumnNames(sessionId, table, transform, arg, partitionName));

    [McpServerTool(Name = "group_keep_all_columns")]
    [Description("Group By keeping ALL columns: Table.Group with {each _} then expand the grouped table over its non-key columns - the workaround for the editor's Group By dropping non-aggregated columns. The expanded set is derived at evaluation time so it survives schema drift. keys is comma-separated. Appends two steps to the table's M query.")]
    public static string GroupKeepAllColumns(ModelService model, string sessionId, string table,
        [Description("the grouping key columns, comma-separated")] string keys,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.GroupKeepAllColumns(sessionId, table, Split(keys), partitionName));

    [McpServerTool(Name = "running_total_m")]
    [Description("Add a FAST running total of valueColumn ordered by orderColumn, optionally restarting within each groupColumn. Uses List.Buffer + a single accumulation pass (O(n)) rather than a per-row re-scan (O(n^2)). Appends sort + index + running-total steps to the table's M query.")]
    public static string RunningTotalM(ModelService model, string sessionId, string table,
        [Description("the value column to accumulate")] string valueColumn,
        [Description("the column that orders the rows")] string orderColumn,
        [Description("the partition column to reset within (optional)")] string? groupColumn = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RunningTotal(sessionId, table, valueColumn, orderColumn, groupColumn, partitionName));

    [McpServerTool(Name = "pivot_text_values")]
    [Description("Pivot TEXT values: Table.Pivot with a Text.Combine aggregation (the default pivot errors on text values). Colliding values in a cell are joined with delimiter (default \", \"). Appends one step to the table's M query.")]
    public static string PivotTextValues(ModelService model, string sessionId, string table,
        [Description("the column whose distinct values become new columns")] string attributeColumn,
        [Description("the text column whose values fill the pivoted cells")] string valueColumn,
        [Description("delimiter joining colliding values (default \", \")")] string? delimiter = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.PivotTextValues(sessionId, table, attributeColumn, valueColumn, delimiter, partitionName));

    [McpServerTool(Name = "unpivot_keep_nulls")]
    [Description("Unpivot keeping NULL rows: replace nulls with a sentinel, UnpivotOtherColumns, then restore the sentinel to null - because plain unpivot silently DROPS null-valued rows. keepColumns (the columns NOT unpivoted) is comma-separated. Appends three steps to the table's M query.")]
    public static string UnpivotKeepNulls(ModelService model, string sessionId, string table,
        [Description("the columns to keep (not unpivot), comma-separated")] string keepColumns,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.UnpivotKeepNulls(sessionId, table, Split(keepColumns), partitionName));

    [McpServerTool(Name = "dynamic_unpivot_other_columns")]
    [Description("Dynamic Unpivot Other Columns: unpivot every column NOT in keepColumns, deriving the unpivot set from Table.ColumnNames at evaluation time so NEW attribute columns added later auto-unpivot. keepColumns is comma-separated. Appends one step to the table's M query.")]
    public static string DynamicUnpivotOtherColumns(ModelService model, string sessionId, string table,
        [Description("the columns to keep (not unpivot), comma-separated")] string keepColumns,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.DynamicUnpivotOtherColumns(sessionId, table, Split(keepColumns), partitionName));

    [McpServerTool(Name = "concatenate_with_group_by")]
    [Description("Concatenate text within groups: Table.Group + Text.Combine (the inverse of split-to-rows). Collapses each group's textColumn values into one delimited string. keys is comma-separated; delimiter defaults to \", \". Appends one step to the table's M query.")]
    public static string ConcatenateWithGroupBy(ModelService model, string sessionId, string table,
        [Description("the grouping key columns, comma-separated")] string keys,
        [Description("the text column to concatenate within each group")] string textColumn,
        [Description("delimiter between values (default \", \")")] string? delimiter = null,
        [Description("output column name (default \"<textColumn> Concatenated\")")] string? outputColumn = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.ConcatenateWithGroupBy(sessionId, table, Split(keys), textColumn, delimiter, outputColumn, partitionName));

    [McpServerTool(Name = "add_table_view_folding")]
    [Description("ADVANCED: scaffold a Table.View that implements custom query folding over a non-foldable source. Emits Table.View(null, [handlers]). handlers is comma-separated from GetType, GetRows, OnTake, OnSkip, OnSelectColumns, OnSelectRows, GetRowCount. GetType+GetRows are the mandatory baseline (added automatically); OnTake is the cheapest win (bounded preview). FLAG: the handler bodies are TEMPLATES that defer to the table and must be specialised to actually fold into the native source.")]
    public static string AddTableViewFolding(ModelService model, string sessionId, string table,
        [Description("handlers, comma-separated, e.g. OnTake,OnSkip,GetRowCount")] string handlers,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.AddTableViewFolding(sessionId, table, Split(handlers), partitionName));

    [McpServerTool(Name = "value_nativequery_folding")]
    [Description("Run a native query against a source and KEEP downstream folding alive: Value.NativeQuery(sourceExpr, nativeQuery, params, [EnableFolding=true]). sourceExpr is raw M (e.g. a Sql.Database(...) reference). paramsExpr is an optional raw M parameter record/list. Appends one step to the table's M query.")]
    public static string ValueNativeQueryFolding(ModelService model, string sessionId, string table,
        [Description("the source as raw M, e.g. Sql.Database(\"srv\",\"db\")")] string sourceExpr,
        [Description("the native (SQL) query text")] string nativeQuery,
        [Description("optional raw M parameter record/list (default null)")] string? paramsExpr = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.ValueNativeQueryFolding(sessionId, table, sourceExpr, nativeQuery, paramsExpr, partitionName));

    [McpServerTool(Name = "set_list_buffer")]
    [Description("Perf heuristic: wrap a referenced list/table in List.Buffer / Table.Buffer to cache it for repeated reads (complements set_query_buffer). kind = list | table. referenceExpr is the raw M reference to buffer. Appends one step to the table's M query.")]
    public static string SetListBuffer(ModelService model, string sessionId, string table,
        [Description("the raw M list/table reference to buffer")] string referenceExpr,
        [Description("list | table (default list)")] string kind = "list",
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SetListBuffer(sessionId, table, referenceExpr, kind, partitionName));

    [McpServerTool(Name = "export_dataflow_modeljson")]
    [Description("Export a Power BI dataflow model.json to disk from a set of entity definitions. entities is a JSON array of {name, m, attributes:[{name,dataType}]} where m is the entity's full M query and dataType = string|int64|double|decimal|date|dateTime|time|boolean|guid. The query mashup is embedded at the root pbi:mashup.document. FLAG: the inner pbi:mashup layout is best-known, NOT verified against a real export.")]
    public static string ExportDataflowModelJson(ModelService model, string sessionId,
        [Description("the dataflow name")] string dataflowName,
        [Description("JSON array of {name, m, attributes:[{name,dataType}]}")] string entities,
        [Description("output file path for model.json")] string outputPath,
        [Description("culture (default en-US)")] string? culture = null)
        => J.Try(() => model.ExportDataflowModelJson(dataflowName, ParseDataflowEntities(entities), culture, outputPath));

    // ---------------------------------------------------------------- discrete Power Query transforms
    // Each appends one mapped step to the target table's M query and persists it. Refresh afterwards.

    [McpServerTool(Name = "merge_queries")]
    [Description("Power Query Merge: join another query into this table on matching key columns, then optionally expand chosen columns. Appends Table.NestedJoin (+ Table.ExpandTableColumn) to the table's M query. leftKeys/rightKeys are comma-separated and must be equal length. joinKind = Inner | LeftOuter | RightOuter | FullOuter | LeftAnti | RightAnti.")]
    public static string MergeQueries(ModelService model, string sessionId, string table,
        [Description("the query/table to merge in (the right side)")] string rightTable,
        [Description("this table's key columns, comma-separated")] string leftKeys,
        [Description("the right table's key columns, comma-separated (same count as leftKeys)")] string rightKeys,
        [Description("Inner | LeftOuter | RightOuter | FullOuter | LeftAnti | RightAnti (default Inner)")] string joinKind = "Inner",
        [Description("columns from the right table to expand, comma-separated (omit to leave the merge column nested)")] string? expandColumns = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.MergeQueries(sessionId, table, rightTable, Split(leftKeys), Split(rightKeys), joinKind,
            expandColumns == null ? null : Split(expandColumns), partitionName));

    [McpServerTool(Name = "append_queries")]
    [Description("Power Query Append: stack the rows of one or more other queries onto this table (union). Appends Table.Combine to the table's M query. otherTables is comma-separated.")]
    public static string AppendQueries(ModelService model, string sessionId, string table,
        [Description("queries/tables to append, comma-separated")] string otherTables,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.AppendQueries(sessionId, table, Split(otherTables), partitionName));

    [McpServerTool(Name = "pivot_column")]
    [Description("Power Query Pivot Column: turn the distinct values of an attribute column into new columns, aggregating a value column. Appends Table.Pivot to the table's M query. aggregation = Sum | Count | Average | Min | Max (default Sum).")]
    public static string PivotColumn(ModelService model, string sessionId, string table,
        [Description("the column whose distinct values become new column headers")] string attributeColumn,
        [Description("the column whose values fill the pivoted cells")] string valueColumn,
        [Description("Sum | Count | Average | Min | Max (default Sum)")] string? aggregation = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.PivotColumn(sessionId, table, attributeColumn, valueColumn, aggregation, partitionName));

    [McpServerTool(Name = "group_by")]
    [Description("Power Query Group By: collapse rows to one per key-column combination with aggregates. Appends Table.Group to the table's M query. keyColumns is comma-separated. aggregations is comma-separated as name:op[:column], e.g. \"Total:Sum:Amount,Lines:Count,Distinct Customers:CountDistinct:CustomerId\". op = Sum | Count | Average | Min | Max | CountDistinct | All (column required except for Count/All).")]
    public static string GroupBy(ModelService model, string sessionId, string table,
        [Description("the grouping key columns, comma-separated")] string keyColumns,
        [Description("aggregations as name:op[:column], comma-separated")] string aggregations,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.GroupBy(sessionId, table, Split(keyColumns), ParseAggregations(aggregations), partitionName));

    [McpServerTool(Name = "split_column")]
    [Description("Power Query Split Column: split a text column into N new columns. Appends Table.SplitColumn (+ the matching Splitter.*) to the table's M query, emitting parts output columns named <col>.1 .. <col>.N. by = delimiter (arg is the delimiter, e.g. \",\"), positions (arg is comma-separated zero-based positions, e.g. \"0,5\"), or lengths (arg is comma-separated repeated lengths, e.g. \"3,3\"). parts is the number of output columns (default 2).")]
    public static string SplitColumn(ModelService model, string sessionId, string table,
        [Description("the column to split")] string column,
        [Description("delimiter | positions | lengths")] string by,
        [Description("the delimiter, the positions, or the lengths (see description)")] string arg,
        [Description("number of output columns to produce (default 2)")] int parts = 2,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SplitColumn(sessionId, table, column, by, arg, parts, partitionName));

    [McpServerTool(Name = "split_column_to_rows")]
    [Description("Power Query Split Column into Rows: split a text column on a delimiter so each part becomes its own row. Appends Table.SplitColumn (into a list) + Table.ExpandListColumn to the table's M query.")]
    public static string SplitColumnToRows(ModelService model, string sessionId, string table,
        [Description("the column to split into rows")] string column,
        [Description("the delimiter to split on, e.g. \",\"")] string delimiter,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SplitColumnToRows(sessionId, table, column, delimiter, partitionName));

    [McpServerTool(Name = "replace_values")]
    [Description("Power Query Replace Values: replace every occurrence of a text value in a column. Appends Table.ReplaceValue (Replacer.ReplaceText) to the table's M query.")]
    public static string ReplaceValues(ModelService model, string sessionId, string table,
        [Description("the column to replace in")] string column,
        [Description("the value to find")] string find,
        [Description("the replacement value")] string replace,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.ReplaceValues(sessionId, table, column, find, replace, partitionName));

    [McpServerTool(Name = "change_column_type")]
    [Description("Power Query Change Type: set the data type of one or more columns. Appends Table.TransformColumnTypes to the table's M query. types is comma-separated as column:type, e.g. \"Amount:number,OrderDate:date,Qty:int\". type = text | int | number | date | datetime | bool | currency. Pass culture (e.g. \"en-US\", \"en-NZ\") to parse text against that locale's date/number conventions - the fix for silent type-conversion corruption on mixed-locale sources.")]
    public static string ChangeColumnType(ModelService model, string sessionId, string table,
        [Description("column:type pairs, comma-separated")] string types,
        [Description("locale/culture for parsing, e.g. en-US (optional)")] string? culture = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.ChangeColumnType(sessionId, table, ParsePairs(types), culture, partitionName));

    [McpServerTool(Name = "detect_column_types")]
    [Description("Power Query Detect Data Type: auto-detect and apply each column's type from its data. Appends a self-contained Table.TransformColumnTypes whose {column, type} pairs are inferred from the first non-null value of every column - no schema needs to be supplied.")]
    public static string DetectColumnTypes(ModelService model, string sessionId, string table,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.DetectColumnTypes(sessionId, table, partitionName));

    [McpServerTool(Name = "filter_rows")]
    [Description("Power Query Filter Rows: keep only the rows matching a condition. Appends Table.SelectRows(prev, each <condition>) to the table's M query. Provide the condition referencing the row as _, e.g. \"[Amount] > 0 and [Region] = \\\"NZ\\\"\" (the 'each' is added if you omit it).")]
    public static string FilterRows(ModelService model, string sessionId, string table,
        [Description("the row condition, e.g. [Amount] > 0")] string mCondition,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.FilterRows(sessionId, table, mCondition, partitionName));

    [McpServerTool(Name = "add_custom_column")]
    [Description("Power Query Add Custom Column: add a column from an M expression. Appends Table.AddColumn(prev, name, each <expr>) to the table's M query. The expression references other columns as [Col], e.g. \"[Qty] * [Price]\".")]
    public static string AddCustomColumn(ModelService model, string sessionId, string table,
        [Description("the new column name")] string name,
        [Description("the M expression, e.g. [Qty] * [Price]")] string mExpression,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.AddCustomColumn(sessionId, table, name, mExpression, partitionName));

    [McpServerTool(Name = "add_index_column")]
    [Description("Power Query Add Index Column: add a sequential index. Appends Table.AddIndexColumn to the table's M query.")]
    public static string AddIndexColumn(ModelService model, string sessionId, string table,
        [Description("the index column name (default Index)")] string? name = null,
        [Description("starting value (default 0)")] int start = 0,
        [Description("increment between rows (default 1)")] int step = 1,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.AddIndexColumn(sessionId, table, name, start, step, partitionName));

    [McpServerTool(Name = "remove_columns")]
    [Description("Power Query Remove Columns: drop columns. Appends Table.RemoveColumns to the table's M query. columns is comma-separated.")]
    public static string RemoveColumns(ModelService model, string sessionId, string table,
        [Description("columns to remove, comma-separated")] string columns,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RemoveColumns(sessionId, table, Split(columns), partitionName));

    [McpServerTool(Name = "rename_columns")]
    [Description("Power Query Rename Columns: rename columns in the query. Appends Table.RenameColumns to the table's M query. renames is comma-separated as from:to, e.g. \"col1:Amount,col2:Region\".")]
    public static string RenameColumns(ModelService model, string sessionId, string table,
        [Description("from:to pairs, comma-separated")] string renames,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RenameColumns(sessionId, table, ParsePairsTuple(renames), partitionName));

    [McpServerTool(Name = "fill_down")]
    [Description("Power Query Fill Down: replace nulls in a column with the most recent non-null value above. Appends Table.FillDown to the table's M query. columns is comma-separated.")]
    public static string FillDown(ModelService model, string sessionId, string table,
        [Description("columns to fill down, comma-separated")] string columns,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.FillDown(sessionId, table, Split(columns), partitionName));

    [McpServerTool(Name = "fill_up")]
    [Description("Power Query Fill Up: replace nulls in a column with the most recent non-null value below. Appends Table.FillUp to the table's M query. columns is comma-separated.")]
    public static string FillUp(ModelService model, string sessionId, string table,
        [Description("columns to fill up, comma-separated")] string columns,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.FillUp(sessionId, table, Split(columns), partitionName));

    [McpServerTool(Name = "remove_duplicates")]
    [Description("Power Query Remove Duplicates: keep one row per distinct combination. Appends Table.Distinct to the table's M query. Pass columns (comma-separated) to dedupe on those columns only; omit to dedupe whole rows.")]
    public static string RemoveDuplicates(ModelService model, string sessionId, string table,
        [Description("columns to dedupe on, comma-separated (omit for whole-row distinct)")] string? columns = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RemoveDuplicates(sessionId, table, columns == null ? null : Split(columns), partitionName));

    [McpServerTool(Name = "promote_headers")]
    [Description("Power Query Use First Row as Headers: promote the first data row to column names. Appends Table.PromoteHeaders to the table's M query.")]
    public static string PromoteHeaders(ModelService model, string sessionId, string table,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.PromoteHeaders(sessionId, table, partitionName));

    [McpServerTool(Name = "transpose")]
    [Description("Power Query Transpose: flip rows into columns and columns into rows. Appends Table.Transpose to the table's M query.")]
    public static string Transpose(ModelService model, string sessionId, string table,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.Transpose(sessionId, table, partitionName));

    // ---------------------------------------------------------------- more discrete Power Query transforms

    [McpServerTool(Name = "unpivot_columns")]
    [Description("Power Query Unpivot Columns: turn the listed columns into Attribute/Value row pairs. Appends Table.Unpivot to the table's M query. columns is comma-separated.")]
    public static string UnpivotColumns(ModelService model, string sessionId, string table,
        [Description("columns to unpivot, comma-separated")] string columns,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.UnpivotColumns(sessionId, table, Split(columns), partitionName));

    [McpServerTool(Name = "unpivot_other_columns")]
    [Description("Power Query Unpivot Other Columns: keep the listed columns and unpivot every OTHER column into Attribute/Value pairs (robust to new columns appearing). Appends Table.UnpivotOtherColumns to the table's M query. keepColumns is comma-separated.")]
    public static string UnpivotOtherColumns(ModelService model, string sessionId, string table,
        [Description("columns to keep (everything else is unpivoted), comma-separated")] string keepColumns,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.UnpivotOtherColumns(sessionId, table, Split(keepColumns), partitionName));

    [McpServerTool(Name = "merge_columns")]
    [Description("Power Query Merge Columns: concatenate two or more columns into one new column with a separator. Appends Table.CombineColumns (+ Combiner.CombineTextByDelimiter) to the table's M query. columns is comma-separated (the source columns are consumed).")]
    public static string MergeColumns(ModelService model, string sessionId, string table,
        [Description("columns to merge, comma-separated (at least two)")] string columns,
        [Description("the separator placed between values, e.g. \" \" or \", \"")] string separator,
        [Description("the new combined column name")] string newColumnName,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.MergeColumns(sessionId, table, Split(columns), separator, newColumnName, partitionName));

    [McpServerTool(Name = "expand_column")]
    [Description("Power Query Expand Column: surface inner fields of a structured column. Appends Table.ExpandRecordColumn / Table.ExpandTableColumn / Table.ExpandListColumn to the table's M query. kind = record | table | list (default record). fields (comma-separated) selects which inner fields to surface for record/table; ignored for list.")]
    public static string ExpandColumn(ModelService model, string sessionId, string table,
        [Description("the structured column to expand")] string column,
        [Description("inner fields to surface, comma-separated (required for record/table)")] string? fields = null,
        [Description("record | table | list (default record)")] string? kind = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.ExpandColumn(sessionId, table, column, fields == null ? null : Split(fields), kind, partitionName));

    [McpServerTool(Name = "keep_top_rows")]
    [Description("Power Query Keep Top Rows: keep only the first N rows. Appends Table.FirstN to the table's M query.")]
    public static string KeepTopRows(ModelService model, string sessionId, string table,
        [Description("number of rows to keep from the top")] int count,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.KeepTopRows(sessionId, table, count, partitionName));

    [McpServerTool(Name = "keep_bottom_rows")]
    [Description("Power Query Keep Bottom Rows: keep only the last N rows. Appends Table.LastN to the table's M query.")]
    public static string KeepBottomRows(ModelService model, string sessionId, string table,
        [Description("number of rows to keep from the bottom")] int count,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.KeepBottomRows(sessionId, table, count, partitionName));

    [McpServerTool(Name = "skip_rows")]
    [Description("Power Query Remove Top Rows: drop the first N rows. Appends Table.Skip to the table's M query.")]
    public static string SkipRows(ModelService model, string sessionId, string table,
        [Description("number of rows to skip from the top")] int count,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SkipRows(sessionId, table, count, partitionName));

    [McpServerTool(Name = "keep_range_rows")]
    [Description("Power Query Keep Range of Rows: skip offset rows then keep count rows. Appends Table.Range to the table's M query.")]
    public static string KeepRangeRows(ModelService model, string sessionId, string table,
        [Description("rows to skip before the range starts")] int offset,
        [Description("number of rows to keep")] int count,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.KeepRangeRows(sessionId, table, offset, count, partitionName));

    [McpServerTool(Name = "sort_rows")]
    [Description("Power Query Sort Rows: sort by one or more columns. Appends Table.Sort to the table's M query. sorts is comma-separated as column[:direction], e.g. \"Region:Ascending,Sales:Descending\". direction = Ascending | Descending (default Ascending).")]
    public static string SortRows(ModelService model, string sessionId, string table,
        [Description("sort keys as column[:direction], comma-separated")] string sorts,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SortRows(sessionId, table, ParseSorts(sorts), partitionName));

    [McpServerTool(Name = "select_columns")]
    [Description("Power Query Choose Columns: keep ONLY the listed columns (in that order) and drop the rest. Appends Table.SelectColumns to the table's M query. columns is comma-separated.")]
    public static string SelectColumns(ModelService model, string sessionId, string table,
        [Description("columns to keep, comma-separated")] string columns,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SelectColumns(sessionId, table, Split(columns), partitionName));

    [McpServerTool(Name = "reorder_columns")]
    [Description("Power Query Reorder Columns: move the listed columns to the front in the given order (remaining columns keep their relative order). Appends Table.ReorderColumns to the table's M query. order is comma-separated.")]
    public static string ReorderColumns(ModelService model, string sessionId, string table,
        [Description("desired leading column order, comma-separated")] string order,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.ReorderColumns(sessionId, table, Split(order), partitionName));

    [McpServerTool(Name = "duplicate_column")]
    [Description("Power Query Duplicate Column: copy a column under a new name. Appends Table.DuplicateColumn to the table's M query.")]
    public static string DuplicateColumn(ModelService model, string sessionId, string table,
        [Description("the column to duplicate")] string column,
        [Description("the new (duplicate) column name")] string newName,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.DuplicateColumn(sessionId, table, column, newName, partitionName));

    [McpServerTool(Name = "transform_column")]
    [Description("Power Query single-column transform: apply a scalar function to a column in place. Appends Table.TransformColumns to the table's M query. operation = (text) upper, lower, trim, clean, proper, length; (number) round, abs, floor, ceiling, sign, sqrt; (date) year, month, day, quarter, weekofyear, dayofweek, startofmonth, endofmonth, startofyear, endofyear, monthname, dayname.")]
    public static string TransformColumn(ModelService model, string sessionId, string table,
        [Description("the column to transform")] string column,
        [Description("the operation (see description for the supported set)")] string operation,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.TransformColumn(sessionId, table, column, operation, partitionName));

    // ---------------------------------------------------------------- folding-control hints

    [McpServerTool(Name = "set_query_buffer")]
    [Description("Power Query folding hint: wrap the table in Table.Buffer so it loads once into memory (stops re-evaluation and downstream folding). Appends Table.Buffer to the table's M query.")]
    public static string SetQueryBuffer(ModelService model, string sessionId, string table,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SetQueryBuffer(sessionId, table, partitionName));

    [McpServerTool(Name = "set_stop_folding")]
    [Description("Power Query folding hint: append Table.StopFolding to prevent the M engine folding any further operations into the data source's native query. Appends Table.StopFolding to the table's M query.")]
    public static string SetStopFolding(ModelService model, string sessionId, string table,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.SetStopFolding(sessionId, table, partitionName));

    // ---------------------------------------------------------------- connector source step

    [McpServerTool(Name = "add_source_step")]
    [Description("Seed or replace a table's data-source step with a generated connector expression (downstream steps preserved). connector = sql | oracle | mysql | postgresql | db2 | web | odata | excel | csv | json | xml | pdf | html | analysisservices | odbc | oledb | folder | sharepoint | azureblob | azuretable | datalake | deltalake | cdm. params is comma-separated key=value pairs; required keys per connector: sql/oracle/mysql/postgresql/db2 -> server, database (optional query); analysisservices -> server, database (optional query = MDX/DAX); web -> url (optional relativePath, query as a raw M record e.g. [#\"$top\"=\"10\"], headers as a raw M record, content for a POST binary, apiKeyName, manualStatusHandling e.g. 404,500); odata -> url (optional filter/select/top/orderby folded into $filter/$select/$top/$orderby); excel/pdf -> path; csv -> path (optional delimiter, columns); json/xml/html -> path OR url; odbc -> connectionString or dsn (optional query); oledb -> connectionString (optional query); folder -> path; sharepoint -> url; azureblob/azuretable -> account; datalake/deltalake/cdm -> url. Static base URL + relativePath/query is the refresh-safe web pattern.")]
    public static string AddSourceStep(ModelService model, string sessionId, string table,
        [Description("the connector kind (see description for the full list)")] string connector,
        [Description("connector parameters as key=value, comma-separated (see description)")] string @params,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.AddSourceStep(sessionId, table, connector, ParseKeyValues(@params), partitionName));

    // ---------------------------------------------------------------- error rows / row trims / value+error replace

    [McpServerTool(Name = "remove_errors")]
    [Description("Power Query Remove Errors: drop rows that carry an error value. Appends Table.RemoveRowsWithErrors to the table's M query. Pass columns (comma-separated) to test only those columns; omit to test the whole row.")]
    public static string RemoveErrors(ModelService model, string sessionId, string table,
        [Description("columns to test for errors, comma-separated (omit for whole row)")] string? columns = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RemoveErrors(sessionId, table, columns == null ? null : Split(columns), partitionName));

    [McpServerTool(Name = "keep_errors")]
    [Description("Power Query Keep Errors: keep ONLY rows that carry an error value (to inspect bad data). Appends Table.SelectRowsWithErrors to the table's M query. Pass columns (comma-separated) to test only those columns; omit for the whole row.")]
    public static string KeepErrors(ModelService model, string sessionId, string table,
        [Description("columns to test for errors, comma-separated (omit for whole row)")] string? columns = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.KeepErrors(sessionId, table, columns == null ? null : Split(columns), partitionName));

    [McpServerTool(Name = "replace_errors")]
    [Description("Power Query Replace Errors: substitute a value for error cells in specific columns. Appends Table.ReplaceErrorValues to the table's M query. replacements is comma-separated as column:value, e.g. \"Amount:0,Region:Unknown\". valueType (text | number | logical | null) applies to all replacement values; for an all-columns replacement, list every column with the same value.")]
    public static string ReplaceErrors(ModelService model, string sessionId, string table,
        [Description("column:value pairs, comma-separated")] string replacements,
        [Description("text | number | logical | null (default text)")] string valueType = "text",
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.ReplaceErrors(sessionId, table, ParseErrorReplacements(replacements, valueType), partitionName));

    [McpServerTool(Name = "remove_blank_rows")]
    [Description("Power Query Remove Blank Rows: drop rows where every field is blank (empty or null). Appends Table.SelectRows with the all-fields-non-blank predicate to the table's M query.")]
    public static string RemoveBlankRows(ModelService model, string sessionId, string table,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RemoveBlankRows(sessionId, table, partitionName));

    [McpServerTool(Name = "remove_bottom_rows")]
    [Description("Power Query Remove Bottom Rows: drop the last N rows. Appends Table.RemoveLastN to the table's M query.")]
    public static string RemoveBottomRows(ModelService model, string sessionId, string table,
        [Description("number of rows to remove from the bottom")] int count,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RemoveBottomRows(sessionId, table, count, partitionName));

    [McpServerTool(Name = "remove_alternate_rows")]
    [Description("Power Query Remove Alternate Rows: keep the first firstKept rows, then repeatedly take and skip in a pattern. Appends Table.AlternateRows to the table's M query.")]
    public static string RemoveAlternateRows(ModelService model, string sessionId, string table,
        [Description("rows kept at the start before the pattern begins")] int firstKept,
        [Description("rows taken (removed) in each cycle")] int taken,
        [Description("rows skipped (kept) in each cycle")] int skipped,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.RemoveAlternateRows(sessionId, table, firstKept, taken, skipped, partitionName));

    [McpServerTool(Name = "replace_value")]
    [Description("Power Query Replace Value (whole-cell): swap the entire cell value in a column - unlike replace_values which replaces a text substring. Appends Table.ReplaceValue (Replacer.ReplaceValue) to the table's M query, supporting null<->value and numeric replacements (e.g. null->0, 0->null, -1->0). valueType (text | number | logical | null) applies to BOTH oldValue and newValue.")]
    public static string ReplaceValue(ModelService model, string sessionId, string table,
        [Description("the column to replace in")] string column,
        [Description("the whole-cell value to find (omit/empty for null)")] string? oldValue = null,
        [Description("the replacement whole-cell value (omit/empty for null)")] string? newValue = null,
        [Description("text | number | logical | null (default text)")] string valueType = "text",
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.ReplaceValue(sessionId, table, column, oldValue, newValue, valueType, partitionName));

    // ---------------------------------------------------------------- demote headers / move column / conditional column

    [McpServerTool(Name = "demote_headers")]
    [Description("Power Query Use Headers as First Row: push the current column names back down into a data row. Appends Table.DemoteHeaders to the table's M query.")]
    public static string DemoteHeaders(ModelService model, string sessionId, string table,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.DemoteHeaders(sessionId, table, partitionName));

    [McpServerTool(Name = "move_column")]
    [Description("Power Query Move Column: reposition a column. Appends Table.ReorderColumns with the computed order to the table's M query. position = start | end | before | after. For before/after, refColumn is required.")]
    public static string MoveColumn(ModelService model, string sessionId, string table,
        [Description("the column to move")] string column,
        [Description("start | end | before | after")] string position,
        [Description("the reference column (required for before/after)")] string? refColumn = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.MoveColumn(sessionId, table, column, position, refColumn, partitionName));

    [McpServerTool(Name = "add_conditional_column")]
    [Description("Power Query Add Conditional Column: add a column from an ordered if/then/else rule chain (the structured Conditional Column builder). Appends Table.AddColumn with a nested if-chain to the table's M query. rules is comma-separated as column:op:value:result, e.g. \"Score:ge:90:A,Score:ge:80:B\". op = eq | ne | gt | ge | lt | le | contains | startswith | endswith. valueType types the compared values; resultType types the results (text | number | logical | null).")]
    public static string AddConditionalColumn(ModelService model, string sessionId, string table,
        [Description("the new column name")] string name,
        [Description("rules as column:op:value:result, comma-separated")] string rules,
        [Description("the else/default result when no rule matches")] string? elseResult = null,
        [Description("value type for comparisons: text | number | logical | null (default text)")] string valueType = "text",
        [Description("result type: text | number | logical | null (default text)")] string resultType = "text",
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.AddConditionalColumn(sessionId, table, name, ParseConditionRules(rules), elseResult,
            valueType, resultType, partitionName));

    // ---------------------------------------------------------------- fuzzy join / group / cluster

    [McpServerTool(Name = "fuzzy_merge")]
    [Description("Power Query Fuzzy Merge: join another query on APPROXIMATE key matches (typos, casing, spacing). Appends Table.FuzzyNestedJoin (+ optional Table.ExpandTableColumn) to the table's M query. leftKeys/rightKeys are comma-separated equal-length lists. joinKind = Inner | LeftOuter | RightOuter | FullOuter | LeftAnti | RightAnti. threshold is the 0..1 similarity cut-off (default 0.8). transformationTable is an optional {From,To} mapping query name.")]
    public static string FuzzyMerge(ModelService model, string sessionId, string table,
        [Description("the query/table to merge in (the right side)")] string rightTable,
        [Description("this table's key columns, comma-separated")] string leftKeys,
        [Description("the right table's key columns, comma-separated (same count as leftKeys)")] string rightKeys,
        [Description("Inner | LeftOuter | RightOuter | FullOuter | LeftAnti | RightAnti (default LeftOuter)")] string joinKind = "LeftOuter",
        [Description("similarity threshold 0..1 (default 0.8)")] double threshold = 0.8,
        [Description("ignore case when matching (optional)")] bool? ignoreCase = null,
        [Description("ignore whitespace when matching (optional)")] bool? ignoreSpace = null,
        [Description("optional {From,To} transformation/mapping query name")] string? transformationTable = null,
        [Description("columns from the right table to expand, comma-separated (omit to leave nested)")] string? expandColumns = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.FuzzyMerge(sessionId, table, rightTable, Split(leftKeys), Split(rightKeys), joinKind,
            threshold, ignoreCase, ignoreSpace, transformationTable,
            expandColumns == null ? null : Split(expandColumns), partitionName));

    [McpServerTool(Name = "fuzzy_group")]
    [Description("Power Query Fuzzy Group By: collapse rows whose key values are APPROXIMATELY equal (typos/casing) into one group with aggregates. Appends Table.FuzzyGroup to the table's M query. keyColumns is comma-separated. aggregations is comma-separated as name:op[:column] (op = Sum | Count | Average | Min | Max | CountDistinct | All). threshold is the optional 0..1 similarity cut-off.")]
    public static string FuzzyGroup(ModelService model, string sessionId, string table,
        [Description("the grouping key columns, comma-separated")] string keyColumns,
        [Description("aggregations as name:op[:column], comma-separated")] string aggregations,
        [Description("similarity threshold 0..1 (optional)")] double? threshold = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.FuzzyGroup(sessionId, table, Split(keyColumns), ParseAggregations(aggregations),
            threshold, partitionName));

    [McpServerTool(Name = "fuzzy_cluster_column")]
    [Description("Power Query Fuzzy Cluster: add a column giving each row the canonical cluster representative of a text column (groups near-identical values, e.g. 'Acme' / 'acme inc' / 'ACME'). Appends Table.AddFuzzyClusterColumn to the table's M query. threshold is the optional 0..1 similarity cut-off.")]
    public static string FuzzyClusterColumn(ModelService model, string sessionId, string table,
        [Description("the text column to cluster")] string column,
        [Description("the new cluster column name")] string newColumn,
        [Description("similarity threshold 0..1 (optional)")] double? threshold = null,
        [Description("partition name (optional; defaults to the first partition)")] string? partitionName = null)
        => J.Try(() => model.FuzzyClusterColumn(sessionId, table, column, newColumn, threshold, partitionName));

    // ---------------------------------------------------------------- M parameter

    [McpServerTool(Name = "add_m_parameter")]
    [Description("Create (or update) a Power Query parameter (the Manage Parameters entry) as a shared expression carrying the IsParameterQuery metadata. type = Text | Number | Logical | DateTime. Pass allowedValues (comma-separated) to restrict the parameter to a fixed list.")]
    public static string AddMParameter(ModelService model, string sessionId,
        [Description("the parameter name")] string name,
        [Description("Text | Number | Logical | DateTime (default Text)")] string type = "Text",
        [Description("the default/current value (parsed per type)")] string? defaultValue = null,
        [Description("allowed values, comma-separated (omit for a free value)")] string? allowedValues = null)
        => J.Try(() => model.AddMParameter(sessionId, name, type, defaultValue,
            allowedValues == null ? null : Split(allowedValues)));

    // ================================================================ Wave P: DAX UDFs + primitives

    [McpServerTool(Name = "define_udf")]
    [Description("Define a DAX User-Defined Function (a net-new model object). The body carries its own typed parameters and return type. params is a comma-separated name:type list (type = Scalar | Table | ColumnRef | MeasureRef | AnyRef | Numeric | String; omit a type for an untyped param). UDFs need compatibility level 1702+, which is auto-bumped if lower. Inspect existing UDFs with list_udfs.")]
    public static string DefineUdf(ModelService model, string sessionId,
        [Description("function name, e.g. AddMargin")] string name,
        [Description("the function BODY DAX (the expression after =>), e.g. amount * (1 - cost)")] string bodyDax,
        [Description("comma-separated name:type params, e.g. amount:Scalar, cost:Scalar (optional)")] string? @params = null,
        [Description("return type: Scalar | Table (optional)")] string? returnType = null,
        [Description("description (becomes the /// doc comment)")] string? description = null)
        => J.Try(() => model.DefineUdf(sessionId, name,
            string.IsNullOrWhiteSpace(@params) ? new List<(string, string)>() : ParsePairs(@params!),
            bodyDax, returnType, description));

    [McpServerTool(Name = "list_udfs")]
    [Description("List the model's DAX User-Defined Functions (name, expression, description).")]
    public static string ListUdfs(ModelService model, string sessionId)
        => J.Try(() => model.ListUdfs(sessionId));

    [McpServerTool(Name = "info_view")]
    [Description("Run EVALUATE INFO.VIEW.<view>() (or INFO.<x>()) and return shaped rows - the model's documentation/lineage surface. view = TABLES | COLUMNS | MEASURES | RELATIONSHIPS | CALCDEPENDENCY | etc.")]
    public static string InfoView(ModelService model, string sessionId,
        [Description("TABLES | COLUMNS | MEASURES | RELATIONSHIPS | CALCDEPENDENCY")] string view,
        [Description("max rows to return (default 1000)")] int maxRows = 1000)
        => J.Try(() => model.InfoView(sessionId, view, maxRows));

    [McpServerTool(Name = "column_statistics")]
    [Description("Run EVALUATE COLUMNSTATISTICS() - per-column Min / Max / Cardinality / MaxLength profiling for the whole model in one call.")]
    public static string ColumnStatistics(ModelService model, string sessionId,
        [Description("max rows to return (default 2000)")] int maxRows = 2000)
        => J.Try(() => model.ColumnStatistics(sessionId, maxRows));

    [McpServerTool(Name = "inject_evaluateandlog")]
    [Description("Wrap a measure's DAX in EVALUATEANDLOG so its value is captured in Server Timings / DAX Studio traces during debugging. Idempotent. Remember to strip_evaluateandlog afterwards.")]
    public static string InjectEvaluateAndLog(ModelService model, string sessionId, string table, string measure,
        [Description("optional log label")] string? label = null)
        => J.Try(() => model.InjectEvaluateAndLog(sessionId, table, measure, label));

    [McpServerTool(Name = "strip_evaluateandlog")]
    [Description("Remove EVALUATEANDLOG wrappers. Pass table+measure for one measure, or omit both to strip every measure in the model (clean-up after debugging).")]
    public static string StripEvaluateAndLog(ModelService model, string sessionId,
        [Description("table (omit to strip the whole model)")] string? table = null,
        [Description("measure (omit to strip the whole model)")] string? measure = null)
        => J.Try(() => model.StripEvaluateAndLog(sessionId, table, measure));

    // ================================================================ Wave P: parameterised generators

    [McpServerTool(Name = "add_time_intelligence_measures")]
    [Description("Generate the full time-intelligence measure set off a base measure: YTD/QTD/MTD, PY/PM, MoM/YoY (+%), PYTD/YOYTD (+%). fiscalYearEnd (MM-DD, e.g. 06-30) drives fiscal *TD.")]
    public static string AddTimeIntelligenceMeasures(ModelService model, string sessionId,
        [Description("home table for the new measures")] string table,
        [Description("base measure name, e.g. Total Sales")] string baseMeasure,
        [Description("date table name")] string dateTable,
        [Description("date column name on the date table")] string dateColumn,
        [Description("fiscal year-end MM-DD (optional, e.g. 06-30)")] string? fiscalYearEnd = null)
        => J.Try(() => model.AddTimeIntelligenceMeasures(sessionId, table, baseMeasure, dateTable, dateColumn, fiscalYearEnd));

    [McpServerTool(Name = "add_running_total")]
    [Description("Generate a running-total measure. Provide dateTable+dateColumn for a date cumulative, OR sortColumn (Table[Column]) for a generic / Pareto running total.")]
    public static string AddRunningTotal(ModelService model, string sessionId,
        [Description("home table")] string table,
        [Description("base measure name")] string baseMeasure,
        [Description("date table (omit when using sortColumn)")] string? dateTable = null,
        [Description("date column (omit when using sortColumn)")] string? dateColumn = null,
        [Description("generic sort column as Table[Column] (alternative to date)")] string? sortColumn = null)
        => J.Try(() => model.AddRunningTotal(sessionId, table, baseMeasure, dateTable, dateColumn, sortColumn));

    [McpServerTool(Name = "add_moving_average")]
    [Description("Generate a rolling moving-average measure over the last N periods. unit = day | month (default day).")]
    public static string AddMovingAverage(ModelService model, string sessionId,
        [Description("home table")] string table,
        [Description("base measure name")] string baseMeasure,
        [Description("date table")] string dateTable,
        [Description("date column")] string dateColumn,
        [Description("number of periods, e.g. 7 or 3")] int periods,
        [Description("day | month (default day)")] string? unit = null)
        => J.Try(() => model.AddMovingAverage(sessionId, table, baseMeasure, dateTable, dateColumn, periods, unit));

    [McpServerTool(Name = "add_percent_of_total")]
    [Description("Generate a percent-of-total measure for a base measure over a dimension. scope = ALL | ALLSELECTED | ALLEXCEPT (default ALLSELECTED).")]
    public static string AddPercentOfTotal(ModelService model, string sessionId,
        [Description("home table")] string table,
        [Description("base measure name")] string baseMeasure,
        [Description("dimension column as Table[Column]")] string dimension,
        [Description("ALL | ALLSELECTED | ALLEXCEPT (default ALLSELECTED)")] string? scope = null)
        => J.Try(() => model.AddPercentOfTotal(sessionId, table, baseMeasure, dimension, scope));

    [McpServerTool(Name = "add_percent_of_parent")]
    [Description("Generate a percent-of-parent measure over a hierarchy. hierarchyColumns is a comma-separated list of Table[Column] from top level to leaf, e.g. Product[Category], Product[Subcategory], Product[Product].")]
    public static string AddPercentOfParent(ModelService model, string sessionId,
        [Description("home table")] string table,
        [Description("base measure name")] string baseMeasure,
        [Description("comma-separated Table[Column] hierarchy levels, top to leaf")] string hierarchyColumns)
        => J.Try(() => model.AddPercentOfParent(sessionId, table, baseMeasure, Split(hierarchyColumns)));

    [McpServerTool(Name = "add_rank_measure")]
    [Description("Generate a RANKX measure with ISINSCOPE + ALLSELECTED + blank guard. order = ASC | DESC (default DESC); ties = SKIP | DENSE (default SKIP). withinGroup (Table[Column]) ranks inside each group.")]
    public static string AddRankMeasure(ModelService model, string sessionId,
        [Description("home table")] string table,
        [Description("base measure name")] string baseMeasure,
        [Description("dimension column as Table[Column]")] string dimension,
        [Description("ASC | DESC (default DESC)")] string order = "DESC",
        [Description("SKIP | DENSE (default SKIP)")] string ties = "SKIP",
        [Description("group column as Table[Column] for within-group ranking (optional)")] string? withinGroup = null)
        => J.Try(() => model.AddRankMeasure(sessionId, table, baseMeasure, dimension, order, ties, withinGroup));

    [McpServerTool(Name = "add_semiadditive_measures")]
    [Description("Generate semi-additive balance measures (opening/closing balance, last/first non-blank) over a value column and date column - the standard inventory / account-balance pattern.")]
    public static string AddSemiAdditiveMeasures(ModelService model, string sessionId,
        [Description("home table")] string table,
        [Description("value column as Table[Column]")] string valueColumn,
        [Description("date column as Table[Column]")] string dateColumn)
        => J.Try(() => model.AddSemiAdditiveMeasures(sessionId, table, valueColumn, dateColumn));

    [McpServerTool(Name = "add_dynamic_segmentation")]
    [Description("Generate a dynamic-segmentation measure that classifies an entity by a measure value against a disconnected band table (left-closed, right-open bounds). Returns the count of entities in the selected segment.")]
    public static string AddDynamicSegmentation(ModelService model, string sessionId,
        [Description("entity / home table")] string entityTable,
        [Description("the measure to classify on, e.g. Total Sales")] string measure,
        [Description("the boundary/band table name")] string boundaryTable,
        [Description("entity grain column as Table[Column], e.g. Customer[CustomerKey]")] string granularityColumn,
        [Description("lower-bound column name on the band table")] string lowerColumn,
        [Description("upper-bound column name on the band table")] string upperColumn,
        [Description("segment-label column name on the band table")] string segmentColumn)
        => J.Try(() => model.AddDynamicSegmentation(sessionId, entityTable, measure, boundaryTable,
            granularityColumn, lowerColumn, upperColumn, segmentColumn));

    [McpServerTool(Name = "add_abc_classification")]
    [Description("Generate ABC (Pareto) classification: a dynamic class measure plus a calculated class table. aThreshold / bThreshold are cumulative-share cut-offs (e.g. 0.7 and 0.9); the rest fall into C.")]
    public static string AddAbcClassification(ModelService model, string sessionId,
        [Description("entity / home table")] string entityTable,
        [Description("entity key column as Table[Column]")] string key,
        [Description("value measure to Pareto on, e.g. Total Sales")] string valueMeasure,
        [Description("A cumulative-share threshold (default 0.7)")] double aThreshold = 0.7,
        [Description("B cumulative-share threshold (default 0.9)")] double bThreshold = 0.9,
        [Description("name for the class table (optional)")] string? classTableName = null)
        => J.Try(() => model.AddAbcClassification(sessionId, entityTable, key, valueMeasure, aThreshold, bThreshold, classTableName));

    [McpServerTool(Name = "add_dynamic_topn")]
    [Description("Generate a dynamic Top N: a what-if N slider, a UNION+Others dimension table, and a rank-or-others measure that buckets everything below rank N into an Others row.")]
    public static string AddDynamicTopN(ModelService model, string sessionId,
        [Description("home table for the measure")] string homeTable,
        [Description("dimension column as Table[Column]")] string dimension,
        [Description("the measure to rank on")] string measure,
        [Description("minimum N (default 1)")] double nMin = 1,
        [Description("maximum N (default 20)")] double nMax = 20,
        [Description("N increment (default 1)")] double nIncrement = 1,
        [Description("default N (optional)")] double? nDefault = null,
        [Description("label for the Others bucket (default Others)")] string? othersLabel = null,
        [Description("name for the Top N dimension table (optional)")] string? topNTableName = null)
        => J.Try(() => model.AddDynamicTopN(sessionId, homeTable, dimension, measure, nMin, nMax, nIncrement, nDefault, othersLabel, topNTableName));

    [McpServerTool(Name = "add_time_intelligence_calc_group")]
    [Description("Create a time-intelligence calculation group on a table: Current/MTD/QTD/YTD/PY/PYTD/YoY/YoY% items with ordinals, a format-string on YoY%, and DiscourageImplicitMeasures set at model level.")]
    public static string AddTimeIntelligenceCalcGroup(ModelService model, string sessionId,
        [Description("the (single-column) table that becomes the calc group")] string table,
        [Description("date table")] string dateTable,
        [Description("date column")] string dateColumn,
        [Description("precedence (optional)")] int? precedence = null)
        => J.Try(() => model.AddTimeIntelligenceCalcGroup(sessionId, table, dateTable, dateColumn, precedence));

    [McpServerTool(Name = "add_currency_conversion_calc_group")]
    [Description("Create a currency-conversion calculation group: an Original Value item and a Converted item that applies the daily exchange rate (SUMX over Date * rate).")]
    public static string AddCurrencyConversionCalcGroup(ModelService model, string sessionId,
        [Description("the (single-column) table that becomes the calc group")] string table,
        [Description("the exchange-rate table name")] string rateTable,
        [Description("the rate column name")] string rateColumn,
        [Description("the currency column name")] string currencyColumn,
        [Description("precedence (optional)")] int? precedence = null)
        => J.Try(() => model.AddCurrencyConversionCalcGroup(sessionId, table, rateTable, rateColumn, currencyColumn, precedence));

    [McpServerTool(Name = "add_dynamic_rls")]
    [Description("Generate dynamic row-level security: create the role (if absent) and set a USERPRINCIPALNAME()-driven filter on the SECURED table. shape = direct | bridge | hierarchy | lookup. Never targets the user/mapping table (refused - it would break the lookup).")]
    public static string AddDynamicRls(ModelService model, string sessionId,
        [Description("role name")] string role,
        [Description("direct | bridge | hierarchy | lookup")] string shape,
        [Description("the fact/dimension table to secure")] string securedTable,
        [Description("the column on the secured table to filter")] string securedColumn,
        [Description("the user/security mapping table")] string userTable,
        [Description("the email/UPN column on the user table")] string userEmailColumn,
        [Description("the value column on the user table that matches the secured column")] string userValueColumn,
        [Description("path column on the user table for the hierarchy shape (optional)")] string? pathColumn = null,
        [Description("model permission for a new role (default Read)")] string? modelPermission = null)
        => J.Try(() => model.AddDynamicRls(sessionId, role, shape, securedTable, securedColumn,
            userTable, userEmailColumn, userValueColumn, pathColumn, modelPermission));

    [McpServerTool(Name = "add_custom_calendar_time_intelligence")]
    [Description("Generate integer-index time intelligence for a custom (445/454/544/weekly/13-period) calendar - the case where built-in TI breaks. Builds *TD/PY/PYTD/prev-week/MAT(364) measures off DayIndex/MonthIndex/Year/PeriodIndex columns on the calendar table. kind = 445 | 454 | 544 | weekly | 13period.")]
    public static string AddCustomCalendarTimeIntelligence(ModelService model, string sessionId,
        [Description("home table for the new measures")] string table,
        [Description("the custom calendar table name")] string calendarTable,
        [Description("445 | 454 | 544 | weekly | 13period")] string kind,
        [Description("base measure name")] string baseMeasure)
        => J.Try(() => model.AddCustomCalendarTimeIntelligence(sessionId, table, calendarTable, kind, baseMeasure));

    // ---- argument parsers for the comma-separated tool inputs -------------------------------------
    private static string[] Split(string s) =>
        (s ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

    private static IReadOnlyList<(string column, string type)> ParsePairs(string s) =>
        Split(s).Select(p =>
        {
            int i = p.IndexOf(':');
            if (i < 0) throw new InvalidOperationException($"Expected column:type, got '{p}'.");
            return (p[..i].Trim(), p[(i + 1)..].Trim());
        }).ToList();

    private static IReadOnlyList<(string from, string to)> ParsePairsTuple(string s) =>
        Split(s).Select(p =>
        {
            int i = p.IndexOf(':');
            if (i < 0) throw new InvalidOperationException($"Expected from:to, got '{p}'.");
            return (p[..i].Trim(), p[(i + 1)..].Trim());
        }).ToList();

    private static IReadOnlyList<(string name, string op, string? column)> ParseAggregations(string s) =>
        Split(s).Select(a =>
        {
            var parts = a.Split(':');
            if (parts.Length < 2) throw new InvalidOperationException($"Expected name:op[:column], got '{a}'.");
            string name = parts[0].Trim();
            string op = parts[1].Trim();
            string? col = parts.Length >= 3 && parts[2].Trim().Length > 0 ? parts[2].Trim() : null;
            return (name, op, col);
        }).ToList();

    private static IReadOnlyList<(string column, string direction)> ParseSorts(string s) =>
        Split(s).Select(p =>
        {
            int i = p.IndexOf(':');
            return i < 0
                ? (p.Trim(), "Ascending")
                : (p[..i].Trim(), p[(i + 1)..].Trim());
        }).ToList();

    // column:value pairs for replace_errors; only the FIRST ':' splits, so a value may contain ':'.
    private static IReadOnlyList<(string column, string? value, string valueType)> ParseErrorReplacements(string s, string valueType) =>
        Split(s).Select(p =>
        {
            int i = p.IndexOf(':');
            if (i < 0) throw new InvalidOperationException($"Expected column:value, got '{p}'.");
            string col = p[..i].Trim();
            string val = p[(i + 1)..].Trim();
            return (col, (string?)val, valueType);
        }).ToList();

    // column:op:value:result rules for add_conditional_column (split on the first three ':').
    private static IReadOnlyList<(string column, string op, string? value, string? result)> ParseConditionRules(string s) =>
        Split(s).Select(r =>
        {
            var parts = r.Split(new[] { ':' }, 4);
            if (parts.Length < 4) throw new InvalidOperationException($"Expected column:op:value:result, got '{r}'.");
            return (parts[0].Trim(), parts[1].Trim(), (string?)parts[2].Trim(), (string?)parts[3].Trim());
        }).ToList();

    // key=value pairs (the source-step connector bag); only the first '=' splits, so values may contain '='.
    private static IReadOnlyDictionary<string, string> ParseKeyValues(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Split(s))
        {
            int i = pair.IndexOf('=');
            if (i < 0) throw new InvalidOperationException($"Expected key=value, got '{pair}'.");
            d[pair[..i].Trim()] = pair[(i + 1)..].Trim();
        }
        return d;
    }

    // dataflow entities: a JSON array of {name, m, attributes:[{name,dataType}]}.
    private static IReadOnlyList<(string name, string mExpression, IReadOnlyList<(string name, string dataType)> attributes)>
        ParseDataflowEntities(string json)
    {
        var arr = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray
                  ?? throw new ArgumentException("entities must be a JSON array of {name, m, attributes:[{name,dataType}]}.");
        var list = new List<(string, string, IReadOnlyList<(string, string)>)>();
        foreach (var n in arr)
        {
            if (n is not System.Text.Json.Nodes.JsonObject o) continue;
            string name = (string?)o["name"] ?? throw new ArgumentException("each entity needs a name.");
            string m = (string?)o["m"] ?? throw new ArgumentException($"entity '{name}' needs an m expression.");
            var attrs = new List<(string, string)>();
            if (o["attributes"] is System.Text.Json.Nodes.JsonArray aa)
                foreach (var an in aa)
                    if (an is System.Text.Json.Nodes.JsonObject ao)
                        attrs.Add(((string?)ao["name"] ?? throw new ArgumentException("each attribute needs a name."),
                                   (string?)ao["dataType"] ?? "string"));
            list.Add((name, m, attrs));
        }
        if (list.Count == 0) throw new ArgumentException("at least one entity is required.");
        return list;
    }

    [McpServerTool(Name = "add_calculated_table")]
    [Description("Create a new calculated table from a DAX table expression (e.g. a Calendar via CALENDAR()).")]
    public static string AddCalculatedTable(ModelService model, string sessionId, string name,
        [Description("DAX table expression")] string dax)
        => J.Try(() => model.AddCalculatedTable(sessionId, name, dax));

    [McpServerTool(Name = "set_shared_expression")]
    [Description("Create or update a shared Power Query expression (a parameter, function, or staging query in the Queries pane).")]
    public static string SetSharedExpression(ModelService model, string sessionId, string name,
        [Description("the M expression")] string m)
        => J.Try(() => model.SetSharedExpression(sessionId, name, m));

    [McpServerTool(Name = "list_m")]
    [Description("List every Power Query (M) partition expression and shared expression in the model.")]
    public static string ListM(ModelService model, string sessionId)
        => J.Try(() => model.ListM(sessionId));

    [McpServerTool(Name = "format_column")]
    [Description("Set a column's format string (e.g. \"#,0\", \"$#,0.00\", \"0.0%\", \"dd mmm yyyy\") and/or data category (e.g. \"City\", \"WebUrl\", \"ImageUrl\").")]
    public static string FormatColumn(ModelService model, string sessionId, string table, string column,
        [Description("format string (omit to keep)")] string? formatString = null,
        [Description("data category (omit to keep)")] string? dataCategory = null)
        => J.Try(() => model.FormatColumn(sessionId, table, column, formatString, dataCategory));

    [McpServerTool(Name = "sort_column_by")]
    [Description("Sort a column by another column (e.g. Month by MonthSort) so visuals order chronologically/logically instead of alphabetically.")]
    public static string SortColumnBy(ModelService model, string sessionId, string table, string column,
        [Description("column to sort by")] string sortByColumn)
        => J.Try(() => model.SortColumnBy(sessionId, table, column, sortByColumn));

    [McpServerTool(Name = "set_column_visibility")]
    [Description("Hide or show a column in the field list. Hidden columns still work in DAX and relationships - use to hide keys/sort columns.")]
    public static string SetColumnVisibility(ModelService model, string sessionId, string table, string column, bool hidden)
        => J.Try(() => model.SetColumnVisibility(sessionId, table, column, hidden));

    [McpServerTool(Name = "set_table_visibility")]
    [Description("Hide or show a whole table in the field list (e.g. hide a bridge/parameter table).")]
    public static string SetTableVisibility(ModelService model, string sessionId, string table, bool hidden)
        => J.Try(() => model.SetTableVisibility(sessionId, table, hidden));

    [McpServerTool(Name = "rename_column")]
    [Description("Rename a column (updates the model object; references by old name in M/DAX are not rewritten).")]
    public static string RenameColumn(ModelService model, string sessionId, string table, string column, string newName)
        => J.Try(() => model.RenameColumn(sessionId, table, column, newName));

    [McpServerTool(Name = "preview_table")]
    [Description("Return the first N rows of a table (TOPN) to inspect the actual data after a refresh.")]
    public static string PreviewTable(ModelService model, string sessionId, string table, int rows = 10)
        => J.Try(() => model.RunDax(sessionId, $"TOPN({rows}, '{table.Replace("'", "''")}')", rows));

    [McpServerTool(Name = "create_date_table")]
    [Description("Create a fully-formed date table in ONE call: Date, Year, Quarter, Month, MonthYear + sort-by columns + a Year>Quarter>Month hierarchy. Then relate your fact's date column to <name>[Date]. Pass dateColumnRef (e.g. \"Fact[Date]\") to size the range to your data, else CALENDARAUTO() is used.")]
    public static string CreateDateTable(ModelService model, string sessionId,
        [Description("table name, e.g. Calendar")] string name = "Calendar",
        [Description("date column to size the range, e.g. Fact_Sales[Week Ending] (optional)")] string? dateColumnRef = null,
        [Description("also build a Year>Quarter>Month hierarchy")] bool hierarchy = true)
        => J.Try(() => model.CreateDateTable(sessionId, name, dateColumnRef, hierarchy));

    [McpServerTool(Name = "add_hierarchy")]
    [Description("Create a drill-down hierarchy on a table from an ordered, comma-separated list of existing columns (e.g. \"Year,Quarter,Month\" or \"Segment,Subsegment,ItemDesc\").")]
    public static string AddHierarchy(ModelService model, string sessionId, string table, string name,
        [Description("ordered column names, comma-separated")] string levels)
        => J.Try(() => model.AddHierarchy(sessionId, table,
            name, levels.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()));

    [McpServerTool(Name = "add_time_intelligence")]
    [Description("Generate the standard time-intelligence measure set for a base measure: YTD, QTD, MTD, PY (prior year), and YoY %. Needs a date table column (e.g. Calendar[Date]).")]
    public static string AddTimeIntelligence(ModelService model, string sessionId,
        [Description("existing base measure, e.g. Total Sales")] string baseMeasure,
        [Description("date column, e.g. Calendar[Date]")] string dateColumn,
        [Description("home table for the new measures (optional; defaults to the base measure's table)")] string? homeTable = null)
        => J.Try(() => model.AddTimeIntelligence(sessionId, baseMeasure, dateColumn, homeTable));

    [McpServerTool(Name = "analyze_model")]
    [Description("Read-only best-practices scan: measures/columns missing format strings, tables in no relationship, etc. Returns a list of findings to fix.")]
    public static string AnalyzeModel(ModelService model, string sessionId)
        => J.Try(() => model.AnalyzeModel(sessionId));

    [McpServerTool(Name = "quality_gate")]
    [Description("PRE-DELIVERY QUALITY GATE: a comprehensive best-practices lint with a pass/review/fail verdict - unformatted measures/columns, tables out of relationships, visible key columns, missing date table, auto-date bloat, inactive relationships, and live relationship-integrity (orphan keys via DAX). Run before shipping a report so nothing goes out broken or untidy.")]
    public static string QualityGate(ModelService model, string sessionId)
        => J.Try(() => model.QualityGate(sessionId));

    [McpServerTool(Name = "run_bpa")]
    [Description("Run the Best Practice Analyzer: a catalogue of ~90 lint rules (merging the Tabular Editor and semantic-link-labs rulesets) across Performance, DAXExpressions, ErrorPrevention, Maintenance, NamingConventions, Formatting, Metadata and RelationshipsLayout. Read-only. Returns every finding (ruleId, category, severity 1 info/2 warning/3 error, objectType, objectName, message, fixable) plus a summary count by category and severity. Filter with categories / severities / ruleIds (comma-separated) and scope (Model|Table|Column|Measure|Relationship|Partition|Hierarchy). Use fix_bpa to auto-remedy fixable findings.")]
    public static string RunBpa(ModelService model, string sessionId,
        [Description("comma-separated category filter, e.g. Performance,Metadata (default all)")] string? categories = null,
        [Description("comma-separated severity filter: 1 info, 2 warning, 3 error (default all)")] string? severities = null,
        [Description("comma-separated rule IDs to run only those (default all)")] string? ruleIds = null,
        [Description("scope filter: Model|Table|Column|Measure|Relationship|Partition|Hierarchy")] string? scope = null)
        => J.Try(() => model.RunBpa(sessionId,
            Split(categories ?? ""),
            Split(severities ?? "").Select(int.Parse).ToArray(),
            Split(ruleIds ?? ""),
            scope));

    [McpServerTool(Name = "fix_bpa")]
    [Description("Apply a BPA rule's autofix - it sets the precise TOM property the rule names (e.g. Column.IsHidden, Column.IsAvailableInMDX, Measure.FormatString, strip EVALUATEANDLOG). With no objectName every matching object is fixed; with objectName only that one. dryRun=true (the default) lists exactly what WOULD change without modifying anything - run again with dryRun=false to apply. Rules with no safe automatic fix (e.g. DAX_USE_DIVIDE, AVOID_FLOATING_POINT_DATA_TYPES) are refused with guidance.")]
    public static string FixBpa(ModelService model, string sessionId,
        [Description("the rule ID to fix, e.g. HIDE_FOREIGN_KEYS")] string ruleId,
        [Description("a single object to fix (e.g. Sales[CustomerKey]); omit to fix all matching objects")] string? objectName = null,
        [Description("preview only (default true); set false to apply")] bool dryRun = true)
        => J.Try(() => model.FixBpa(sessionId, ruleId, objectName, dryRun));

    [McpServerTool(Name = "list_bpa_rules")]
    [Description("List the BPA rule catalogue so you can see coverage: id, category, severity, scope, fixable flag, the exact TOM property each autofix sets, and the description. Optionally filter to one category. Use the IDs with run_bpa (ruleIds) and fix_bpa (ruleId).")]
    public static string ListBpaRules(ModelService model, string sessionId,
        [Description("category filter: Performance|DAXExpressions|ErrorPrevention|Maintenance|NamingConventions|Formatting|Metadata|RelationshipsLayout")] string? category = null)
        => J.Try(() => model.ListBpaRules(category));

    [McpServerTool(Name = "import_bpa_ruleset")]
    [Description("Import a Tabular Editor BPARules.json document and reconcile it with the built-in catalogue: rules whose ID matches a built-in are mapped to our evaluable check; rules whose logic is a Tabular Editor dynamic C#/LINQ expression we cannot safely evaluate are registered as descriptive-only (id/severity/description surfaced) and FLAGGED. The arbitrary TE expression string is never executed.")]
    public static string ImportBpaRuleset(ModelService model, string sessionId,
        [Description("the raw BPARules.json content (a JSON array of rules, or an object with a 'Rules' array)")] string json)
        => J.Try(() => model.ImportBpaRuleset(json));

    [McpServerTool(Name = "export_tmdl")]
    [Description("Serialize the live model to TMDL text files (Microsoft's official serializer) - the model half of a PBIP project, fully text and source-control-friendly. The foundation for generating projects without a live Power BI Desktop.")]
    public static string ExportTmdl(ModelService model, string sessionId,
        [Description("output folder for the .tmdl files (will be created/overwritten)")] string outputFolder)
        => J.Try(() => model.ExportTmdl(sessionId, outputFolder));

    [McpServerTool(Name = "generate_pbip")]
    [Description("Generate a complete text-based PBIP project from the live model + a source pbix's report: <name>.SemanticModel (TMDL via the official serializer) + <name>.Report (legacy report.json + definition.pbir + StaticResources) + <name>.pbip. Pure files - Power BI Desktop / Fabric open it directly, NO live engine needed to author. This is the spine that gets the whole tool off the live-Desktop dependency.")]
    public static string GeneratePbip(ModelService model, string sessionId,
        [Description("source .pbix to take the report (Report/Layout + StaticResources) from")] string sourcePbixPath,
        [Description("output folder for the project (created/overwritten)")] string outputFolder,
        [Description("project name (used for the .pbip / .SemanticModel / .Report folder names)")] string name,
        [Description("also back the model up to cache.abf so the project opens WITH data (no manual refresh). Bigger output. Default false.")] bool includeData = false)
        => J.Try(() => model.GeneratePbip(sessionId, sourcePbixPath, outputFolder, name, includeData));

    [McpServerTool(Name = "audit_robustness")]
    [Description("The complete 'select a value and the visuals fall over' detector - the pre-delivery reliability gate. For every low-cardinality slicer-style column it catches BOTH failure modes: (1) ERROR-on-select - a measure throws in that filter context, breaking the canvas (e.g. 'Brand=a given brand breaks [Margin %]'); and (2) BLANK-on-select - a value that empties every visual because it has no underlying data (the empty-brand class - a member with no rows). Pinpoints the offending values. Run before shipping. Tune with maxColumns/maxValuesPerColumn/maxMeasures; set anchorMeasure to choose the measure used for the blank test (defaults to the first model measure).")]
    public static string AuditRobustness(ModelService model, string sessionId,
        [Description("max slicer-style columns to test (default 20)")] int maxColumns = 0,
        [Description("only test columns with at most this many distinct values (default 200)")] int maxValuesPerColumn = 0,
        [Description("max measures to test (default 40)")] int maxMeasures = 0,
        [Description("measure name used for the blank-on-select test (default: first model measure, e.g. a primary sales measure)")] string? anchorMeasure = null)
        => J.Try(() => model.AuditRobustness(sessionId, maxColumns, maxValuesPerColumn, maxMeasures, anchorMeasure));

    [McpServerTool(Name = "conform_dimension")]
    [Description("Cross-retailer / total-market builder. Two separate fact islands (e.g. a grocery-retailer fact and a second-retailer fact) can't be filtered by one slicer because they share no dimension. This creates a CONFORMED dimension (a calculated table of the distinct union of a column from each side) and relates it to both, so one slicer filters both retailers and combined measures ([Total Market] = [retailer A] + [retailer B]) compute correctly. Auto-materialises + recalcs. Turns two side-by-side islands into a true total-market view (the Phase-2 join).")]
    public static string ConformDimension(ModelService model, string sessionId,
        [Description("name for the new conformed dimension table (e.g. 'Brand (All Retailers)')")] string newTable,
        [Description("the key/column name on the new table (e.g. 'Brand')")] string keyName,
        [Description("first source table (e.g. Dim_Products)")] string table1,
        [Description("column on the first table to union (e.g. Brand)")] string column1,
        [Description("second source table (e.g. Retailer2_Sales)")] string table2,
        [Description("column on the second table to union (e.g. Brand)")] string column2)
        => J.Try(() => model.ConformDimension(sessionId, newTable, keyName, table1, column1, table2, column2));

    [McpServerTool(Name = "find_attribution_gaps")]
    [Description("The 'your report is silently incomplete' detector. A single dimension member with no fact rows can be normal; a WHOLE GROUP of members under one attribute value (an entire supplier, a whole category) with zero data almost never is - it signals a systematic exclusion (mis-attribution / over-scoped fact), the empty-brand / supplier-group class. Finds those clusters automatically - no brand list or domain knowledge needed - so a human can review them. Read-only. The high-value data-integrity guarantee.")]
    public static string FindAttributionGaps(ModelService model, string sessionId,
        [Description("measure used to test data coverage (default: first model measure, e.g. a sales measure)")] string? anchorMeasure = null,
        [Description("max grouping columns to scan (default 24)")] int maxColumns = 0,
        [Description("only scan columns with at most this many distinct values (default 400)")] int maxValuesPerColumn = 0)
        => J.Try(() => model.FindAttributionGaps(sessionId, anchorMeasure, maxColumns, maxValuesPerColumn));

    [McpServerTool(Name = "add_coverage_flag")]
    [Description("Close the loop on blank-on-select (the partner to audit_robustness). Adds a boolean calculated column to a dimension flagging whether each member has rows in a fact table. Filter slicers or add a report-level filter on it = users can no longer select a dead value (e.g. a brand with no sales). Returns how many members have data vs are dead. Idempotent + auto-recalcs.")]
    public static string AddCoverageFlag(ModelService model, string sessionId,
        [Description("the dimension table to add the flag to (e.g. Dim_Products)")] string table,
        [Description("the fact table to test coverage against (e.g. Fact_Sales)")] string factTable,
        [Description("column name (default 'Has <factTable> Data')")] string? columnName = null)
        => J.Try(() => model.AddCoverageFlag(sessionId, table, factTable, columnName));

    [McpServerTool(Name = "sentinel_snapshot")]
    [Description("Sentinel (the trust layer): take a model integrity snapshot - grand total, per-table row counts, per-group totals, and per-measure health - and optionally write it to a file. Take one before and one after a refresh (or any change), then sentinel_diff them to catch a regression - a vanished category, collapsed rows, a newly-broken measure - the instant it happens. CI/observability for BI.")]
    public static string SentinelSnapshot(ModelService model, string sessionId,
        [Description("measure to anchor totals (default: first model measure)")] string? anchorMeasure = null,
        [Description("write the snapshot JSON to this path so you can diff it later")] string? outPath = null,
        [Description("max grouping columns to capture (default 14)")] int maxGroups = 0,
        [Description("only capture columns with at most this many distinct values (default 500)")] int maxValuesPerGroup = 0)
        => J.Try(() => model.SentinelSnapshot(sessionId, anchorMeasure, outPath, maxGroups, maxValuesPerGroup));

    [McpServerTool(Name = "sentinel_diff")]
    [Description("Sentinel: compare two integrity snapshots (before vs after a refresh) and raise ranked alerts on any regression - a whole category dropped to zero, a table's rows collapsed, the grand total fell, a measure started erroring. Explains the likely cause and proposes the fix. status=fail means a report is now WRONG - do not trust the refresh. Pure/headless (two snapshot files).")]
    public static string SentinelDiff(
        [Description("path to the BEFORE snapshot JSON")] string beforePath,
        [Description("path to the AFTER snapshot JSON")] string afterPath)
        => J.Try(() => SuperBiMcp.Sentinel.Diff(System.IO.File.ReadAllText(beforePath), System.IO.File.ReadAllText(afterPath)));

    [McpServerTool(Name = "check_relationships")]
    [Description("Diagnose 'the visual is blank' issues: for every Fact->Dim relationship, count dimension members that have NO matching fact rows (these render BLANK when a user selects them in a slicer - the #1 cause of an apparently-broken visual) and fact keys with no dimension match. Run this whenever a visual is empty but the model 'looks fine'.")]
    public static string CheckRelationships(ModelService model, string sessionId)
        => J.Try(() => model.CheckRelationships(sessionId));

    [McpServerTool(Name = "infer_relationships")]
    [Description("Auto-detect missing relationships from the DATA, not just names: finds columns matching by name + type across tables, then proves each one - which side is the unique key (cardinality + direction) and what fraction of the many-side keys exist on the one side (coverage). Returns ranked proposals with confidence; with autoCreate:true it creates the high-confidence many-to-one matches (as inactive if an active path already exists). Run this instead of guessing add_relationship calls.")]
    public static string InferRelationships(ModelService model, string sessionId,
        [Description("create the high-confidence many-to-one matches automatically (default false = propose only)")] bool autoCreate = false,
        [Description("min fraction of many-side keys that must exist on the one side to be 'high' confidence (default 0.9)")] double minCoverage = 0.9)
        => J.Try(() => model.InferRelationships(sessionId, autoCreate, minCoverage));

    [McpServerTool(Name = "refresh_table")]
    [Description("Refresh one table (or the whole model if table omitted). Required after add_table_from_m / set_partition_m for data and columns to appear.")]
    public static string RefreshTable(ModelService model, string sessionId,
        [Description("table to refresh (omit for whole model)")] string? table = null,
        [Description("full data refresh (true) or calculate-only (false)")] bool full = true)
        => J.Try(() => model.Refresh(sessionId, table, full));

    // ---------------------------------------------------------------- security (RLS / OLS)
    [McpServerTool(Name = "add_role")]
    [Description("Create a security role. modelPermission controls what the role can do model-wide: Read (query only), ReadRefresh (query + refresh), Refresh (refresh only), Administrator (full control), or None. Then attach row-level filters with set_rls and members with add_role_member.")]
    public static string AddRole(ModelService model, string sessionId, string name,
        [Description("Read | ReadRefresh | Refresh | Administrator | None (default Read)")] string modelPermission = "Read")
        => J.Try(() => model.AddRole(sessionId, name, modelPermission));

    [McpServerTool(Name = "delete_role")]
    [Description("Delete a security role (and its row-level/object-level rules and members) from the model.")]
    public static string DeleteRole(ModelService model, string sessionId, string name)
        => J.Try(() => model.DeleteRole(sessionId, name));

    [McpServerTool(Name = "list_roles")]
    [Description("List every security role with its model permission, members, and per-table row-level filters (RLS) and object-level metadata permissions (OLS).")]
    public static string ListRoles(ModelService model, string sessionId)
        => J.Try(() => model.ListRoles(sessionId));

    [McpServerTool(Name = "set_rls")]
    [Description("Set or update the row-level security (RLS) filter for a role on a table. The DAX boolean expression keeps only the rows it returns true for, e.g. \"[Region] = USERPRINCIPALNAME()\" or \"'Sales'[Country] = \\\"NZ\\\"\". Creates the table permission if absent.")]
    public static string SetRls(ModelService model, string sessionId,
        [Description("role name (create with add_role first)")] string role,
        [Description("table the filter applies to")] string table,
        [Description("DAX boolean filter expression")] string daxFilterExpression)
        => J.Try(() => model.SetRls(sessionId, role, table, daxFilterExpression));

    [McpServerTool(Name = "add_role_member")]
    [Description("Add a member to a role. By default a Windows member (e.g. DOMAIN\\\\user or user@org.com); pass provider (e.g. AzureAD) to add an external/cloud identity member instead.")]
    public static string AddRoleMember(ModelService model, string sessionId,
        [Description("role name")] string role,
        [Description("member identity, e.g. user@org.com or DOMAIN\\\\user")] string memberName,
        [Description("external identity provider (e.g. AzureAD); omit for a Windows member")] string? provider = null)
        => J.Try(() => model.AddRoleMember(sessionId, role, memberName, provider));

    [McpServerTool(Name = "set_table_ols")]
    [Description("Object-level security: set whether a role can SEE a whole table. permission = None (table hidden + inaccessible to the role), Read (visible), or Default (inherit). Creates the table permission if absent.")]
    public static string SetTableOls(ModelService model, string sessionId,
        [Description("role name")] string role,
        [Description("table to secure")] string table,
        [Description("Default | None | Read")] string permission)
        => J.Try(() => model.SetTableOls(sessionId, role, table, permission));

    [McpServerTool(Name = "set_column_ols")]
    [Description("Object-level security: set whether a role can SEE a column. permission = None (column hidden + inaccessible to the role), Read (visible), or Default (inherit).")]
    public static string SetColumnOls(ModelService model, string sessionId,
        [Description("role name")] string role,
        [Description("table the column is on")] string table,
        [Description("column to secure")] string column,
        [Description("Default | None | Read")] string permission)
        => J.Try(() => model.SetColumnOls(sessionId, role, table, column, permission));

    // ---------------------------------------------------------------- calculation groups
    [McpServerTool(Name = "add_calculation_group")]
    [Description("Turn a (single-column) table into a calculation group - the engine for reusable selectors like Time Intelligence (Current/YTD/PY/YoY) applied to ANY measure. After this, add items with add_calculation_item. Lower precedence is applied first when groups are nested.")]
    public static string AddCalculationGroup(ModelService model, string sessionId,
        [Description("the table to convert into a calculation group")] string table,
        [Description("precedence when multiple groups exist (higher applies last; optional)")] int? precedence = null)
        => J.Try(() => model.AddCalculationGroup(sessionId, table, precedence));

    [McpServerTool(Name = "add_calculation_item")]
    [Description("Add a calculation item to a calculation group. The DAX usually wraps SELECTEDMEASURE(), e.g. \"CALCULATE(SELECTEDMEASURE(), DATESYTD('Calendar'[Date]))\" for a YTD item. ordinal sets display order; formatStringExpression sets a dynamic format for that item.")]
    public static string AddCalculationItem(ModelService model, string sessionId,
        [Description("the calculation-group table")] string table,
        [Description("item name, e.g. YTD")] string name,
        [Description("DAX expression, usually over SELECTEDMEASURE()")] string daxExpression,
        [Description("display order (optional)")] int? ordinal = null,
        [Description("DAX dynamic format-string expression for this item (optional)")] string? formatStringExpression = null)
        => J.Try(() => model.AddCalculationItem(sessionId, table, name, daxExpression, ordinal, formatStringExpression));

    // ---------------------------------------------------------------- KPI / detail rows / dynamic format
    [McpServerTool(Name = "set_kpi")]
    [Description("Attach a KPI to a measure: a target to compare against and a status expression (typically returns -1/0/1 for bad/neutral/good) that drives a traffic-light indicator. statusGraphic picks the indicator set (default 'Three Circles Colored').")]
    public static string SetKpi(ModelService model, string sessionId,
        [Description("table the measure is on")] string table,
        [Description("measure to attach the KPI to")] string measure,
        [Description("DAX target expression, e.g. [Sales Target]")] string targetExpression,
        [Description("DAX status expression returning e.g. -1/0/1")] string statusExpression,
        [Description("indicator graphic set (optional; default 'Three Circles Colored')")] string? statusGraphic = null)
        => J.Try(() => model.SetKpi(sessionId, table, measure, targetExpression, statusExpression, statusGraphic));

    [McpServerTool(Name = "set_detail_rows")]
    [Description("Define drillthrough detail rows - the DAX table returned when a user drills into a measure value (or the table's default). Set measure for a per-measure definition, or omit it to set the table's default detail rows. daxTableExpression is a DAX table expression, e.g. \"SELECTCOLUMNS('Sales', ...)\".")]
    public static string SetDetailRows(ModelService model, string sessionId,
        [Description("table (the measure's table, or the table whose default rows you're setting)")] string table,
        [Description("measure name for a per-measure definition; omit for the table default")] string? measure,
        [Description("DAX table expression returning the detail rows")] string daxTableExpression)
        => J.Try(() => model.SetDetailRows(sessionId, table, measure, daxTableExpression));

    [McpServerTool(Name = "set_dynamic_format_string")]
    [Description("Set a dynamic (DAX-driven) format string on a measure OR a calculation item, so the displayed format changes with context (e.g. currency vs percent, or scaling units). Provide measure for a measure, or calculationItem (with its calculation-group table) for an item.")]
    public static string SetDynamicFormatString(ModelService model, string sessionId,
        [Description("table (the measure's table, or the calculation-group table)")] string table,
        [Description("measure name; omit if targeting a calculation item")] string? measure,
        [Description("calculation-item name; omit if targeting a measure")] string? calculationItem,
        [Description("DAX expression returning the format string")] string daxExpression)
        => J.Try(() => model.SetDynamicFormatString(sessionId, table, measure, calculationItem, daxExpression));

    // ---------------------------------------------------------------- parameters (field / what-if)
    [McpServerTool(Name = "add_field_parameter")]
    [Description("Create a FIELD PARAMETER - a small calculated table that lets a user swap which field a visual shows. Pass the fields as Table[Column] references; the first generated column drops onto a slicer to switch between them. Builds the {(\"Display\", NAMEOF('T'[Col]), order), ...} table with the parameter metadata the report layer recognises.")]
    public static string AddFieldParameter(ModelService model, string sessionId,
        [Description("name for the field-parameter table, e.g. 'Measure Selector'")] string name,
        [Description("fields to switch between, comma-separated Table[Column] refs, e.g. \"Sales[Amount],Sales[Qty]\"")] string fields)
        => J.Try(() => model.AddFieldParameter(sessionId, name,
            fields.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()));

    [McpServerTool(Name = "add_whatif_parameter")]
    [Description("Create a WHAT-IF parameter: a disconnected GENERATESERIES calculated table plus a SELECTEDVALUE measure ([<name> Value]) that picks up the slider selection. Drop the table's column on a slider slicer and reference the value measure in your DAX. The standard Modeling > New parameter pattern.")]
    public static string AddWhatIfParameter(ModelService model, string sessionId, string name,
        [Description("minimum value")] double min,
        [Description("maximum value")] double max,
        [Description("step / increment between values")] double increment,
        [Description("default value the measure returns when nothing is selected (optional; defaults to min)")] double? defaultValue = null)
        => J.Try(() => model.AddWhatIfParameter(sessionId, name, min, max, increment, defaultValue));

    // ---------------------------------------------------------------- perspectives
    [McpServerTool(Name = "add_perspective")]
    [Description("Create a perspective - a named, focused view that shows only a chosen subset of tables/columns/measures (e.g. a 'Finance' or 'Sales' lens over a large model). Add members with add_to_perspective.")]
    public static string AddPerspective(ModelService model, string sessionId, string name)
        => J.Try(() => model.AddPerspective(sessionId, name));

    [McpServerTool(Name = "add_to_perspective")]
    [Description("Add a table - or a specific column / measure / hierarchy on it - to a perspective. Omit childObject to include the whole table. Run add_perspective first.")]
    public static string AddToPerspective(ModelService model, string sessionId,
        [Description("perspective name")] string perspective,
        [Description("table to include")] string table,
        [Description("a column, measure or hierarchy on the table; omit to include the whole table")] string? childObject = null)
        => J.Try(() => model.AddToPerspective(sessionId, perspective, table, childObject));

    // ---------------------------------------------------------------- cultures / translations
    [McpServerTool(Name = "add_culture")]
    [Description("Add a culture (locale, e.g. fr-FR or mi-NZ) so the model can carry translated captions, descriptions and display folders for that language. Add the actual translations with set_translation.")]
    public static string AddCulture(ModelService model, string sessionId,
        [Description("locale code, e.g. fr-FR, de-DE, mi-NZ")] string locale)
        => J.Try(() => model.AddCulture(sessionId, locale));

    [McpServerTool(Name = "set_translation")]
    [Description("Set a translated Caption, Description or DisplayFolder for a model object in a culture. objectType = table | column | measure | hierarchy | model. For a column/measure/hierarchy, also pass its table. Run add_culture first.")]
    public static string SetTranslation(ModelService model, string sessionId,
        [Description("culture/locale, e.g. fr-FR")] string culture,
        [Description("table | column | measure | hierarchy | model")] string objectType,
        [Description("the object's name (the model name for objectType=model)")] string objectName,
        [Description("Caption | Description | DisplayFolder")] string property,
        [Description("the translated text")] string value,
        [Description("the object's table (required for column/measure/hierarchy)")] string? table = null)
        => J.Try(() => model.SetTranslation(sessionId, culture, objectType, objectName, property, value, table));

    // ---------------------------------------------------------------- aggregations
    [McpServerTool(Name = "set_aggregation")]
    [Description("Define a user-defined AGGREGATION: mark a column on a (typically pre-aggregated) detail table as the aggregate of a base column or table, so the engine can transparently answer queries from the smaller table. summarization = GroupBy | Sum | Count | Min | Max. For GroupBy, baseColumnOrTable is a Table[Column]; for the aggregate functions it's the base table name (or a Table[Column]).")]
    public static string SetAggregation(ModelService model, string sessionId,
        [Description("the detail/aggregation table the column is on")] string table,
        [Description("the column being mapped")] string detailColumn,
        [Description("the base column (Table[Column]) for GroupBy, or the base table name for Sum/Count/Min/Max")] string baseColumnOrTable,
        [Description("GroupBy | Sum | Count | Min | Max")] string summarization)
        => J.Try(() => model.SetAggregation(sessionId, detailColumn, baseColumnOrTable, summarization, table));

    // ---------------------------------------------------------------- incremental refresh
    [McpServerTool(Name = "set_incremental_refresh")]
    [Description("Attach a basic incremental-refresh policy to a table: keep a rolling window of data and only refresh the most recent increment. Needs RangeStart/RangeEnd M parameters - this creates them if absent (then point the table's M partition at them to bind the date range). Granularity = Day | Month | Quarter | Year.")]
    public static string SetIncrementalRefresh(ModelService model, string sessionId, string table,
        [Description("rolling-window granularity: Day | Month | Quarter | Year")] string rollingWindowGranularity,
        [Description("how many rolling-window periods of history to keep")] int rollingWindowPeriods,
        [Description("incremental granularity: Day | Month | Quarter | Year")] string incrementalGranularity,
        [Description("how many recent incremental periods to refresh each run")] int incrementalPeriods,
        [Description("optional DAX polling expression to detect changes (real-time / hybrid)")] string? pollingExpression = null)
        => J.Try(() => model.SetIncrementalRefresh(sessionId, table, rollingWindowGranularity, rollingWindowPeriods,
            incrementalGranularity, incrementalPeriods, pollingExpression));

    // ---------------------------------------------------------------- variations (date navigation)
    [McpServerTool(Name = "add_variation")]
    [Description("Add a date-navigation Variation to a column: when the column is used in a visual, the model navigates through the named relationship to a default hierarchy on the related (date) table. isDefault makes it the column's default variation.")]
    public static string AddVariation(ModelService model, string sessionId,
        [Description("table the column is on")] string table,
        [Description("the column to add the variation to")] string column,
        [Description("the relationship name to navigate through")] string relationship,
        [Description("the default hierarchy on the related table")] string defaultHierarchy,
        [Description("make this the column's default variation (default true)")] bool isDefault = true)
        => J.Try(() => model.AddVariation(sessionId, table, column, relationship, defaultHierarchy, isDefault));

    // ---------------------------------------------------------------- column data category
    [McpServerTool(Name = "set_column_data_category")]
    [Description("Set a column's data category so the report layer treats it specially, e.g. Address, City, Continent, Country, County, Latitude, Longitude, Place, PostalCode, StateOrProvince, WebUrl, ImageUrl, BarcodeText. (For format + category in one call use format_column.)")]
    public static string SetColumnDataCategory(ModelService model, string sessionId, string table, string column,
        [Description("category, e.g. City | Latitude | WebUrl | ImageUrl | BarcodeText")] string category)
        => J.Try(() => model.SetColumnDataCategory(sessionId, table, column, category));

    // ---------------------------------------------------------------- display folders
    [McpServerTool(Name = "set_display_folder")]
    [Description("Set the display folder for a measure or a column - groups fields into folders in the field list (e.g. 'Key Measures', 'Time Intelligence'). Use a backslash for nested folders, e.g. 'Sales\\\\Margins'.")]
    public static string SetDisplayFolder(ModelService model, string sessionId,
        [Description("the measure or column name")] string target,
        [Description("folder path (empty string clears it)")] string folder,
        [Description("the table the measure/column lives on")] string table)
        => J.Try(() => model.SetDisplayFolder(sessionId, target, folder, table));

    // ---------------------------------------------------------------- mark as date table
    [McpServerTool(Name = "mark_as_date_table")]
    [Description("Mark a table as the model's date table (the TOM equivalent of 'Mark as date table'): sets the table's data category to Time and flags the given DateTime column as the date key. Use this so built-in time intelligence works reliably against your own calendar table.")]
    public static string MarkAsDateTable(ModelService model, string sessionId, string table,
        [Description("the DateTime date column to use as the date key")] string dateColumn)
        => J.Try(() => model.MarkAsDateTable(sessionId, table, dateColumn));

    // ---------------------------------------------------------------- query groups
    [McpServerTool(Name = "add_query_group")]
    [Description("Create a query group - a display folder for queries (shared expressions / partitions) in the Power Query Queries pane, e.g. 'Staging' or 'Parameters'. Assign objects to it with set_object_query_group.")]
    public static string AddQueryGroup(ModelService model, string sessionId,
        [Description("folder name/path, e.g. 'Staging' or 'Data\\\\Raw'")] string folder)
        => J.Try(() => model.AddQueryGroup(sessionId, folder));

    [McpServerTool(Name = "set_object_query_group")]
    [Description("Put a shared expression or a table partition into a query group (display folder). objectType = expression | partition. For a partition, name is the table name (uses its first partition) or 'Table/Partition'. Creates the query group if it does not exist.")]
    public static string SetObjectQueryGroup(ModelService model, string sessionId,
        [Description("expression | partition")] string objectType,
        [Description("the shared-expression name, or 'Table' / 'Table/Partition'")] string name,
        [Description("the query group folder to put it in")] string queryGroupFolder)
        => J.Try(() => model.SetObjectQueryGroup(sessionId, objectType, name, queryGroupFolder));

    // ---------------------------------------------------------------- Q&A synonyms (linguistic schema)
    [McpServerTool(Name = "set_synonyms")]
    [Description("Add Q&A natural-language synonyms for a model object (so 'revenue'/'turnover' map to a 'Sales' measure). objectType = table | column | measure | hierarchy; pass the object's table for column/measure/hierarchy. synonyms is a comma-separated list. culture defaults to the model culture. NOTE: the Q&A linguistic schema is large and complex - this authors a flat per-entity synonyms list and merges into any existing schema; richer phrasings/relationships need a hand-authored schema.")]
    public static string SetSynonyms(ModelService model, string sessionId,
        [Description("table | column | measure | hierarchy")] string objectType,
        [Description("the object's name")] string objectName,
        [Description("synonyms, comma-separated, e.g. \"revenue,turnover,takings\"")] string synonyms,
        [Description("culture/locale (optional; defaults to the model culture, e.g. en-US)")] string? culture = null,
        [Description("the object's table (required for column/measure/hierarchy)")] string? table = null)
        => J.Try(() => model.SetSynonyms(sessionId, objectType, objectName,
            synonyms.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray(), culture, table));

    // ---------------------------------------------------------------- table-level detail rows
    [McpServerTool(Name = "set_table_detail_rows")]
    [Description("Set a TABLE's default detail rows (Table.DefaultDetailRowsDefinition) - the DAX table returned when a user drills into a value of this table that has no per-measure detail rows. Distinct from the measure-level set_detail_rows. daxTableExpression is a DAX table expression.")]
    public static string SetTableDetailRows(ModelService model, string sessionId, string table,
        [Description("DAX table expression returning the detail rows")] string daxTableExpression)
        => J.Try(() => model.SetTableDetailRows(sessionId, table, daxTableExpression));

    // ---------------------------------------------------------------- calc-group precedence / item ordinal
    [McpServerTool(Name = "set_calc_group_precedence")]
    [Description("Set a calculation group's Precedence - when multiple calculation groups apply, the HIGHER precedence is evaluated last (outermost). Run on a table that is already a calculation group.")]
    public static string SetCalcGroupPrecedence(ModelService model, string sessionId,
        [Description("the calculation-group table")] string table,
        [Description("precedence value (higher applies last)")] int precedence)
        => J.Try(() => model.SetCalcGroupPrecedence(sessionId, table, precedence));

    [McpServerTool(Name = "set_calc_item_ordinal")]
    [Description("Set a calculation item's Ordinal - its display/sort order within the calculation group (lower shows first).")]
    public static string SetCalcItemOrdinal(ModelService model, string sessionId,
        [Description("the calculation-group table")] string table,
        [Description("the calculation item name")] string itemName,
        [Description("display order ordinal (lower first)")] int ordinal)
        => J.Try(() => model.SetCalcItemOrdinal(sessionId, table, itemName, ordinal));

    // ---------------------------------------------------------------- partition mode / data coverage
    [McpServerTool(Name = "set_partition_mode")]
    [Description("Set a partition's storage mode: Import | DirectQuery | Dual | DirectLake. Defaults to the table's first partition. Use Dual + a DirectQuery partition with set_data_coverage to build a hybrid (Import + DirectQuery) table. (Switching to DirectLake/DirectQuery may also need the matching partition source kind; this flips the Mode flag.)")]
    public static string SetPartitionMode(ModelService model, string sessionId, string table,
        [Description("Import | DirectQuery | Dual | DirectLake")] string mode,
        [Description("partition name (optional; defaults to the first partition)")] string? partition = null)
        => J.Try(() => model.SetPartitionMode(sessionId, table, mode, partition));

    [McpServerTool(Name = "set_data_coverage")]
    [Description("Set a partition's data coverage definition (DataCoverageDefinition) - a DAX boolean telling the engine which data a DirectQuery partition covers, so a hybrid Import+DirectQuery table can answer in-range queries from the cheaper imported partition. Defaults to the table's first partition.")]
    public static string SetDataCoverage(ModelService model, string sessionId, string table,
        [Description("DAX boolean expression describing the partition's coverage, e.g. \"'Sales'[OrderDate] >= DATE(2024,1,1)\"")] string daxExpression,
        [Description("partition name (optional; defaults to the first partition)")] string? partition = null)
        => J.Try(() => model.SetDataCoverage(sessionId, table, daxExpression, partition));

    // ---------------------------------------------------------------- annotations / extended properties
    [McpServerTool(Name = "set_annotation")]
    [Description("Set a name/value annotation on any model object: objectType = model | table | column | measure | hierarchy | partition. Pass the object's table for column/measure/hierarchy/partition. Replaces a same-named annotation. Annotations are free-form metadata that survive serialization (used by tooling like Tabular Editor).")]
    public static string SetAnnotation(ModelService model, string sessionId,
        [Description("model | table | column | measure | hierarchy | partition")] string objectType,
        [Description("the object's name (the model name for objectType=model)")] string objectName,
        [Description("annotation name")] string name,
        [Description("annotation value")] string value,
        [Description("the object's table (required for column/measure/hierarchy/partition)")] string? table = null)
        => J.Try(() => model.SetAnnotation(sessionId, objectType, objectName, name, value, table));

    [McpServerTool(Name = "set_extended_property")]
    [Description("Set a typed extended property on any model object: type = String | Json. objectType = model | table | column | measure | hierarchy | partition. Pass the object's table for column/measure/hierarchy/partition. Replaces a same-named property. Extended properties are structured metadata (e.g. the ParameterMetadata tag on field-parameter columns).")]
    public static string SetExtendedProperty(ModelService model, string sessionId,
        [Description("model | table | column | measure | hierarchy | partition")] string objectType,
        [Description("the object's name")] string objectName,
        [Description("property name")] string name,
        [Description("property value (a JSON string when type=Json)")] string value,
        [Description("String | Json (default String)")] string type = "String",
        [Description("the object's table (required for column/measure/hierarchy/partition)")] string? table = null)
        => J.Try(() => model.SetExtendedProperty(sessionId, objectType, objectName, name, value, type, table));

    // ---------------------------------------------------------------- TMDL import
    [McpServerTool(Name = "import_tmdl")]
    [Description("Import a TMDL folder (the official deserializer) into the live model. DEFAULTS TO A DRY RUN that returns the table/measure diff and changes NOTHING. Applying TMDL REPLACES the live model metadata (createOrReplace semantics), so the actual apply is gated behind applyToLiveModel:true. The folder may be a model TMDL folder or a PBIP SemanticModel (the nested 'definition' subfolder is auto-detected).")]
    public static string ImportTmdl(ModelService model, string sessionId,
        [Description("path to the TMDL folder (or a PBIP .SemanticModel folder)")] string folderPath,
        [Description("APPLY the TMDL to the live model (createOrReplace). Default false = dry-run diff only.")] bool applyToLiveModel = false)
        => J.Try(() => model.ImportTmdl(sessionId, folderPath, applyToLiveModel));

    // ---------------------------------------------------------------- model health
    [McpServerTool(Name = "model_health")]
    [Description("Model-health report: object counts (tables/columns/measures/relationships) plus VertiPaq per-column storage (size + cardinality) read from the storage DMVs through the model connection - surfaces the largest columns so you can find bloat / unused high-cardinality columns. Read-only.")]
    public static string ModelHealth(ModelService model, string sessionId)
        => J.Try(() => model.ModelHealth(sessionId));

    // ================================================================ audited TOM coverage gaps

    // ---------------------------------------------------------------- column properties
    [McpServerTool(Name = "set_summarize_by")]
    [Description("Set a column's default summarization (the implicit aggregation used when the column is dropped on a visual). summarizeBy = Default | None | Sum | Min | Max | Count | Average | DistinctCount. Set None on key/ID columns so they are not silently summed.")]
    public static string SetSummarizeBy(ModelService model, string sessionId, string table, string column,
        [Description("Default | None | Sum | Min | Max | Count | Average | DistinctCount")] string summarizeBy)
        => J.Try(() => model.SetSummarizeBy(sessionId, table, column, summarizeBy));

    [McpServerTool(Name = "set_column_data_type")]
    [Description("Set a column's data type. dataType = String | Int64 | Double | Decimal | DateTime | Boolean | Binary. A refresh is required for the change to take effect on imported data.")]
    public static string SetColumnDataType(ModelService model, string sessionId, string table, string column,
        [Description("String | Int64 | Double | Decimal | DateTime | Boolean | Binary")] string dataType)
        => J.Try(() => model.SetColumnDataType(sessionId, table, column, dataType));

    [McpServerTool(Name = "set_column_flags")]
    [Description("Set a column's modelling flags (any subset; omitted flags are left unchanged): isKey (the table's key column), isNullable, isUnique, alignment (Left | Center | Right | Default), encodingHint (Hash | Value | Default - the VertiPaq encoding preference).")]
    public static string SetColumnFlags(ModelService model, string sessionId, string table, string column,
        [Description("mark/unmark as the table key")] bool? isKey = null,
        [Description("allow nulls")] bool? isNullable = null,
        [Description("values are unique")] bool? isUnique = null,
        [Description("Left | Center | Right | Default")] string? alignment = null,
        [Description("Hash | Value | Default (VertiPaq encoding hint)")] string? encodingHint = null)
        => J.Try(() => model.SetColumnFlags(sessionId, table, column, isKey, isNullable, isUnique, alignment, encodingHint));

    // ---------------------------------------------------------------- rename table
    [McpServerTool(Name = "rename_table")]
    [Description("Rename a table (updates the model object; references by the old name in M/DAX are not rewritten).")]
    public static string RenameTable(ModelService model, string sessionId, string table, string newName)
        => J.Try(() => model.RenameTable(sessionId, table, newName));

    // ---------------------------------------------------------------- KPI extras
    [McpServerTool(Name = "update_kpi")]
    [Description("Extend a measure's KPI beyond set_kpi: set the trend expression, a target format string, and the status/trend/target descriptions. Creates the KPI if the measure has none. Any omitted property is left unchanged.")]
    public static string UpdateKpi(ModelService model, string sessionId, string table, string measure,
        [Description("DAX trend expression (e.g. prior-period value)")] string? trendExpression = null,
        [Description("format string applied to the KPI target")] string? targetFormatString = null,
        [Description("status description text")] string? statusDescription = null,
        [Description("trend description text")] string? trendDescription = null,
        [Description("target description text")] string? targetDescription = null)
        => J.Try(() => model.UpdateKpi(sessionId, table, measure, trendExpression, targetFormatString, statusDescription, trendDescription, targetDescription));

    // ---------------------------------------------------------------- hierarchy / level properties
    [McpServerTool(Name = "set_hierarchy_properties")]
    [Description("Set hierarchy-level properties (any subset): displayFolder, hidden (show/hide the hierarchy), hideMembers = Default | HideBlankMembers (hide blank members of the hierarchy).")]
    public static string SetHierarchyProperties(ModelService model, string sessionId, string table, string hierarchy,
        [Description("display folder for the hierarchy")] string? displayFolder = null,
        [Description("hide (true) or show (false) the hierarchy")] bool? hidden = null,
        [Description("Default | HideBlankMembers")] string? hideMembers = null)
        => J.Try(() => model.SetHierarchyProperties(sessionId, table, hierarchy, displayFolder, hidden, hideMembers));

    [McpServerTool(Name = "set_level_properties")]
    [Description("Set a hierarchy LEVEL's properties (any subset): ordinal (its order within the hierarchy), description, and/or newName (rename the level). NOTE: hide-blank-members and display folder are hierarchy-wide - set those with set_hierarchy_properties.")]
    public static string SetLevelProperties(ModelService model, string sessionId, string table, string hierarchy, string level,
        [Description("ordinal/order within the hierarchy")] int? ordinal = null,
        [Description("description text")] string? description = null,
        [Description("rename the level to this name")] string? newName = null)
        => J.Try(() => model.SetLevelProperties(sessionId, table, hierarchy, level, ordinal, description, newName));

    [McpServerTool(Name = "delete_hierarchy")]
    [Description("Delete a hierarchy from a table.")]
    public static string DeleteHierarchy(ModelService model, string sessionId, string table, string hierarchy)
        => J.Try(() => model.DeleteHierarchy(sessionId, table, hierarchy));

    // ---------------------------------------------------------------- model settings / auto date-time
    [McpServerTool(Name = "set_model_settings")]
    [Description("Set model-level settings (any subset; omitted settings unchanged): discourageImplicitMeasures (force explicit measures - a best practice), defaultMode = Import | DirectQuery | Dual | DirectLake | Push | Default, directLakeBehavior = Automatic | DirectLakeOnly | DirectQueryOnly, culture (model locale, e.g. en-US).")]
    public static string SetModelSettings(ModelService model, string sessionId,
        [Description("discourage implicit measures (force explicit measures)")] bool? discourageImplicitMeasures = null,
        [Description("Import | DirectQuery | Dual | DirectLake | Push | Default")] string? defaultMode = null,
        [Description("Automatic | DirectLakeOnly | DirectQueryOnly")] string? directLakeBehavior = null,
        [Description("model culture/locale, e.g. en-US")] string? culture = null)
        => J.Try(() => model.SetModelSettings(sessionId, discourageImplicitMeasures, defaultMode, directLakeBehavior, culture));

    [McpServerTool(Name = "disable_auto_date_time")]
    [Description("Turn OFF Auto date/time: sets the model's __PBI_TimeIntelligenceEnabled annotation to 0 (so Power BI Desktop stops auto-generating a hidden LocalDateTable per date column) and removes any auto date/time tables already in the model. Slims the model and stops auto-date bloat.")]
    public static string DisableAutoDateTime(ModelService model, string sessionId)
        => J.Try(() => model.DisableAutoDateTime(sessionId));

    // ---------------------------------------------------------------- role member / permission removal
    [McpServerTool(Name = "remove_role_member")]
    [Description("Remove a member from a security role (by member identity). Returns removed=false if the member was not on the role.")]
    public static string RemoveRoleMember(ModelService model, string sessionId, string role,
        [Description("member identity to remove, e.g. user@org.com")] string member)
        => J.Try(() => model.RemoveRoleMember(sessionId, role, member));

    [McpServerTool(Name = "set_role_permission")]
    [Description("Change a role's model-wide permission: Read | ReadRefresh | Refresh | Administrator | None.")]
    public static string SetRolePermission(ModelService model, string sessionId, string role,
        [Description("Read | ReadRefresh | Refresh | Administrator | None")] string modelPermission)
        => J.Try(() => model.SetRolePermission(sessionId, role, modelPermission));

    // ---------------------------------------------------------------- perspective removal
    [McpServerTool(Name = "remove_from_perspective")]
    [Description("Remove an object from a perspective. objectType = table | column | measure | hierarchy. For column/measure/hierarchy, pass the object's table. Removing a table removes it (and its members) from the perspective.")]
    public static string RemoveFromPerspective(ModelService model, string sessionId, string perspective,
        [Description("table | column | measure | hierarchy")] string objectType,
        [Description("the object's name")] string name,
        [Description("the object's table (required for column/measure/hierarchy)")] string? table = null)
        => J.Try(() => model.RemoveFromPerspective(sessionId, perspective, objectType, name, table));

    [McpServerTool(Name = "delete_perspective")]
    [Description("Delete a perspective from the model.")]
    public static string DeletePerspective(ModelService model, string sessionId, string perspective)
        => J.Try(() => model.DeletePerspective(sessionId, perspective));

    // ---------------------------------------------------------------- variations: list / delete
    [McpServerTool(Name = "list_variations")]
    [Description("List the date-navigation variations on a column (name, relationship, default hierarchy, isDefault). Read-only.")]
    public static string ListVariations(ModelService model, string sessionId, string table, string column)
        => J.Try(() => model.ListVariations(sessionId, table, column));

    [McpServerTool(Name = "delete_variation")]
    [Description("Delete a date-navigation variation from a column (by variation name).")]
    public static string DeleteVariation(ModelService model, string sessionId, string table, string column,
        [Description("the variation name to remove")] string variation)
        => J.Try(() => model.DeleteVariation(sessionId, table, column, variation));

    // ---------------------------------------------------------------- first-class data source
    [McpServerTool(Name = "set_data_source")]
    [Description("Create or update a first-class DataSource object (separate from raw M partitions). kind = Structured (modern Power Query - connectionDetails and credential are each a flat JSON object of key/value pairs, e.g. connectionDetails={\"protocol\":\"tds\",\"server\":\"srv\",\"database\":\"db\"}) or Provider (legacy - connectionString + provider + impersonation = Default | ImpersonateAccount | ImpersonateAnonymous | ImpersonateCurrentUser | ImpersonateServiceAccount | ImpersonateUnattendedAccount). Replaces a same-named data source in place.")]
    public static string SetDataSource(ModelService model, string sessionId, string name,
        [Description("Structured | Provider")] string kind,
        [Description("(Structured) flat JSON object of connection-detail key/value pairs")] string? connectionDetails = null,
        [Description("(Structured) flat JSON object of credential key/value pairs")] string? credential = null,
        [Description("(Provider) the connection string")] string? connectionString = null,
        [Description("(Provider) the OLE DB/provider name")] string? provider = null,
        [Description("(Provider) impersonation mode")] string? impersonation = null)
        => J.Try(() => model.SetDataSource(sessionId, name, kind, connectionDetails, credential, connectionString, provider, impersonation));

    // ================================================================ Wave O: SVG-image measures
    [McpServerTool(Name = "add_svg_databar")]
    [Description("Author an SVG DATA BAR measure (DataCategory=ImageUrl) that renders an in-cell bar scaled to value/max. Drop it into a table/matrix/card. valueMeasure/maxMeasure are measure names; pass maxMeasure for a comparable scale. negativeFill colours bars below zero; align=left|right.")]
    public static string AddSvgDatabar(ModelService model, string sessionId,
        [Description("home table for the new measure")] string table,
        [Description("the new measure's name")] string name,
        [Description("the value measure, e.g. Total Sales")] string valueMeasure,
        [Description("a measure giving the bar's max/100% (optional - else |value|)")] string? maxMeasure = null,
        [Description("bar colour hex, e.g. #2E86AB")] string fill = "#2E86AB",
        [Description("colour for negative bars (optional)")] string? negativeFill = null,
        [Description("svg width in px")] double width = 100,
        [Description("svg height in px")] double height = 16,
        [Description("left | right (right grows leftwards)")] string align = "left")
        => J.Try(() => model.AddSvgDatabar(sessionId, table, name, valueMeasure, maxMeasure, fill, negativeFill, width, height, align));

    [McpServerTool(Name = "add_svg_sparkline")]
    [Description("Author an SVG SPARKLINE measure (DataCategory=ImageUrl): a min-max scaled line/area over a category column, rendered in-cell. kind=line|area|gradient-area. showLastPoint adds a terminal marker; intercept draws a zero baseline. categoryColumn = Table[Column].")]
    public static string AddSvgSparkline(ModelService model, string sessionId,
        string table, string name,
        [Description("the value measure plotted per category")] string valueMeasure,
        [Description("the category column, e.g. Calendar[Month]")] string categoryColumn,
        [Description("line | area | gradient-area")] string kind = "line",
        [Description("draw a marker on the final point")] bool showLastPoint = false,
        [Description("draw a dashed zero baseline")] bool intercept = false,
        [Description("line colour hex")] string lineColor = "#2E86AB",
        [Description("svg width in px")] double width = 100,
        [Description("svg height in px")] double height = 24)
        => J.Try(() => model.AddSvgSparkline(sessionId, table, name, valueMeasure, categoryColumn, kind, showLastPoint, intercept, lineColor, width, height));

    [McpServerTool(Name = "add_svg_progress_bar")]
    [Description("Author an SVG PROGRESS BAR / BULLET measure (DataCategory=ImageUrl): a track + a fill scaled to value/target, rendered in-cell. kind=bar|bullet (bullet adds a target tick). trackColor is the rail; fillColor the achieved portion.")]
    public static string AddSvgProgressBar(ModelService model, string sessionId,
        string table, string name,
        [Description("the value measure (achieved)")] string valueMeasure,
        [Description("the target measure (100%)")] string targetMeasure,
        [Description("bar | bullet")] string kind = "bar",
        [Description("track colour hex")] string trackColor = "#E6E9EF",
        [Description("fill colour hex")] string fillColor = "#2E7D32",
        [Description("svg width in px")] double width = 100,
        [Description("svg height in px")] double height = 14)
        => J.Try(() => model.AddSvgProgressBar(sessionId, table, name, valueMeasure, targetMeasure, kind, trackColor, fillColor, width, height));

    [McpServerTool(Name = "add_svg_gauge")]
    [Description("Author an SVG ARC GAUGE measure (DataCategory=ImageUrl): a semicircular gauge mapping the value onto min..max, rendered in-cell. thresholds (optional JSON array of {at,color}, ascending) tint the value arc by the highest band the value clears.")]
    public static string AddSvgGauge(ModelService model, string sessionId,
        string table, string name,
        [Description("the value measure")] string valueMeasure,
        [Description("gauge minimum")] double min,
        [Description("gauge maximum")] double max,
        [Description("JSON array of {at,color} threshold bands (optional), e.g. [{\"at\":80,\"color\":\"#2E7D32\"}]")] string? thresholds = null,
        [Description("default arc colour hex")] string fillColor = "#2E86AB",
        [Description("svg size in px (square)")] double size = 60)
        => J.Try(() => model.AddSvgGauge(sessionId, table, name, valueMeasure, min, max, ParseThresholds(thresholds), fillColor, size));

    [McpServerTool(Name = "add_svg_icon")]
    [Description("Author an SVG THRESHOLD ICON measure (DataCategory=ImageUrl): renders a unicode glyph in a colour chosen by which [min,max) band the value falls into. rules = JSON array of {min,max,glyph,color}, e.g. [{\"min\":-1e9,\"max\":0,\"glyph\":\"▼\",\"color\":\"#D7263D\"},{\"min\":0,\"max\":1e9,\"glyph\":\"▲\",\"color\":\"#2E7D32\"}].")]
    public static string AddSvgIcon(ModelService model, string sessionId,
        string table, string name,
        [Description("the value measure tested against the bands")] string valueMeasure,
        [Description("JSON array of {min,max,glyph,color} rules")] string rules,
        [Description("svg size in px (square)")] double size = 16)
        => J.Try(() => model.AddSvgIcon(sessionId, table, name, valueMeasure, ParseIconRules(rules), size));

    [McpServerTool(Name = "add_svg_chip")]
    [Description("Author an SVG CHIP/PILL measure (DataCategory=ImageUrl): an auto-sizing rounded label whose width fits the text. textMeasure supplies the label; colorMeasure (optional, returns a hex) supplies the fill, else defaultFill.")]
    public static string AddSvgChip(ModelService model, string sessionId,
        string table, string name,
        [Description("a measure returning the chip's text")] string textMeasure,
        [Description("a measure returning the chip's fill hex (optional)")] string? colorMeasure = null,
        [Description("default fill hex when no colorMeasure")] string defaultFill = "#2E86AB",
        [Description("svg height in px")] double height = 20)
        => J.Try(() => model.AddSvgChip(sessionId, table, name, textMeasure, colorMeasure, defaultFill, height));

    // ================================================================ Wave O: text / format helpers
    [McpServerTool(Name = "add_dynamic_title_measure")]
    [Description("Author a DYNAMIC TITLE text measure: SELECTEDVALUE over a column with an All/multiple fallback, optionally wrapped in a template ({value} is the placeholder). e.g. column Dim_Product[Category], template \"Sales - {value}\". Then bind it to a visual title with bind_dynamic_title (report side).")]
    public static string AddDynamicTitleMeasure(ModelService model, string sessionId,
        [Description("home table for the new measure")] string homeTable,
        [Description("the new measure's name, e.g. Page Title")] string measureName,
        [Description("the column whose selection drives the title, e.g. Dim_Product[Category]")] string column,
        [Description("title template with {value} placeholder (optional; omit for the bare value)")] string? template = null,
        [Description("label when not a single selection (default 'All')")] string? allLabel = null)
        => J.Try(() => model.AddDynamicTitleMeasure(sessionId, homeTable, measureName, column, template, allLabel));

    [McpServerTool(Name = "set_custom_format_string")]
    [Description("Set a measure's STATIC custom format string: a 3/4-section pattern positive;negative;zero;\"text\" with optional [Colour] codes and UNICHAR arrows (distinct from set_dynamic_format_string, the DAX-driven one). e.g. \"[Green]▲ #,0;[Red]▼ #,0;0\".")]
    public static string SetCustomFormatString(ModelService model, string sessionId,
        [Description("the measure's table")] string table,
        [Description("the measure name")] string measure,
        [Description("the format pattern (3/4 sections separated by ;)")] string pattern)
        => J.Try(() => model.SetCustomFormatString(sessionId, table, measure, pattern));

    [McpServerTool(Name = "add_calc_group_format")]
    [Description("Build a calc group whose items OVERRIDE the displayed format (a currency / % / scale switcher): each item keeps the value (SELECTEDMEASURE()) but reformats it via its own format string. items = JSON array of {name,formatString}. Creates the calc group on table if absent.")]
    public static string AddCalcGroupFormat(ModelService model, string sessionId,
        [Description("the table to host the format calc group")] string table,
        [Description("JSON array of {name,formatString}, e.g. [{\"name\":\"Currency\",\"formatString\":\"$#,0\"},{\"name\":\"Percent\",\"formatString\":\"0.0%\"}]")] string items,
        [Description("calc-group precedence (optional)")] int? precedence = null)
        => J.Try(() => model.AddCalcGroupFormat(sessionId, table, ParseFormatItems(items), precedence));

    [McpServerTool(Name = "add_ibcs_variance_measure")]
    [Description("Author IBCS variance measure(s) for an actual measure vs a comparison base (PY|PL|FC). kind=abs (AC - base, leading-sign format) or rel (DIVIDE(AC - base, ABS(base)) %). The base is expected as a measure '<actual> <comparison>' unless comparisonMeasure is given. applyIbcsFormat applies an IBCS number format (+/- leading sign).")]
    public static string AddIbcsVarianceMeasure(ModelService model, string sessionId,
        [Description("the measure's table")] string table,
        [Description("the actual measure, e.g. Sales")] string actualMeasure,
        [Description("PY | PL | FC")] string comparison = "PY",
        [Description("abs | rel")] string kind = "abs",
        [Description("explicit comparison-base measure name (optional; else '<actual> <comparison>')")] string? comparisonMeasure = null,
        [Description("apply an IBCS number format")] bool applyIbcsFormat = true)
        => J.Try(() => model.AddIbcsVarianceMeasure(sessionId, table, actualMeasure, comparison, kind, comparisonMeasure, applyIbcsFormat));

    // ---- Wave O parse helpers (JSON -> tuples) ----
    private static IReadOnlyList<(double at, string color)>? ParseThresholds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var arr = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray
                  ?? throw new ArgumentException("thresholds must be a JSON array of {at,color}.");
        var list = new List<(double, string)>();
        foreach (var n in arr)
            if (n is System.Text.Json.Nodes.JsonObject o)
                list.Add((ToD(o["at"]), (string?)o["color"] ?? "#808080"));
        return list;
    }

    private static IReadOnlyList<(double min, double max, string? glyph, string? color)> ParseIconRules(string json)
    {
        var arr = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray
                  ?? throw new ArgumentException("rules must be a JSON array of {min,max,glyph,color}.");
        var list = new List<(double, double, string?, string?)>();
        foreach (var n in arr)
            if (n is System.Text.Json.Nodes.JsonObject o)
                list.Add((ToD(o["min"]), ToD(o["max"]), (string?)o["glyph"], (string?)o["color"]));
        if (list.Count == 0) throw new ArgumentException("at least one icon rule is required.");
        return list;
    }

    private static IReadOnlyList<(string name, string formatString)> ParseFormatItems(string json)
    {
        var arr = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray
                  ?? throw new ArgumentException("items must be a JSON array of {name,formatString}.");
        var list = new List<(string, string)>();
        foreach (var n in arr)
            if (n is System.Text.Json.Nodes.JsonObject o)
                list.Add(((string?)o["name"] ?? throw new ArgumentException("each item needs a name."),
                          (string?)o["formatString"] ?? throw new ArgumentException("each item needs a formatString.")));
        if (list.Count == 0) throw new ArgumentException("at least one format item is required.");
        return list;
    }

    private static double ToD(System.Text.Json.Nodes.JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<double>(); }
        catch { return double.TryParse(n.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0; }
    }

    // ================================================================ Wave R: model internals
    //  sync-correctness annotations, perf levers, Direct Lake / aggregations, calc-group selection
    //  expressions, data-access options, table privacy, Q&A linguistic depth, dependency/unused
    //  analyzers, calendar-based time intelligence, case-sensitive DAX fixer, report-measure extraction.

    [McpServerTool(Name = "set_lineage_tag")]
    [Description("Set the LineageTag (a stable identity that survives schema sync / git merge across renames) on a model object. objectType = model | table | column | measure | hierarchy | partition | relationship; pass the object's table for column/measure/hierarchy/partition. Needs compatibility level 1540+ (auto-bumped, reported back).")]
    public static string SetLineageTag(ModelService model, string sessionId,
        [Description("model | table | column | measure | hierarchy | partition | relationship")] string objectType,
        [Description("the object's name")] string name,
        [Description("the lineage tag (any stable id, e.g. a GUID)")] string tag,
        [Description("the object's table (for column/measure/hierarchy/partition)")] string? table = null)
        => J.Try(() => model.SetLineageTag(sessionId, objectType, name, tag, table));

    [McpServerTool(Name = "set_source_lineage_tag")]
    [Description("Set the SourceLineageTag (the SOURCE-side name a Direct Lake / composite model re-binds to after a schema refresh) on a model object. objectType = model | table | column | measure | hierarchy | partition | relationship; pass the object's table where needed. value = the source object's name. Needs compatibility level 1550+ (auto-bumped, reported back).")]
    public static string SetSourceLineageTag(ModelService model, string sessionId,
        [Description("model | table | column | measure | hierarchy | partition | relationship")] string objectType,
        [Description("the object's name")] string name,
        [Description("the SOURCE name to bind to (e.g. the lakehouse column/table name)")] string value,
        [Description("the object's table (for column/measure/hierarchy/partition)")] string? table = null)
        => J.Try(() => model.SetSourceLineageTag(sessionId, objectType, name, value, table));

    [McpServerTool(Name = "declare_changed_property")]
    [Description("Mark a property as locally CHANGED so a schema sync does not wipe your override on a composite / Direct Lake object (name, isHidden, formatString, summarizeBy etc. are otherwise treated as source-owned and reset on refresh). objectType = model | table | column | measure | hierarchy | partition | relationship; pass the object's table where needed.")]
    public static string DeclareChangedProperty(ModelService model, string sessionId,
        [Description("model | table | column | measure | hierarchy | partition | relationship")] string objectType,
        [Description("the object's name")] string name,
        [Description("the property to protect, e.g. Name, IsHidden, FormatString, SummarizeBy")] string property,
        [Description("the object's table (for column/measure/hierarchy/partition)")] string? table = null)
        => J.Try(() => model.DeclareChangedProperty(sessionId, objectType, name, property, table));

    [McpServerTool(Name = "mark_removed_children")]
    [Description("Stamp the model/table annotation PBI_RemovedChildren so a schema sync keeps tables/columns you deleted removed (otherwise they reappear on the next refresh). removedSourceLineageTags is a comma-separated list of the SOURCE lineage tags/names of the removed children. Merges into any existing list.")]
    public static string MarkRemovedChildren(ModelService model, string sessionId,
        [Description("the table the removed children belonged to")] string table,
        [Description("removed children's source lineage tags/names, comma-separated")] string removedSourceLineageTags)
        => J.Try(() => model.MarkRemovedChildren(sessionId, table, Split(removedSourceLineageTags)));

    [McpServerTool(Name = "set_isavailableinmdx")]
    [Description("Set Column.IsAvailableInMDX. Setting it false on high-cardinality, non-attribute columns saves memory and processing. GUARD: it is kept TRUE on any SortByColumn target (flipping it false breaks the sort with 'invalid column ID'). Pass table+column for one column; pass table alone to set a whole table; or bulkHeuristic=hiddenAndKeys (with no table/column) for a safe model-wide pass over hidden/key columns.")]
    public static string SetIsAvailableInMdx(ModelService model, string sessionId,
        [Description("the value to set (false to disable MDX availability)")] bool value,
        [Description("table (omit with bulkHeuristic for a model-wide pass)")] string? table = null,
        [Description("column (omit to set the whole table)")] string? column = null,
        [Description("hiddenAndKeys for a model-wide pass over hidden/key columns")] string? bulkHeuristic = null)
        => J.Try(() => model.SetIsAvailableInMdx(sessionId, table, column, value, bulkHeuristic));

    [McpServerTool(Name = "stamp_vertipaq_stats")]
    [Description("Read the storage DMVs and write each column's VertiPaq stats as Vertipaq_* annotations (Vertipaq_TotalSize / Vertipaq_DictionarySize / Vertipaq_Cardinality - the semantic-link-labs scheme) so the numbers persist in the model definition for offline review. Read of the live engine; writes annotations.")]
    public static string StampVertipaqStats(ModelService model, string sessionId)
        => J.Try(() => model.StampVertipaqStats(sessionId));

    [McpServerTool(Name = "set_calc_group_selection_expressions")]
    [Description("Set a calculation group's NoSelectionExpression and/or MultipleOrEmptySelectionExpression - the DAX returned when no / multiple calc items are selected - with optional dynamic format strings. Needs compatibility level 1605+ (auto-bumped, reported back).")]
    public static string SetCalcGroupSelectionExpressions(ModelService model, string sessionId, string table,
        [Description("DAX run when NO calc item is selected (optional)")] string? noSelectionExpression = null,
        [Description("DAX run when MULTIPLE or empty selection (optional)")] string? multipleOrEmptySelectionExpression = null,
        [Description("dynamic format string for the no-selection expression (optional)")] string? noSelectionFormatString = null,
        [Description("dynamic format string for the multiple/empty expression (optional)")] string? multipleOrEmptyFormatString = null)
        => J.Try(() => model.SetCalcGroupSelectionExpressions(sessionId, table, noSelectionExpression,
            multipleOrEmptySelectionExpression, noSelectionFormatString, multipleOrEmptyFormatString));

    [McpServerTool(Name = "set_selection_expression_behavior")]
    [Description("Set the model-wide selection-expression behavior controlling which clients honour calc-group selection expressions: automatic | nonvisual | visual. Needs compatibility level 1605+ (auto-bumped, reported back). FLAG: stamped as a PBI_SelectionExpressionBehavior model annotation - the strongly-typed property is absent from this build.")]
    public static string SetSelectionExpressionBehavior(ModelService model, string sessionId,
        [Description("automatic | nonvisual | visual")] string behavior)
        => J.Try(() => model.SetSelectionExpressionBehavior(sessionId, behavior));

    [McpServerTool(Name = "set_data_access_options")]
    [Description("Set Model.DataAccessOptions (any subset; omitted unchanged): fastCombine (ignore privacy levels for query folding IN this file - the in-file equivalent of disabling Privacy), legacyRedirects, returnErrorValuesAsNull. Needs compatibility level 1400+.")]
    public static string SetDataAccessOptions(ModelService model, string sessionId,
        [Description("ignore privacy levels in-file (fast combine)")] bool? fastCombine = null,
        [Description("allow legacy redirects")] bool? legacyRedirects = null,
        [Description("return error values as null")] bool? returnErrorValuesAsNull = null)
        => J.Try(() => model.SetDataAccessOptions(sessionId, fastCombine, legacyRedirects, returnErrorValuesAsNull));

    [McpServerTool(Name = "set_table_private")]
    [Description("Set Table.IsPrivate - a private table is hidden from ALL clients (stronger than hide), the right setting for internal helper / staging tables.")]
    public static string SetTablePrivate(ModelService model, string sessionId, string table,
        [Description("true to make the table private (hidden from all clients)")] bool isPrivate)
        => J.Try(() => model.SetTablePrivate(sessionId, table, isPrivate));

    [McpServerTool(Name = "set_synonym_state")]
    [Description("Author Q&A synonyms with an explicit State (Authored to make terms stick, Deleted to suppress an auto-generated term, Generated, Suggested) and optional Weight 0..1. objectType = table | column | measure | hierarchy; pass the object's table where needed. Extends set_synonyms (which only adds bare terms). NOTE: the Q&A linguistic schema is large; only flat per-entity terms with State/Weight are authored.")]
    public static string SetSynonymState(ModelService model, string sessionId,
        [Description("table | column | measure | hierarchy")] string objectType,
        [Description("the object's name")] string name,
        [Description("synonyms, comma-separated")] string synonyms,
        [Description("Authored | Deleted | Generated | Suggested")] string state = "Authored",
        [Description("term weight 0..1 (optional)")] double? weight = null,
        [Description("culture/locale (optional; defaults to the model culture)")] string? culture = null,
        [Description("the object's table (for column/measure/hierarchy)")] string? table = null)
        => J.Try(() => model.SetSynonymState(sessionId, objectType, name, Split(synonyms), state, weight, culture, table));

    [McpServerTool(Name = "set_qna_phrasing")]
    [Description("Author an LSDL Q&A phrasing into the model's linguistic metadata - the way to teach Q&A a concept like 'happy customers'. phrasingType = Verb | Adjective | Noun | PreModifier | Preposition | Attribute | Name | DynamicNoun. phrasingJson is the LSDL phrasing definition as a JSON object. FLAG: the linguistic phrasing schema is large and is stored verbatim, not validated.")]
    public static string SetQnaPhrasing(ModelService model, string sessionId,
        [Description("Verb | Adjective | Noun | PreModifier | Preposition | Attribute | Name | DynamicNoun")] string phrasingType,
        [Description("a name for the phrasing/relationship")] string phrasingName,
        [Description("the LSDL phrasing definition as a JSON object")] string phrasingJson,
        [Description("culture/locale (optional; defaults to the model culture)")] string? culture = null)
        => J.Try(() => model.SetQnaPhrasing(sessionId, phrasingType, phrasingName, phrasingJson, culture));

    [McpServerTool(Name = "add_auto_aggregations")]
    [Description("Build an auto-aggregation scaffold: a hidden {Table}_Agg GROUPBY table over the group-by columns, hidden _Agg measures for each mapping, and IF-routing rewrites of the base measures so they answer from the small agg table when possible. groupByColumns is comma-separated Table[Column]. measureMappings is comma-separated aggMeasureName=baseMeasure. Extends set_aggregation. FLAG: the IF-routing is a best-known scaffold - review before production.")]
    public static string AddAutoAggregations(ModelService model, string sessionId,
        [Description("the detail (fact) table to summarise")] string detailTable,
        [Description("group-by columns, comma-separated Table[Column]")] string groupByColumns,
        [Description("measure mappings, comma-separated aggMeasureName=baseMeasure")] string measureMappings)
        => J.Try(() => model.AddAutoAggregations(sessionId, detailTable, Split(groupByColumns),
            ParseKeyValues(measureMappings).Select(kv => (kv.Key, kv.Value)).ToList()));

    [McpServerTool(Name = "warm_directlake_cache")]
    [Description("Warm the Direct Lake cache: run EVALUATE TOPN(1, SELECTCOLUMNS(...)) over the listed columns (or every column on the table) to force them resident, removing the first-query latency. columns is comma-separated (omit for all). Returns the query and result.")]
    public static string WarmDirectLakeCache(ModelService model, string sessionId, string table,
        [Description("columns to warm, comma-separated (omit for all)")] string? columns = null)
        => J.Try(() => model.WarmDirectLakeCache(sessionId, table, columns == null ? null : Split(columns)));

    [McpServerTool(Name = "check_directlake_fallback")]
    [Description("Diagnose Direct Lake DirectQuery fallbacks: read the fallback-reason DMV and list model objects (calculated columns / calculated tables) that are unsupported in Direct Lake and force a fallback. Read-only.")]
    public static string CheckDirectLakeFallback(ModelService model, string sessionId)
        => J.Try(() => model.CheckDirectLakeFallback(sessionId));

    [McpServerTool(Name = "analyze_dependencies")]
    [Description("Analyse measure/column dependencies and impact. Returns the live INFO.CALCDEPENDENCY lineage plus, when an object is named ([Measure] or Table[Column]), the direct dependants (which measures reference it) from the model tree. Read-only.")]
    public static string AnalyzeDependencies(ModelService model, string sessionId,
        [Description("the object to assess impact for: [Measure] or Table[Column] (optional)")] string? @object = null)
        => J.Try(() => model.AnalyzeDependencies(sessionId, @object));

    [McpServerTool(Name = "find_unused")]
    [Description("Find columns and measures never referenced inside the model - not by a relationship, sort-by, hierarchy level, RLS filter, or any measure / calculated-column / calculated-table DAX. (Visual usage lives in the report layer and is not scanned here.) Read-only - the cleanup shortlist.")]
    public static string FindUnused(ModelService model, string sessionId)
        => J.Try(() => model.FindUnused(sessionId));

    [McpServerTool(Name = "add_calendar_based_time_intelligence")]
    [Description("Add native (non-Gregorian) calendar-based time intelligence: a calendar object on the calendar table's primary date column with a calendarColumnGroup over the associated period columns. associatedColumns is comma-separated. FLAG: the calendar / calendarColumnGroup objects are very new and absent from this build, so this is stamped as a PBI_Calendar table annotation - confirm in Desktop.")]
    public static string AddCalendarBasedTimeIntelligence(ModelService model, string sessionId,
        [Description("the calendar table")] string calendarTable,
        [Description("the primary date column")] string primaryColumn,
        [Description("associated period columns, comma-separated")] string associatedColumns)
        => J.Try(() => model.AddCalendarBasedTimeIntelligence(sessionId, calendarTable, primaryColumn, Split(associatedColumns)));

    [McpServerTool(Name = "fix_case_sensitive_dax")]
    [Description("Rewrite every measure's table / column / measure references to the model's EXACT casing - the fix for DirectQuery / Direct Lake against case-sensitive sources where 'sales'[amount] and 'Sales'[Amount] are different objects. Returns the measures rewritten.")]
    public static string FixCaseSensitiveDax(ModelService model, string sessionId)
        => J.Try(() => model.FixCaseSensitiveDax(sessionId));

    [McpServerTool(Name = "extract_report_level_measures")]
    [Description("Promote report-level measures into the model as real measures: read the report definition's config.modelExtensions[].entities[].measures[] from a .pbix on disk and add each to its host table (keeping format/folder), skipping any name that already exists. Returns the measures promoted.")]
    public static string ExtractReportLevelMeasures(ModelService model, string sessionId,
        [Description("path to the .pbix whose report-level measures to promote")] string pbixPath)
        => J.Try(() => model.ExtractReportLevelMeasures(sessionId, pbixPath));
}
