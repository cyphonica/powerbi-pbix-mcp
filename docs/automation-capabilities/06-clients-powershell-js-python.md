# Client surfaces - PowerShell, JavaScript embedding, Python (Semantic Link)

Source: learn.microsoft.com (+ semantic-link-labs readthedocs for sempy_labs).

## PowerShell - MicrosoftPowerBIMgmt (42 cmdlets, 6 submodules)
- Profile: Connect/Disconnect-PowerBIServiceAccount, Get-PowerBIAccessToken, **Invoke-PowerBIRestMethod** (escape hatch for any REST op), Resolve-PowerBIError.
- Workspaces: Get/New/Set/Restore-PowerBIWorkspace, Add/Remove-PowerBIWorkspaceUser.
- Reports: Get/New/Remove/Export/Copy-PowerBIReport (New = upload .pbix), Get/New-PowerBIDashboard, Get/Copy-PowerBITile, Get-PowerBIImport.
- Data: Get/New/Set/Add-PowerBIDataset, Get-PowerBIDataflow/Datasource/Table, New-PowerBITable/Column, Add/Remove-PowerBIRow (push), Export-PowerBIDataflow.
- Admin: Get-PowerBIActivityEvent (audit), encryption keys. Capacities: Get-PowerBICapacity.
- Patterns: `-Scope Organization` (tenant-wide, admin) vs Individual; `-Profile` for multi-tenant. Gateway mgmt is in the separate DataGateway module.
- Breadth comes from Invoke-PowerBIRestMethod + -Scope, not a big cmdlet count.

## JavaScript - powerbi-client (embedding, read/interact, NOT model authoring)
- Service (window.powerbi): embed, bootstrap, load, preload, get, reset, createReport, quickCreate.
- Embeddable: report, visual, dashboard, tile, qna, paginated.
- Report: getPages/getActivePage/setPage/add/delete/renamePage, get/set/update/removeFilters, print, refresh, switchMode/switchLayout, updateSettings, applyTheme/getTheme, get/setZoom, moveVisual/resizeVisual/setVisualDisplayState, fullscreen, save/saveAs, on/off events. bookmarksManager (getBookmarks/apply/applyState/capture/play).
- Page: getVisuals/getVisualByName/getSlicers/getSmartNarrativeInsights, filters, setDisplayName, moveVisual/resizeVisual/resizePage/setVisualDisplayState.
- VisualDescriptor: filters, exportData (Summarized/Underlying, ~30k rows), get/setSlicerState, clone, sortBy, moveVisual/resizeVisual.
- Filters: Advanced/Basic/IncludeExclude/RelativeDate/TopN/Tuple/RelativeTime at report/page/visual level; FiltersOperations Add/Replace/ReplaceAll/RemoveAll; targets Column/Hierarchy/Measure/Aggr.
- Events: loaded, rendered, saved, buttonClicked, commandTriggered, dataSelected, pageChanged, selectionChanged, visualClicked, visualRendered, bookmarkApplied, error.
- Settings (ISettings): filterPaneEnabled, navContentPaneEnabled, panes, layoutType, background, bars, persistentFilters, personalBookmarks, etc.
- powerbi-report-authoring (separate pkg): page.createVisual, delete visual, changeType, add/remove fields, get/set visual properties.

## Python - Semantic Link (sempy.fabric core + sempy_labs extended)
- Core sempy.fabric: list_workspaces/datasets/tables/columns/measures/relationships/hierarchies/partitions/perspectives/calculation_items, read_table, evaluate_dax, evaluate_measure, refresh_dataset (full/clearValues/calculate/dataOnly + maxParallelism/commitMode/objects), execute_tmsl, execute_xmla, get_tmsl, get_item_definition, connect_semantic_model (TOM context manager), FabricRestClient/PowerBIRestClient, Trace.
- **sempy_labs (community) - the rich model authoring** (validates our Wave C scope): sempy_labs.tom.connect_semantic_model -> TOMWrapper with add_* (calc columns/tables, **calculation groups/items, measures, relationships, roles, hierarchies, perspectives, translations, incremental refresh, field parameters, time intelligence**), set_rls/set_ols/set_aggregations/mark_as_date_table/format_dax, run_model_bpa (best-practice analyzer), vertipaq_analyzer (memory), deploy_semantic_model, translate_semantic_model, directlake.*, migration (create_pqt_file), deployment_pipeline, git.

## GAP-RELEVANT for our engine
- PowerShell/JS/Python are alternative CLIENTS; our engine is itself a programmatic client (uses TOM + Layout). The notable reference: **sempy_labs.tom does exactly the model-authoring we are building in Wave C** (calc groups, RLS, OLS, field params, incremental refresh) - confirms the scope + the TOM property names.
- run_model_bpa (best-practice rules) + vertipaq_analyzer (memory) are model-health features we could add (we have analyze_model/quality_gate/audit_robustness already - could extend).
- JS embedding + PowerShell service ops are out of local scope (service-side).
