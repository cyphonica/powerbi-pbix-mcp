# Remaining programmatic gaps (non-visual-property) - after Waves A-E

Verified vs the ~150-179 shipped tools + the capability map. Per-visual formatting properties tracked separately (09-visual-catalog). set_visual_format already sets ANY property by name; these are the structural/feature gaps.

## 1. LOCAL REPORT features still missing
- **Visual calculations** - author visualCalculations[] (in-visual DAX: RUNNINGSUM/MOVINGAVERAGE/PERCENTOFTOTAL/COLLAPSE/EXPAND over the visual matrix). Distinct from model measures.
- **Native sparklines** - add a sparkline column to a table/matrix (objects.sparkline + line measure). add_kpi_card only fakes one.
- **Drill-down/up config** - set drillable hierarchy axis, expand-to-next-level, expansionStates (saved drill state).
- **Drill-through field config** - add_drillthrough exists but not wiring pageBinding.parameters to carried fields / keep-all-filters / cross-report target fields.
- **Tooltip-page field binding** - set_visual_tooltip_page/set_page_type exist but not binding tooltip pageBinding.parameters to hovered field(s).
- **Page navigator / bookmark navigator** built-in visuals (auto-listing pages/bookmarks); Q&A/drillthrough button action subtypes.
- **Full report THEME authoring as a tool** - generate_theme/modify_theme do not expose the whole schema: dataColors[], good/neutral/bad, max/center/min/null CF stops, structural colours (firstLevelElements...secondaryBackground/tableAccent), textClasses (callout/title/header/label + secondaries), named visualStyles{} presets.
- **Mobile layout detail** - per-visual mobile show/hide, mobile-specific formatting overrides, phone-page settings (beyond auto_mobile_layout/set_mobile_position).
- **Accessibility** - visual alt text (objects.general.altText), tab order (position.tabOrder + page tab-order list), showItemsWithNoData.
- **Personalization / inline exploration** - allowInlineExploration + per-visual personalizeVisual.
- **Custom visual import/registration** - register a .pbiviz into resourcePackages / publicCustomVisuals / organizationCustomVisuals.
- **Wallpaper** - the page wallpaper object (grey margin), distinct from set_page_background (canvas area).
- **PBIR read/write path** - engine edits legacy Report/Layout; no native PBIR (enhanced) folder reader/writer (becomes the only format at GA).

## 2. LOCAL MODEL (TOM) features still missing
- **Linguistic schema / Q&A synonyms** - Culture.LinguisticMetadata (synonyms/phrasings) not authorable (set_translation covers ObjectTranslations only).
- **Object-translation depth** - confirm hierarchies/levels/perspectives + displayFolder translations.
- **Table detail rows** - Table.DefaultDetailRowsDefinition (set_detail_rows covers measure-level only).
- **Calc-group precedence/ordinal** - CalculationGroup.Precedence + CalculationItem.Ordinal not set.
- **Hybrid tables / DirectLake** - partition Mode=DirectLake, DataCoverageDefinition (hybrid), Model.DirectLakeBehavior.
- **Composite-model source groups** - multiple source groups, Dual mode, remote source group.
- **KPI status-graphic catalogue** - validate/select named StatusGraphic (Traffic Light/Three Flags/Road Signs...).
- **Annotations / extended properties** - read/write Annotation + ExtendedProperty (String/Json) on any object.
- **TMSL admin ops** - backup/restore/attach/detach/synchronize/sequence (XMLA/service-adjacent).
- **TMDL import** - export_tmdl writes out; no TMDL deserialize/import (TmdlSerializer read folder -> live model).
- **Data-source objects** - first-class StructuredDataSource/ProviderDataSource + credential/impersonation (separate from M text).
- **VertiPaq / model-health metadata** - DISCOVER_STORAGE_* / INFO.* / TMSCHEMA_* / CALCDEPENDENCY for memory profiling + lineage.

## 3. POWER QUERY features still missing
- **Connector-specific source steps** - ergonomic source generators (OData.Feed +$filter/$select, Web.Contents +RelativePath/Headers/POST, AzureStorage.Blobs, DeltaLake.Table, AnalysisServices.Database, Folder.Files combine).
- **M parameters** - create/manage PQ parameters (NamedExpression + ParameterValuesColumn), distinct from what-if/field params.
- **Query folding hints** - Table.Buffer, Table.StopFolding, Table.View custom folding, view-native-query.
- **More discrete transforms not in Wave D** - unpivot (Table.Unpivot/UnpivotOtherColumns), merge columns (CombineColumns), reorder columns, duplicate column, expand record/list/table columns, keep/remove top/bottom-N (FirstN/LastN/Skip), sort (Table.Sort), select-columns (keep), single-column text/number/date transforms (TransformColumns).
- **Dataflows (Gen1/Gen2)** authoring (model.json / Gen2 query defs).
- **Custom connector SDK** (.pq/.mez) - out of normal scope.

## 4. DEFERRED SERVICE / cloud tier (Wave F - entirely unbuilt; engine is local-only today)
publish via Fabric getDefinition/updateDefinition (push TMDL/PBIR) + import .pbix + workspace/dataset/report CRUD; dataset refresh (enhanced async + schedule); executeQueries (DAX on published); ExportToFile (PDF/PPTX/PNG headless); Scanner API; Git integration; Deployment pipelines; gateway/credential/parameter mgmt; push/streaming datasets (retiring 2027); paginated/RDL. Dead-ends: datamarts (retired), email subscriptions (UI-only). Auth feasible: a hosting layer can hold Dataset.ReadWrite.All / Report.ReadWrite.All / Workspace.Read.All.
