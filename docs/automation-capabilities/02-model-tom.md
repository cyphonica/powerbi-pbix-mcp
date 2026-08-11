# Semantic model programming - TOM / TMSL / TMDL / XMLA

Source: learn.microsoft.com, `Microsoft.AnalysisServices.Tabular` (AMO/TOM), CL 1200+. Edits buffered, committed via `Model.SaveChanges()`; wire protocol XMLA.

## TOM object tree (Server -> Database -> Model) - authorable objects + key props

- **Model**: Tables, DataSources, Relationships, Roles, Cultures, Perspectives, Expressions (shared M), QueryGroups, Functions, Annotations, ExtendedProperties; DefaultMode, DiscourageImplicitMeasures, Culture, DirectLakeBehavior, etc. (NB measures live on Table, not Model.)
- **Table**: Columns, Partitions, Measures, Hierarchies, Annotations; IsHidden, IsPrivate, **DataCategory** (Time/Geography/...), ExcludeFromModelRefresh, RefreshPolicy, **CalculationGroup**, DefaultDetailRowsDefinition, LineageTag.
- **Column** (abstract) + DataColumn (SourceColumn) / CalculatedColumn (Expression DAX) / CalculatedTableColumn / RowNumberColumn. Props: DataType, **FormatString**, **SortByColumn**, **DisplayFolder**, **SummarizeBy** (AggregateFunction), **DataCategory**, IsHidden, IsKey, IsNullable, IsUnique, Alignment, EncodingHint, AlternateOf (aggregations), Variations, LineageTag.
- **Measure**: Expression (DAX), FormatString, **DisplayFolder**, Description, IsHidden, **KPI**, **DetailRowsDefinition** (drillthrough), **FormatStringDefinition** (dynamic format), LineageTag.
- **Partition**: Source (PartitionSource), Mode (Import/DirectQuery/Push/Dual/**DirectLake**), DataView, QueryGroup, DataCoverageDefinition (hybrid). Sources: QueryPartitionSource (native SQL), **MPartitionSource** (M/Power Query), CalculatedPartitionSource (DAX), EntityPartitionSource, **PolicyRangePartitionSource** (incremental ranges). Methods: RequestRefresh/RequestMerge.
- **DataSource**: ProviderDataSource (ConnectionString/Provider/Impersonation) or **StructuredDataSource** (ConnectionDetails/Credential, modern PQ).
- **RefreshPolicy / BasicRefreshPolicy** (incremental refresh): RollingWindowGranularity/Periods, IncrementalGranularity/Periods, SourceExpression ([RangeStart]/[RangeEnd]), PollingExpression, Mode (Import/Hybrid).
- **NamedExpression** (Model.Expressions): shared M queries + M parameters; Expression (M), Kind=M, ParameterValuesColumn, QueryGroup.
- **QueryGroup**: Folder (query display folders).
- **SingleColumnRelationship**: FromColumn/ToColumn, From/ToCardinality, CrossFilteringBehavior (One/Both/Auto), SecurityFilteringBehavior, IsActive, JoinOnDateBehavior.
- **Hierarchy** + **Level** (Ordinal, Column): HideMembers, DisplayFolder.
- **ModelRole** (RLS/OLS): ModelPermission (None/Read/ReadRefresh/Refresh/Administrator), Members (Windows/External role members), **TablePermissions**. **TablePermission**: Table, **FilterExpression** (DAX RLS), MetadataPermission (table OLS), ColumnPermissions. **ColumnPermission**: Column, MetadataPermission (column OLS).
- **Perspective**: PerspectiveTables -> PerspectiveColumn/Measure/Hierarchy.
- **Culture** + **ObjectTranslation** (Caption/Description/DisplayFolder translations) + LinguisticMetadata (Q&A synonyms).
- **CalculationGroup** (Table.CalculationGroup): Precedence, CalculationItems, MultipleOrEmptySelectionExpression, NoSelectionExpression. **CalculationItem**: Expression (DAX, SELECTEDMEASURE()), Ordinal, FormatStringDefinition.
- **KPI** (Measure.KPI): TargetExpression, StatusExpression, StatusGraphic, TrendExpression, ...
- **FormatStringDefinition** (dynamic format), **DetailRowsDefinition** (DETAILROWS), **Variation** (date nav), **AlternateOf** (agg: GroupBy/Sum/Count/Min/Max), **DataCoverageDefinition** (hybrid), **Annotation**, **ExtendedProperty** (String/Json), **LineageTag/SourceLineageTag**.

### Key enums
DataType: String/Int64/Double/DateTime/Decimal/Boolean/Binary/Variant. AggregateFunction (SummarizeBy): Default/None/Sum/Min/Max/Count/Average/DistinctCount. ModeType: Import/DirectQuery/Push/Dual/DirectLake. RefreshType: Full/ClearValues/Calculate/DataOnly/Automatic/Add/Defragment/Indexes.

## TMSL commands (JSON over XMLA)
create, createOrReplace (full def required), alter (targeted), delete, refresh (type + objects + overrides + applyRefreshPolicy/effectiveDate), mergePartitions, backup, restore, attach, detach, synchronize (SSAS only - NOT Fabric/PBI), sequence (transactional batch + maxParallelism), updateCulture (SQL2025+).

## TMDL
Text/YAML-like, folder-per-object, full TOM fidelity, source-control friendly. Only verb = createOrReplace. TmdlSerializer API (Serialize/DeserializeModelToFolder, SerializeObject). PBIP definition.pbism can store model as model.bim (TMSL) or /definition (TMDL). Desktop has a TMDL view.

## XMLA endpoint
powerbi://api.powerbi.com/v1.0/<tenant>/<workspace>. Read-only by default; read-write (Premium/PPU/Fabric capacity) enables full model management. Clients: SSMS, Tabular Editor, DAX Studio, ALM Toolkit, Profiler, Excel, Report Builder, PowerShell SqlServer module (Invoke-ASCmd), AMO/TOM + ADOMD.NET. Fine-grain partition refresh bypasses the 48/day + scheduled-timeout limits. Limits: no write to live-connect/push/Excel models; XMLA-written PBIX can't be downloaded back; SPs can't be RLS members / set credentials.

## GAP-RELEVANT (our engine has TOM model tools; likely MISSING vs this surface)
RLS roles + role members + TablePermission.FilterExpression; OLS (table/column MetadataPermission); calculation groups + items; perspectives; KPIs; DetailRowsDefinition (drillthrough); dynamic FormatStringDefinition; translations/cultures + Q&A synonyms; AlternateOf aggregations; RefreshPolicy (incremental); Variations; column DataCategory; measure/column DisplayFolder + description; QueryGroup folders; annotations/extended properties; backup/restore/attach/detach/sequence; TMDL serialize/deserialize round-trip.
