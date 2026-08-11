# Community Power Query comb - generators/primitives/folding -> Wave Q (+ some fold into L)

From gorilla.bi, Chris Webb, Imke Feldmann (BIccountant), Ben Gribaudo, Goodly, powerquery.how. Engine already appends any M transform + connectors + folding hints.

## TIER 1 - high-value generators (build first)
- generate_calendar_table_m (List.Dates + ~25 part columns; params start/end/fiscal-start/locale) - highest ROI.
- generate_fiscal_calendar_m (FiscalYearEnd -> fiscal year/month/quarter).
- generate_445_calendar_m (retail 4-4-5/4-5-4/5-4-4) - differentiator.
- paginated_rest_listgenerate (List.Generate page loop; offset/limit AND cursor/next-link modes; auto-buffer).
- combine_folder_files (Folder.Files -> filter -> per-file fn -> Table.Combine); CSV/Excel/current-workbook variants.
- combine_robust_schema_drift (Table.Combine not Expand-with-sample -> keeps new files' extra columns; fixes MS default defect).
- combine_keep_filename_skip_errors (AddColumn+expand keeps [Name]/[Folder Path]; try..otherwise per file).
- rename_columns_from_mapping (Table.RenameColumns from a control table + MissingField.Ignore).
- transform_all_column_names (Table.TransformColumnNames - bulk header normalise, schema-agnostic).
- running_total_m (FAST List.Accumulate/Generate + List.Buffer; plain + reset-within-group).
- parameter_table_driver / dynamic_file_path (portable sources from a param/named-range table).

## TIER 2 - missing transform primitives (some -> Wave L)
fuzzy_merge (Table.FuzzyNestedJoin + Threshold/IgnoreCase/Space/TransformationTable), fuzzy_cluster_column (Table.AddFuzzyClusterColumn), group_keep_all_columns (Table.Group {each _} + expand), table_group_custom_comparer (GroupKind.Local gap-islands + comparer), split_text_to_rows (Text.Split + ExpandListColumn + special-char delimiters), concatenate_with_group_by (Text.Combine), pivot_text_values (Text.Combine 5th arg), unpivot_keep_nulls, dynamic_unpivot_other_columns, replace_errors_all_columns (Table.ReplaceErrorValues over all cols), date_range_join (non-equi window), crossjoin_all_combinations, allocate_value_across_periods, multiple_replacements_from_table (List.ReplaceMatchingItems - parallel), conditional_column_cascade (rules table -> nested if/switch).

## TIER 3 - folding (deep differentiator)
table_view_custom_folding (Table.View handlers: GetType/GetRows mandatory, OnTake [free preview win], OnSkip, OnSelectColumns, OnSort, OnSelectRows, GetRowCount), table_view_oninvoke_native (OnInvoke/Table.ViewFunction/OnNativeQuery), fold_rejection_and_validation (Table.ViewError, decline-unfoldable), value_nativequery_folding (Value.NativeQuery EnableFolding=true), folding lints (stack foldable before materialise; probe don't hardcode).

## TIER 4 - Web.Contents refresh correctness (LINTS)
web_contents_relativepath_query (static base URL + RelativePath + Query record; auto-fix concatenated URLs), web_contents_options (Headers/Content POST/ManualStatusHandling/IsRetry/ExcludedFromCacheKey/Timeout), skip_test_connection guidance metadata.

## TIER 5 - buffering heuristics (auto-apply in codegen)
buffer_inside_listgenerate (~70% faster), list_buffer_eager_cache (17x on per-row lists), table_buffer_modes (BufferMode=Delayed; only when read >1x; shallow-buffer caveat), binary_buffer_dedupe (one web request).

## TIER 6 - type system / robustness
type_ascription_vs_conversion (Value.ReplaceType ascribe-only vs .From validate; hosts validate on import), type_facets_and_claims (Type.ReplaceFacets NumericPrecision/Scale/MaxLength; Int8-64/Currency/Percentage), type_introspection (Type.RecordFields/ForRecord/FunctionParameters), table_schema_metadata_driven (Table.Schema -> dynamic typing/rename/select), table_profile_dq (Table.Profile), try_catch_error_records (try..catch, cell-scoped), safe_coerce_cascade (try Number.From otherwise...), flag_unmatched_rows.

## TIER 7 - introspection / meta / library
shared_function_explorer (#shared/#sections -> validate emitted call names + signatures), expression_evaluate_sandbox (Expression.Evaluate + auto-env), function_documentation_metadata (Documentation.* via Value.ReplaceType), section_library_mez (package helpers as .mez), List.Accumulate/Generate primitives.

## TIER 8 - dataflow / CDM export (NEW output format)
dataflow_modeljson_export (model.json: entities[]/attributes[]/partitions[]/relationships[]) + dataflow_mashup_embedding (pbi:mashup ext field). VERIFY: inspect a real exported model.json - the inner pbi:mashup layout is undocumented.

## TIER 9 - composable + gotchas
splitter/combiner factories, comparer functions, locale_type_conversion (3rd-arg culture - top silent-corruption source), csv_document gotchas (all-text, QuoteStyle.Csv), structured-parser followups (Json/Excel/Xml expansion), diagnostics_trace, join_kind templates (6 JoinKinds incl anti/self), lazy/streaming codegen rules.

PRIORITY: T1 calendar(+fiscal/445), paginated-REST + web-relativepath lint + buffer-in-listgenerate, combine-folder schema-drift, fuzzy merge/cluster, rename-from-mapping/group-keep-all/split-to-rows/running-total, Table.View OnTake folding, dataflow model.json (verify shape first).
