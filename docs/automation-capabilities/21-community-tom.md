# Community Tabular Editor/TOM comb - model power-user recipes -> Wave R (model internals) + Wave U (BPA/quality)

From Tabular Editor docs/BPA, SQLBI, m-kovalsky/Tabular, semantic-link-labs, esbrina, crossjoin, Data-Marc. Richest comb. Many CONFIRM existing waves; new/high-value below.

## "You'd never have thought of it" shortlist (build these)
1. **VERIFY add_field_parameter**: ParameterMetadata {"version":3,"kind":2} on hidden Fields col + display col SortByColumn=Order AND GroupByColumns.Add(Fields) + Fields/Order hidden. Body ("Name", NAMEOF('T'[Col]), order) tuples; text after a valid ref is ignored (= include field twice). What-if uses {"version":0} + GENERATESERIES. (DAX comb flagged this too - MUST confirm C2 impl.)
2. OLS measure-hiding via fake empty-table dependency (OLS can't target a measure; reference a secured/empty hidden table in an unused VAR).
3. lineage-tag strip-on-clone (clone copies lineageTag/sourceLineageTag -> Direct Lake "Duplicate sourceId" + git merge conflicts; clone tools MUST strip).
4. DiscourageImplicitMeasures=true + model aiInstructions (Copilot ignores the flag - needs AI prompt).
5. dataCoverageDefinition DAX-on-DQ-partition (have Wave I; ensure it targets the DQ partition + validates vs refresh range).
6. dynamic-format-string UNICHAR(8204) preservation sentinel.
7. calc-group precedence "only highest-precedence group's format string applies" model-wide reasoning (Precedence=apply order, Ordinal=display sort).
8. semantic TMDL serializer (omit-at-default, bare-name=true bools, ref = ordering manifest, triple-backtick fences, doubling-quote, createOrReplace only) - for import_tmdl/export_tmdl fidelity.

## TIER 1 special-structure (field param #1, what-if #2, auto-date purge #3, mark-as-date triple #4, KPI builder #5) - mostly mapped to K/C2/I; verify recipes.

## TIER 2 UDFs + calc groups
- DAX UDFs (FUNCTION) - typed params VAL/EXPR, scalar subtypes, ref types (ANYREF/MEASUREREF/COLUMNREF/TABLEREF/CALENDARREF/TABLE), optional defaults, CL>=1702 (Wave P define_udf).
- calc-group selection expressions noSelectionExpression/multipleOrEmptySelectionExpression + model selectionExpressionBehavior (CL1605) (Wave R).
- calc-group scaffold: hidden Ordinal col + Name SortByColumn=Ordinal + CalculationGroupSource() partition + DiscourageImplicitMeasures=true (verify Wave C1).

## TIER 3 perf levers (no Desktop UI) -> Wave R
- set_isavailableinmdx (bulk false; KEEP true on SortByColumn targets else "invalid column ID").
- set_encoding_hint (Default/Value/Hash bulk).
- stamp_vertipaq_stats (annotations; support both semantic-link-labs Vertipaq_* and m-kovalsky key schemes).

## TIER 4 Direct Lake / composite / aggregations -> Wave R
- set_aggregation AlternateOf (have C2; BaseColumn XOR BaseTable by summarization GroupBy/Sum/Count/Min/Max).
- auto_aggregations full scaffold ({Table}_Agg + _Agg measures + IF-routing + annotations).
- dataCoverageDefinition (have I).
- directlake suite: warm_cache (EVALUATE TOPN), guardrails/fallback-reason/unsupported-objects, schema compare/sync, DirectLakeBehavior enum (CL1604).
- composite/DQ-over-AS: EntityPartitionSource + source groups + Dual-mode dims + DiscourageCompositeModels (CL1560).

## TIER 5 lifecycle metadata -> Wave R
- lineage-tag strip-on-clone; detail rows (have); DiscourageImplicitMeasures+aiInstructions; OLS measure-hide; securityFilteringBehavior (None CL1566, have K); display-folder \ nesting + ; multi-folder + number-prefix ordering; DataAccessOptions {FastCombine/LegacyRedirects/ReturnErrorValuesAsNull} (have R-target); calendar-based native time intelligence (calendar + calendarColumnGroup, new).

## TIER 6 introspection / migration -> Wave R/U
- provenance annotations (CreatedThrough/AUTOGEN/@-namespaced) idempotent tag-find.
- MasterModel deployment ($perspective_Hide/Unhide/Remove/Expression/UpdatePartitionQuery/RLS annotations).
- migration ledger (store calc-table DAX as model annotation).
- INFO.* / INFO.VIEW.* introspection (Wave P A2).
- dependency/unused analyzers (measure_dependency_tree, get_dax_query_dependencies, DISCOVER_CALC_DEPENDENCY, RI-violation/blank-row finder, used_in_* family).
- report-level-measure extraction (Report/Layout config->modelExtensions->entities->measures; have E add_report_measure - add an EXTRACT/promote tool).
- case-sensitive DAX fixer (rewrite refs to exact casing for DQ/Direct Lake).

## TIER 7 TMDL fidelity + deployment -> Wave R + (service tier F)
- semantic TMDL serializer rules (see #8); M-parameter meta [IsParameterQuery=true] + QueryGroups slash-Folder + PBI_QueryGroupOrder; changedProperty + PBI_RemovedChildren for synced models (M2 from doc19); deployment: TMSL refresh types, backup/restore .abf, Update Datasources, scale-out, RLS-vs-OLS deploy asymmetry (mostly service tier F); EvaluateAndLog workflow (Wave P A4).

## *** BPA-as-tools -> Wave U (quality/lint suite) ***
Two rulesets: TabularEditor/BestPracticeRules (~29: DAX/Formatting/Metadata/Layout/Naming/Performance) + semantic-link-labs _model_bpa_rules.py (~60). Every rule's fix names a precise TOM property -> ship each as lint + autofix. Build: run_bpa(rules?) -> findings; fix_bpa(rule, autofix). High-value: ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS, DAX_DIVISION_COLUMNS(DIVIDE), USE_TREATAS_INSTEAD_OF_INTERSECT, SUMMARIZE_NONE, AVOID_FLOATING_POINT, MARK_PRIMARY_KEYS, HIDE_FOREIGN_KEYS, INACTIVE_RELATIONSHIPS, UNUSED_COLUMNS/MEASURES, EVALUATEANDLOG-in-production. (We have analyze_model/quality_gate/audit_robustness - extend into a full BPA engine.)

## COMPAT-LEVEL gate map (tools must auto-bump/validate)
DataAccessOptions+EncodingHint=1400; powerBI_V3=1450; AlternateOf=1460; QueryGroups=1480; LineageTag=1540; SourceLineageTag=1550; DiscourageCompositeModels=1560; dataCoverageDefinition/Hybrid=1565; securityFilteringBehavior None=1566; MaxParallelismPerQuery=1569; FormatStringDefinition=1601; DirectLakeBehavior=1604; selection expressions=1605; DAX UDFs=1702.
