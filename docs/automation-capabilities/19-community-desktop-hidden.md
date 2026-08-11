# Community Desktop-hidden/preview comb - file-editable internals -> fold into K/I/N + Wave R

From brunner.bi, Tabular Editor blog, SQLBI, crossjoin, MS TMDL/PBIR docs + json-schemas. Many items CONFIRM what A-K already build; below = genuinely-new or reinforcing.

## MODEL (TMDL/TMSL)
- M1 disable auto date/time - __PBI_TimeIntelligenceEnabled=0 + purge LocalDateTable_<guid>/DateTableTemplate_<guid> + their rels/variations. (Wave K disable_auto_date_time - ensure it also PURGES leftovers.)
- M2 **sync-correctness annotations** (NEW, important): lineageTag, sourceLineageTag (=source name for Direct Lake), changedProperty (else local name/hidden/format/summarizeBy overrides wiped on schema sync), PBI_RemovedChildren (else removed tables/rels reappear). Build set_lineage_tag / declare_changed_property / mark_removed_children.
- M3 KPI (Wave K update_kpi). M4 OLS (have C1). M5 detailRows (have C1/I). 
- M6 column perf: encodingHint (Wave K column flags), **isAvailableInMdx=false** (NEW - high-cardinality perf).
- M7 aggregations alternateOf (have C2) + **alternateSourcePrecedence** (NEW).
- M8 relationship securityFilteringBehavior/relyOnReferentialIntegrity/crossFiltering (Wave K).
- M9 calc-group precedence (Wave K) + **multipleOrEmptySelectionExpression / noSelectionExpression** (NEW) + selectionExpressionBehavior + discourageImplicitMeasures (Wave K).
- M10 variations (have C2).
- M11 **dataAccessOptions {fastCombine(=ignore privacy in-file), legacyRedirects, returnErrorValuesAsNull}** + defaultPowerBIDataSourceVersion + model culture (NEW).
- M12 **table isPrivate** (hide helper tables from all clients) + provenance annotations PBI_QueryOrder/PBI_ResultType/PBI_NavigationStepName (round-trip fidelity) (NEW).

## Q&A LINGUISTIC (model) - biggest untouched surface -> extend Wave I set_synonyms (Wave R)
- Q1 Culture.LinguisticMetadata.Content JSON / cultures/<c>.tmdl - durable synonym/phrasing home (feeds Q&A + Copilot).
- Q2 synonym JSON (schema 1.0.0): Entities{Binding, Terms[], Weight 0-1, State Generated|Authored|Suggested|Deleted} - set Authored so terms stick, Deleted to suppress auto-synonyms.
- Q3 **LSDL YAML phrasings** (.lsdl.yaml schema 3.4.0) - 8 phrasing types (Verb/Adjective/Noun/Preposition/DynamicNoun...) - the only way to teach concepts ("happy customers").

## REPORT (PBIR; our engine = legacy Layout, concepts map, location differs)
- Filters F1-F3: isHiddenInViewMode/isLockedInViewMode (have N/Wave A), type enum, restatement/displayName/filterSortOrder/ordinal (Wave N).
- Page objects: pageRefresh (NEW - auto page refresh), personalizeVisual (have H), pageInformation altName/qnaPodEnabled, outspace/wallpaper (have H), outspacePane/filterCard styling, canvas displayOption ActualSizeTopLeft + displayArea.verticalAlignment.
- Report settings R1-R2: governance toggles (have E set_report_settings; ensure useStylableVisualContainerHeader, defaultDisplayUnitsToNone, slowDataSourceSettings, isPersistentUserStateDisabled).
- S1 syncGroup (have A). S2 drillthrough pageBinding (referenceScope CrossReport, parameters[].boundFilter/fieldExpr; name MUST be unique GUID) (Wave N). S3 tooltip page binding (have H + Wave N field binding).
- Visual chrome V1 header icons ~25 toggles (have via set_visual_format), V2 visualHeaderTooltip, V3 **dynamic alt text** (literal OR measure, 250 char) (have H set_alt_text - add measure-bound), V4 z/tabOrder (have B/H), V5 visualLink action (have buttons), V6 keepLayerOrder/drillFilterOtherVisuals, V7 cardVisual referenceLabels/smallMultiples ($id:default gotcha), V8 button-slicer multi-state CF (Wave M slicer CF), V9 slicer mode, V10 **error bars** (Wave M).
- X1 mobile.json per-visual (position + mobile objects overrides) - membership = file existence (have H partial; extend), X2 default landing page (annotations defaultPage + activePageName), X3 annotations for engine metadata, X4 reportExtensions.json report-measures (have E).

## DO NOT BUILD (not in file): on-object/format-pane-defaults/preview-flags (user profile + registry), spotlight (=bookmark display.mode), RLS view-as/showAllRoles (runtime).

## ARCHITECTURE NOTE: these schemas are PBIR. Our engine writes legacy Report/Layout (works in current Desktop). A PBIR read/write path = the real future-proofing (becomes only format at GA). pageBinding.name must be a unique GUID; fresh visuals may lack filterConfig until pane expanded.

## NEW items -> Wave R (model internals): M2 sync annotations, M6 isAvailableInMdx, M7 alternateSourcePrecedence, M9 selection expressions, M11 dataAccessOptions, M12 isPrivate/provenance, Q1-Q3 linguistic depth, pageRefresh.
