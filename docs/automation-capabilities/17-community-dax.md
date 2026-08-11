# Community DAX comb - generators + primitives -> Wave P

From sqlbi.com, daxpatterns.com, dax.guide, RADACAD. Engine already authors arbitrary measures/calc-tables/calc-groups/RLS. Split: (A) genuinely missing primitives, (B) high-value generators.

## A. Missing DAX primitives
- A1 **define_udf** - DAX User-Defined Functions (the new FUNCTION feature, compat 1702+, GA ~Jun 2026). NET-NEW model object class (functions.tmdl / Model.Functions) - we cannot author UDFs at all today. Typed params (Scalar/Table/ColumnRef/MeasureRef), val vs expr mode, defaults, /// JSDoc. Inspect via INFO.USERDEFINEDFUNCTIONS(). HIGH PRIORITY (new capability).
- A2 **info_view / inspect_model** - wrapper that EVALUATEs INFO.* (esp. INFO.VIEW.TABLES/COLUMNS/MEASURES/RELATIONSHIPS + INFO.CALCDEPENDENCY lineage + *STORAGES). Powers model documentation + dependency/impact analysis. (model_health from Wave I partially covers DMVs; extend with INFO.VIEW.*)
- A3 **column_statistics** - EVALUATE COLUMNSTATISTICS() (Min/Max/Cardinality/MaxLength per column). Instant profiling.
- A4 **inject_evaluateandlog / strip_evaluateandlog** - wrap/unwrap EVALUATEANDLOG(value,label,maxRows) for debugging (MUST strip after).
- A5 set_dynamic_format_string - HAVE (C1); add templates (currency-by-selection, K/M/B SWITCH, %-vs-number).
- A6 set_detail_rows - HAVE (C1/I); add a SELECTCOLUMNS field-list generator form.

## B. High-value parameterised generators (compose add_measure/add_calculation_group/add_role)
Common slots: {factTable,valueColumn,baseMeasure,dateTable,dateColumn,dimensionTable,columns[]} + flags (filter scope ALL|ALLSELECTED|ALLEXCEPT, rank order/ties, calendar Gregorian|Custom).
- B1 **add_time_intelligence_measures** (flagship) - YTD/QTD/MTD, PY/PM, MoM/YoY +%, PYTD/YOYTD +%, ShowValueForDates guard, fiscal-year option, DirectQuery integer-compare mode.
- B2 add_running_total (over date + over generic sort-order/Pareto).
- B3 add_moving_average (rolling N over Year-Month-Number).
- B4 add_percent_of_total / add_percent_of_parent (ALL/ALLSELECTED; % of parent over hierarchy levels - multi-measure, usually hand-written wrong).
- B5 add_rank_measure (RANKX + ISINSCOPE + ALLSELECTED + blank guard; order ASC/DESC, ties SKIP/DENSE, within-group).
- B6 add_semiadditive_measures (opening/closing balance, last/first-non-blank, sparse-snapshot robust form).
- B7 add_dynamic_segmentation (disconnected band table + classify measure; left-open/right-closed bounds).
- B8 add_abc_classification (Pareto - static calc-columns + dynamic measure variants + class table).
- B9 add_dynamic_topn (UNION + Others row + what-if N + rank-or-others measure).
- B10 **add_time_intelligence_calc_group** (Current/MTD/QTD/YTD/PY/PYTD/YoY/YoY% items + formatStringDefinition + ordinal/precedence + DiscourageImplicitMeasures).
- B11 add_currency_conversion_calc_group (SUMX over Date + rate + format-string by currency).
- B12 **add_dynamic_rls** - role filter generators: USERPRINCIPALNAME() simple, bridge/many-to-many, parent-child PATH()/PATHCONTAINS, LOOKUPVALUE. (We have RLS via raw DAX; no generator.) Caveat: never RLS the user/lookup table.
- B13 **add_custom_calendar_time_intelligence** (445/weekly/13-period) - the ONE case built-in TI breaks; build index columns + integer-index *TD/PY/PYTD/prev-week/MAT(364) math.

## VERIFY (validation pass)
- add_field_parameter must emit extended property ParameterMetadata {version:3, kind:2} on the hidden Fields column + a SortBy binding to the ordinal, else the parameter is non-functional. Confirm C2's impl did this.

Highest-leverage first: A1 (UDFs net-new), B1, B10, B12, B13, A2.
