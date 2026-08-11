# Coverage audit - the definitive remaining-gap profile (post Waves A-J)

4 skeptical audits vs the 227-tool registry. Coverage is ~90-95% of the Desktop authoring surface; the precise remaining gaps drive Waves K-N. (08-remaining-gaps.md section 1 is now STALE/over-pessimistic - many items there are built; this file supersedes it.)

## MODEL / TOM gaps -> Wave K (building)
- Relationships: add_relationship hard-codes Many->One; NO update/delete; cannot set 1:1, M:M, CrossFilteringBehavior=Automatic, SecurityFilteringBehavior, JoinOnDateBehavior, toggle IsActive on existing.
- Column SummarizeBy (default summarization) - entirely missing.
- In-model column DataType change (change_column_type is M-only).
- Column flags: IsKey/IsNullable/IsUnique/Alignment/EncodingHint.
- Measure: IsHidden (no hide), rename, description-update (update_measure misses these).
- rename_table.
- KPI: TrendExpression + TargetFormatString/StatusDescription/etc. (set_kpi too shallow).
- Hierarchy: level HideMembers/DisplayFolder, hierarchy DisplayFolder/visibility, delete, level editing.
- Model-level settings: DiscourageImplicitMeasures, DefaultMode, DirectLakeBehavior, Culture, DISABLE AUTO-DATE-TIME (production OOM fix).
- Role member remove + permission change; perspective member remove + delete.
- First-class DataSource objects (Structured/Provider + credential/impersonation).
- Variations list/remove; incremental-refresh Hybrid mode/polling.
- Composite-model source groups + partition source-kind switching (DEFERRED-COMPLEX, flag).

## POWER QUERY gaps -> Wave L
- Error rows: Remove/Keep Errors (Table.RemoveRowsWithErrors/SelectRowsWithErrors), Replace Errors (Table.ReplaceErrorValues).
- Remove blank rows (dedicated), Remove bottom rows (Table.RemoveLastN), Remove alternate rows (Table.AlternateRows).
- replace_values is text-only -> add Replacer.ReplaceValue (whole-cell/numeric/null replace, e.g. null<->0).
- Split column INTO ROWS; split_column fidelity (hard-codes 2 output cols -> support N).
- Demote headers (Table.DemoteHeaders).
- Move column; Conditional column builder (structured if/then/else); Detect type / locale-aware type change.
- Fuzzy merge (Table.FuzzyNestedJoin) + fuzzy group (Table.FuzzyGroup).
- Combine files from folder (Folder.Files -> Binary.Combine fan-out).
- More connectors in add_source_step: Json/Xml/Pdf/Html/AnalysisServices(MDX/DAX)/Oracle/MySQL/PostgreSQL/Odbc/OleDb/AzureStorage.Tables/DataLake/DeltaLake/Cdm/Dataverse/Salesforce; Web POST body/ApiKeyName/ManualStatusHandling; OData $filter/$select/$top.
- merge_columns combiners (lengths/positions/each-delimiter).
- Enable-load / include-in-refresh toggle; reference query; duplicate query; view-native-query/diagnostics (lower priority).

## VISUAL structure gaps -> Wave M (+ the critical encoder fix)
CRITICAL: set_visual_format silently CORRUPTS any property whose value is a nested object lacking a top-level expr/solid key (Encode stringifies it), and the validator does NOT warn (object-typed props pass leniently). FIX: encoder handles arbitrary nested JSON objects (deep-clone verbatim) + validator warns/blocks on malformed object props.
Dedicated builders needed (14):
1. plot-area background image; 2. re-source an existing image visual (sourceFile ResourcePackageItem); 3. rich multi-run textbox (paragraphs[] runs: per-run font/color/bold/hyperlink, bullet/numbered, sub/superscript); 4. gradient-stop sets outside table CF (treemap/funnel/map saturation min/center/max); 5. map conditional formatting (fillRule on filledMap/azureMap); 6. azureMap reference-layer + tile-layer data sources; 7. shapeMap custom topojson/geojson upload; 8. error bars (by-field/by-percentage + error band); 9. anomaly detection; 10. full forecast (length/units/ignore-last/confidence/seasonality); 11. scatter Play Axis binding; 12. cardVisual image hero/callout image; 13. slicer + button-slicer conditional formatting; 14. aiNarratives dynamic-value bindings.

## REPORT-FEATURE gaps -> Wave N
- Pages: rename existing page (displayName), reorder (ordinal), resize existing page + canvas presets (16:9/4:3/Letter/Tooltip).
- Filters: types TopN, RelativeDate, RelativeTime, Include/Exclude, advanced multi-condition And/Or; filter-card formatting (requireSingleSelect/inverted); filterSortOrder.
- Bookmarks: capture DATA state (filter/slicer values, sort, drill position, cross-highlight, field-param selection) - currently visibility-only (BIGGEST functional gap); bookmark groups/folders.
- Drillthrough: cross-report target (referenceScope=CrossReport); carried fields via PBIR pageBinding.parameters (currently legacy howCreated=5); saved drill position (expansionStates seeded empty).
- Tooltips: field tooltip-page binding (pageBinding.parameters); extra fields in default tooltip (Tooltips field-well).
- Buttons: Drillthrough/Q&A/WebUrl action types; standalone Back button; conditional/data-bound actions.
- Conditional formatting: field-value style (measure returns colour) for bg/font/icons; Web URL CF; icon-set picker + glyph + layout; data-bar fixes (reverseDirection/hideText/axis-color/min-max - min==max bug); percentage/percentile/text/blank conditions; CF driven by a column (not only measure).
- Mobile: per-visual phone show/hide; mobile-specific formatting; phone page settings.
- Accessibility: page-level tab-order list; showItemsWithNoData on a projection.
- Custom visuals: organizationCustomVisuals (only public emitted).
- PBIR (enhanced format) read/write - legacy Layout only today (future-proofing; large).

## Community-comb findings (6 agents in flight) -> Wave O+ (SVG measures, dynamic titles, calendar generators, DAX measure-template generators, etc.)
