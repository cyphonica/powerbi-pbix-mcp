> **Tool counts below are historical.** The hand-written figures in this folder ("~124 tools", "227-tool registry", per-domain counts) date from earlier waves and are not maintained. The authoritative, machine-generated tool list is [GENERATED-TOOL-INDEX.md](GENERATED-TOOL-INDEX.md), produced by `SuperBiMcp capability-map` from the [McpServerTool] attributes and drift-checked in CI.

# Power BI automation - master capability map + gap analysis vs Super-BI-MCP

Synthesised from the 7 learn.microsoft.com combs (01-07 in this folder). Goal: every ability possible in code, mapped against the engine's ~124 tools, with the gap and a prioritised roadmap.

## The surface splits into two domains

| Domain | What it is | Our engine today |
|---|---|---|
| **LOCAL authoring** | Edit a .pbix/model/report on disk or a live local model: TOM (semantic model), Report/Layout + PBIR (report), Power Query M (data prep) | This IS our engine. Strong coverage; specific gaps below |
| **SERVICE automation** | Operate the Power BI / Fabric cloud: REST (~250 ops), Fabric REST (getDefinition/updateDefinition, git, pipelines, job scheduler), PowerShell, JS embedding, Python Semantic Link | NOT covered - the engine is local-only today |

---

## LOCAL - MODEL (TOM). We have ~40 model/measure tools.
HAVE: tables (CSV/M/calculated), data + calculated columns, measures (+narrative, update/delete/list), DAX validate/run, relationships (+ check/infer), hierarchies, date table, time-intelligence, shared M expressions, list_m, format_column, sort_column_by, visibility, rename, preview, refresh_table, analyze_model, quality_gate, export_tmdl, generate_pbip, audit_robustness, conform_dimension, sentinel snapshot/diff.

GAP (-> Wave C): **RLS roles + members + filter expressions**, **OLS** (table/column), **calculation groups + items**, **field parameters**, **what-if parameters**, **perspectives**, **model KPIs**, **detail rows** (drillthrough), **dynamic format-string definitions**, **cultures/translations + Q&A synonyms**, **AlternateOf aggregations**, **incremental RefreshPolicy**, **variations**, **column DataCategory**, **display folders** (measure/column), **query-group folders**, **mark-as-date-table**, annotations/extended properties. (TMSL backup/restore/attach/detach/sequence + TMDL round-trip = mostly service/advanced.)

## LOCAL - REPORT (Layout / PBIR). We have ~75 report tools.
HAVE: pages CRUD + clone, all common visuals (card/slicer/table/matrix/chart/textbox/shape/image/button/kpi), generic get/set_visual_format + typed setters (title/background/border/position/data-labels/legend/axis/data-color/totals/sort), themes (apply/read/generate/modify), bookmarks (list/add/update/delete), action buttons + nav + view-switcher, drillthrough, mobile layout, CF (background + colour-scale + discrete bands), recipes, auto-arrange/align. **Wave A added**: page/report filters + remove, edit-interactions, filter-pane hide/lock, sync slicers.

GAP (-> Wave B, running): **group/ungroup + z-order**, **tooltip pages + page-type**, **analytics lines** (constant/avg/min/max/trend/forecast), **CF variants** (font colour / data bars / icons).
GAP (-> Wave E, report polish): report-level **ExplorationSettings** (filter-action default, drill-filters-others, cross-report drillthrough, persistent state, export mode, pages position...), **report-level measures** (reportExtensions), page **displayOption/visibility**, the **selector model** for per-series / per-category / totals formatting (deeper than current set_visual_format), **bookmark options** (data/display/current-page/selected-visuals) + display modes (**spotlight / maximize / focus**), **field parameters** in visual state, expansionStates (drill state). Future: a **PBIR read/write path** (the GA report format).

## LOCAL - POWER QUERY (M). We have ~6 data tools.
HAVE: set_partition_m, add_table_from_m, set_shared_expression, list_m, create_csv_table, stage_excel_to_csv, unpivot_weekly_csv, list_excel_sheets + 17 source connectors (SQL/Excel/Sheets/Drive/SharePoint/OneDrive/Dropbox/Shopify/Xero/Woo/QuickBooks/Lists/...).

GAP (-> Wave D): high-level transform tools generating M - **merge_queries** (Table.NestedJoin+Expand), **append_queries** (Table.Combine), **pivot_column** (Table.Pivot), **group_by** (Table.Group), **split_column**, **replace_values**, **change_column_type**, **filter_rows**, **add_custom_column**, **add_index_column**, **remove/select/rename/reorder columns**, **fill_down/up**, **transpose**, **promote_headers**, **remove_duplicates**, **keep/remove top/bottom rows**. (We can write raw M today; these are the ergonomic discrete layer.)

## SERVICE automation (NOT covered - new tier, decision needed). See 01/05/06/07.
- **Publish / author-as-code**: Fabric getDefinition/updateDefinition (push our locally-built TMDL/PBIR to a workspace), import .pbix, workspace/dataset/report CRUD.
- **Operate**: enhanced async **refresh** + schedule, **executeQueries** (DAX on a published dataset), **ExportToFile** (headless PDF/PPTX/PNG render), parameter/datasource/credential update, bind to gateway.
- **Govern / CI-CD**: **Scanner API**, git integration, **deployment pipelines**.
- Nicher: push/streaming rows (retiring 2027), Gen1/Gen2 dataflows, paginated/RDL. Dead-ends (no API): datamarts (retired), email subscriptions (UI-only).
- Auth is solvable: a hosting layer can hold Power BI OAuth scopes (Dataset.ReadWrite.All, Report.ReadWrite.All, Workspace.Read.All) - a service tier is feasible via SP/OAuth.

---

## ROADMAP
1. **Wave A** - interactions + filters - DONE (committed 65ab8ec).
2. **Wave B** - report structure + analytics (group/z-order, tooltip pages, analytics lines, CF variants) - BUILDING.
3. **Wave C** - model/Tabular parity (RLS/OLS, calc groups, field/what-if params, perspectives, KPIs, detail rows, dynamic format strings, translations, aggregations, incremental refresh, variations, data category, display folders, query groups, mark-as-date-table).
4. **Wave D** - Power Query transform tools (the M-generating discrete layer above).
5. **Wave E** - report polish (ExplorationSettings, report-level measures, selector-based formatting, bookmark options + spotlight/maximize, field parameters, page display options).
6. **Wave F (optional, big)** - SERVICE tier: publish/deploy (updateDefinition), refresh, executeQueries, ExportToFile, scanner, git + deployment pipelines, gateway/credentials. Fundamentally new (cloud, not local .pbix) - confirm before building.

After A-E, the engine has full **local Desktop-authoring parity**. F adds the **cloud/service** half (everything the Power BI service + Fabric can do in code).
