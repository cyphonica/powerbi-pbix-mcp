# Power Query (M) + DAX automation surface

Source: learn.microsoft.com/powerquery-m + learn.microsoft.com/dax.

## Power Query M - connectors (data access)
Relational (fold to native SQL): Sql.Database/Databases, Oracle/MySQL/PostgreSQL/Db2/Teradata/Sybase/Informix/SapHana.Database. Generic: Odbc.DataSource/Query, OleDb.*, AdoDotNet.*, Access.Database. OLAP: AnalysisServices.Database (MDX/DAX), SapBW/Essbase cubes + Cube.* transforms. Files: Excel.Workbook, Excel.CurrentWorkbook, Csv.Document, Json.Document/FromValue, Xml.Tables/Document, Pdf.Tables, Html.Table, File.Contents, Lines.*. Folder: Folder.Files/Contents, Hdfs.*. Web/OData: Web.Contents (Query/Headers/RelativePath/Content POST/ApiKeyName/ManualStatusHandling), Web.Page/Headers/BrowserContents, OData.Feed (folds $filter/$select/$top), Soda.Feed. SharePoint.Files/Contents/Tables. Azure: AzureStorage.Blobs/Tables/DataLake, DeltaLake.Table, Cdm.Contents. SaaS: Salesforce, Exchange, GoogleAnalytics, AdobeAnalytics, ActiveDirectory.

## Power Query M - Table.* transforms (the data-prep workhorse) + UI mapping
This is the blueprint for Wave D (high-level transform tools generating M):
- Merge Queries -> Table.NestedJoin (+ Table.ExpandTableColumn); fuzzy -> Table.FuzzyNestedJoin
- Append Queries -> Table.Combine
- Pivot Column -> Table.Pivot ; Unpivot -> Table.Unpivot / Table.UnpivotOtherColumns
- Group By -> Table.Group (fuzzy -> Table.FuzzyGroup)
- Split Column -> Table.SplitColumn (+ Splitter.SplitTextBy*); into rows -> Table.ExpandListColumn
- Merge Columns -> Table.CombineColumns (+ Combiner.*)
- Replace Values/Errors -> Table.ReplaceValue (Replacer.*) / Table.ReplaceErrorValues
- Change Type -> Table.TransformColumnTypes
- Filter Rows / Remove Blank -> Table.SelectRows
- Keep/Remove Errors -> Table.SelectRowsWithErrors / Table.RemoveRowsWithErrors
- Remove/Choose Columns -> Table.RemoveColumns / Table.SelectColumns
- Rename/Reorder -> Table.RenameColumns / Table.ReorderColumns ; Duplicate -> Table.DuplicateColumn
- Add Custom/Conditional/Index Column -> Table.AddColumn (if/then/else) / Table.AddIndexColumn
- Single-column (Upper/Trim/Round/Extract/Date part) -> Table.TransformColumns + Text.*/Number.*/Date.*
- Fill Down/Up -> Table.FillDown/FillUp ; Transpose -> Table.Transpose
- First Row as Headers -> Table.PromoteHeaders ; Remove Duplicates -> Table.Distinct
- Sort -> Table.Sort ; Keep/Remove Top/Bottom -> Table.FirstN/LastN/Skip/RemoveLastN
- Expand record/table -> Table.ExpandRecordColumn / Table.ExpandTableColumn (+ AggregateTableColumn)
Construction: #table, Table.FromRows/Records/Columns. Profiling: Table.Schema, Table.Profile, Table.RowCount. Folding control: Table.Buffer, Table.View (custom folding), Table.StopFolding.

Scalar/structured categories: List.*, Record.*, Text.*, Number.*, Date.*, Time.*, DateTime(Zone).*, Duration.*, Logical.*, Binary.* + BinaryFormat.*, Value.* (ReplaceType), Type.*, Splitter.*, Combiner.*, Replacer.*, Comparer.*, Expression.*, Uri.*, Lines.*.

## Power Query SDK / custom connectors
VS Code extension; .pq section document -> .mez/.pqx. DataSource.Kind + Publish binding triple; Authentication kinds Anonymous/Key/UsernamePassword/Windows/OAuth/Aad; navigation tables (Table.ToNavigationTable); function docs via Documentation.* meta + Value.ReplaceType; Table.View for custom query folding; query folding (View Native Query). M engine runs in PBI Desktop+service+dataflows, Excel, Power Apps/Automate, ADF, Fabric Data Factory, SSIS, SSAS.

## DAX - 15 function categories (~250 fns)
Aggregation (SUM/SUMX/COUNT...), Filter (CALCULATE/ALL/FILTER/SELECTEDVALUE + window: WINDOW/OFFSET/INDEX/RANK/ROWNUMBER/RUNNINGSUM/MOVINGAVERAGE), Time-intelligence (TOTALYTD/DATEADD/SAMEPERIODLASTYEAR...), Relationship (RELATED/USERELATIONSHIP/CROSSFILTER), Table-manipulation (ADDCOLUMNS/SUMMARIZECOLUMNS/GROUPBY/UNION/TOPN/TREATAS/DETAILROWS/GENERATESERIES/DATATABLE), Logical (IF/SWITCH/COALESCE/BIT*), Text (CONCATENATE/FORMAT/SUBSTITUTE/COMBINEVALUES), Date-and-time (DATE/DATEDIFF/CALENDAR/CALENDARAUTO/NETWORKDAYS), Math-and-trig, Statistical (MEDIAN/PERCENTILE/RANKX/STDEV/LINEST), Financial (PMT/XIRR/XNPV/PRICE/YIELD), Information (ISBLANK/ISINSCOPE/HASONEVALUE/USERNAME/SELECTEDMEASURE/NAMEOF), Parent-child (PATH/PATHITEM), Other (BLANK/ERROR/TOJSON/TOCSV), INFO (metadata).

## DAX queries (EVALUATE) - data automation
EVALUATE <table-expr> [ORDER BY] [START AT]; DEFINE MEASURE/VAR/TABLE/COLUMN (override a measure within the query - the way to test before saving). Runners: DAX query view, Fabric notebooks (semantic link), executeQueries REST, SSMS, DAX Studio, Tabular Editor - all over XMLA.

## DAX INFO.* metadata functions (~75) - run inside EVALUATE, return tables
INFO.VIEW.TABLES/COLUMNS/MEASURES/RELATIONSHIPS (friendly, no joins). Core: INFO.TABLES/COLUMNS/MEASURES/RELATIONSHIPS/PARTITIONS/MODEL/HIERARCHIES/LEVELS/KPIS/DATASOURCES/EXPRESSIONS/ANNOTATIONS/CULTURES/OBJECTTRANSLATIONS/DETAILROWSDEFINITIONS/FORMATSTRINGDEFINITIONS/REFRESHPOLICIES/CALCULATIONGROUPS/CALCULATIONITEMS/ROLES/TABLEPERMISSIONS/COLUMNPERMISSIONS/PERSPECTIVES + storage/VertiPaq (STORAGETABLECOLUMNSEGMENTS etc.) + CALCDEPENDENCY (lineage).

## DMVs ($SYSTEM.*) - SELECT ... FROM $System.<rowset> (no JOIN/GROUP BY)
TMSCHEMA_* (tabular metadata), DBSCHEMA_*, MDSCHEMA_*, DISCOVER_* (sessions/connections/commands/locks/memory/storage/calc-dependency/traces). For monitoring + raw rowsets + memory profiling.

## GAP-RELEVANT for our engine
- Wave D = high-level PQ transform tools generating the M above (we have set_partition_m / add_table_from_m raw-M; the discrete transforms = merge/append/pivot/group-by/split/replace/filter/custom/index would be the ergonomic layer).
- Metadata: our analyze_model could lean on INFO.VIEW.* / TMSCHEMA_* via executeQueries/XMLA; we already run_dax. VertiPaq memory profiling (DISCOVER_STORAGE_*) = a possible model-health tool.
- DAX testing: DEFINE MEASURE override before save (we have validate_dax/run_dax).
