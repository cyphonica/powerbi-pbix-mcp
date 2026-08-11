using System.Globalization;

namespace SuperBiMcp.Services;

/// <summary>
/// Pure, side-effect-free generators for community DAX patterns (time intelligence, running totals,
/// ranking, ABC, segmentation, calc-group items, dynamic RLS, custom-calendar TI, UDFs, INFO/profiling
/// queries). Every method returns DAX text or a list of measure specs; nothing here touches a live
/// Analysis Services connection, so the whole surface is unit-testable against an in-memory model.
///
/// The mutation helpers that turn these specs into Tabular objects live in <see cref="ModelService"/>
/// (the *Core methods), so the report/model engine stays the single owner of SaveChanges().
/// </summary>
internal static class DaxGenerators
{
    /// <summary>A measure the generators want authored: home table, name, DAX, optional format + folder.</summary>
    public readonly record struct MeasureSpec(string Table, string Name, string Dax, string? Format, string? Folder);

    /// <summary>A calculation item the calc-group generators want authored.</summary>
    public readonly record struct CalcItemSpec(string Name, string Dax, int Ordinal, string? FormatStringExpression);

    // ---------------------------------------------------------------- reference helpers
    /// <summary>Normalise a Table[Column] / 'Table'[Column] reference to the quoted 'Table'[Column] form.</summary>
    public static string Col(string columnRef)
    {
        string s = (columnRef ?? "").Trim();
        int lb = s.IndexOf('[');
        int rb = s.LastIndexOf(']');
        if (lb <= 0 || rb <= lb) throw new InvalidOperationException($"column must be Table[Column] (got '{columnRef}').");
        string tbl = s[..lb].Trim().Trim('\'');
        string col = s[(lb + 1)..rb].Trim();
        if (tbl.Length == 0 || col.Length == 0) throw new InvalidOperationException($"column must be Table[Column] (got '{columnRef}').");
        return $"'{tbl}'[{col}]";
    }

    /// <summary>Quote a table name for DAX: 'Sales'.</summary>
    public static string Tbl(string table) => $"'{(table ?? "").Trim().Trim('\'')}'";

    /// <summary>A measure reference in bracket form: [Total Sales] (accepts a bare name or [name]).</summary>
    public static string Mref(string measure)
    {
        string s = (measure ?? "").Trim();
        if (s.StartsWith("[") && s.EndsWith("]")) return s;
        return $"[{s}]";
    }

    private static string Inv(double d) => d.ToString(CultureInfo.InvariantCulture);

    // ================================================================ A. primitives

    /// <summary>
    /// Author the DAX text of a User-Defined Function body. The TOM Function.Expression is the whole
    /// <c>name = (params) =&gt; body</c> form. params are {name,type}; type maps to DAX UDF type annotations.
    /// </summary>
    public static string UdfExpression(string name, IReadOnlyList<(string name, string type)> parameters,
        string bodyDax, string? returnType, string? description)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("UDF name is required.");
        if (string.IsNullOrWhiteSpace(bodyDax)) throw new InvalidOperationException("UDF body DAX is required.");
        var ps = (parameters ?? new List<(string, string)>())
            .Select(p =>
            {
                string pn = (p.name ?? "").Trim();
                if (pn.Length == 0) throw new InvalidOperationException("UDF parameter name is required.");
                string t = UdfType(p.type);
                return t.Length == 0 ? pn : $"{pn}: {t}";
            });
        string paramList = string.Join(", ", ps);
        string ret = string.IsNullOrWhiteSpace(returnType) ? "" : $": {UdfReturnType(returnType)}";
        string doc = string.IsNullOrWhiteSpace(description) ? "" : $"/// {description!.Replace("\n", " ")}\n";
        string body = bodyDax.Trim();
        return $"{doc}{name} = ({paramList}){ret} =>\n{body}";
    }

    private static string UdfType(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "" => "",
        "scalar" => "scalar val",
        "scalarval" or "scalar val" => "scalar val",
        "scalarexpr" or "scalar expr" => "scalar expr",
        "table" => "table val",
        "tableval" or "table val" => "table val",
        "tableexpr" or "table expr" => "table expr",
        "columnref" => "anyref",
        "measureref" => "anyref",
        "anyref" => "anyref",
        "numeric" => "numeric scalar val",
        "string" => "string scalar val",
        _ => throw new InvalidOperationException($"Unknown UDF param type '{type}'. Use Scalar, Table, ColumnRef, MeasureRef, AnyRef, Numeric or String."),
    };

    private static string UdfReturnType(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "scalar" => "scalar",
        "table" => "table",
        _ => (type ?? "").Trim(),
    };

    /// <summary>The EVALUATE text for an INFO view. view=TABLES|COLUMNS|MEASURES|RELATIONSHIPS|CALCDEPENDENCY|...</summary>
    public static string InfoViewQuery(string view)
    {
        string v = (view ?? "").Trim().ToUpperInvariant();
        if (v.Length == 0) throw new InvalidOperationException("view is required (e.g. TABLES, COLUMNS, MEASURES, RELATIONSHIPS, CALCDEPENDENCY).");
        // CALCDEPENDENCY has no INFO.VIEW variant - use the flat INFO function.
        return v switch
        {
            "CALCDEPENDENCY" or "CALCULATIONDEPENDENCY" => "EVALUATE INFO.CALCDEPENDENCY()",
            "TABLES" or "COLUMNS" or "MEASURES" or "RELATIONSHIPS" => $"EVALUATE INFO.VIEW.{v}()",
            _ => $"EVALUATE INFO.{v}()",
        };
    }

    /// <summary>The EVALUATE text for whole-model column profiling.</summary>
    public static string ColumnStatisticsQuery() => "EVALUATE COLUMNSTATISTICS()";

    /// <summary>Wrap a measure's DAX RETURN value in EVALUATEANDLOG for debugging.</summary>
    public static string InjectEvaluateAndLog(string dax, string? label)
    {
        string body = (dax ?? "").Trim();
        if (body.Length == 0) throw new InvalidOperationException("measure DAX is empty.");
        if (body.Contains("EVALUATEANDLOG", StringComparison.OrdinalIgnoreCase)) return body;   // idempotent
        string lbl = string.IsNullOrWhiteSpace(label) ? "" : $", \"{label!.Replace("\"", "\"\"")}\"";
        return $"EVALUATEANDLOG (\n{body}{lbl}\n)";
    }

    /// <summary>Remove an outer EVALUATEANDLOG wrapper, restoring the inner expression. Idempotent.</summary>
    public static string StripEvaluateAndLog(string dax)
    {
        string body = (dax ?? "").Trim();
        int at = body.IndexOf("EVALUATEANDLOG", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return body;
        int open = body.IndexOf('(', at);
        if (open < 0) return body;
        // find the matching close paren for this open
        int depth = 0, close = -1;
        for (int i = open; i < body.Length; i++)
        {
            if (body[i] == '(') depth++;
            else if (body[i] == ')') { depth--; if (depth == 0) { close = i; break; } }
        }
        if (close < 0) return body;
        string inner = body[(open + 1)..close].Trim();
        // strip a trailing , "label" (and optional , maxrows) added by the inject helper
        int lastComma = FindTopLevelLastComma(inner);
        if (lastComma > 0)
        {
            string tail = inner[(lastComma + 1)..].Trim();
            if (tail.StartsWith("\"") || long.TryParse(tail, out _))
                inner = inner[..lastComma].Trim();
        }
        return inner;
    }

    private static int FindTopLevelLastComma(string s)
    {
        int depth = 0, last = -1; bool inStr = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') inStr = !inStr;
            else if (!inStr && c == '(') depth++;
            else if (!inStr && c == ')') depth--;
            else if (!inStr && c == ',' && depth == 0) last = i;
        }
        return last;
    }

    // ================================================================ B5. time intelligence measures
    /// <summary>
    /// The flagship set: YTD/QTD/MTD, PY, PYTD, MoM/YoY (+%), each guarded by a ShowValueForDates check
    /// so future/empty dates show blank. fiscalYearEnd is "MM-DD" (e.g. "06-30") for fiscal *TD.
    /// </summary>
    public static IReadOnlyList<MeasureSpec> TimeIntelligenceMeasures(string table, string baseMeasure,
        string dateTable, string dateColumn, string? fiscalYearEnd)
    {
        string m = Mref(baseMeasure);
        string dcol = $"'{dateTable}'[{dateColumn}]";
        string folder = $"{StripBrackets(baseMeasure)} Time Intelligence";
        string fye = string.IsNullOrWhiteSpace(fiscalYearEnd) ? "" : $", \"{fiscalYearEnd}\"";
        string guard = $"IF ( NOT ISEMPTY ( DATESBETWEEN ( {dcol}, MIN ( {dcol} ), MAX ( {dcol} ) ) ),";

        MeasureSpec S(string name, string body) =>
            new(table, $"{StripBrackets(baseMeasure)} {name}", body, null, folder);

        var list = new List<MeasureSpec>
        {
            S("YTD",  $"CALCULATE ( {m}, DATESYTD ( {dcol}{fye} ) )"),
            S("QTD",  $"CALCULATE ( {m}, DATESQTD ( {dcol} ) )"),
            S("MTD",  $"CALCULATE ( {m}, DATESMTD ( {dcol} ) )"),
            S("PY",   $"CALCULATE ( {m}, SAMEPERIODLASTYEAR ( {dcol} ) )"),
            S("PM",   $"CALCULATE ( {m}, DATEADD ( {dcol}, -1, MONTH ) )"),
            S("PYTD", $"CALCULATE ( {m}, SAMEPERIODLASTYEAR ( DATESYTD ( {dcol}{fye} ) ) )"),
        };

        string self = StripBrackets(baseMeasure);
        list.Add(new(table, $"{self} YoY", $"{m} - [{self} PY]", null, folder));
        list.Add(new(table, $"{self} YoY %", $"DIVIDE ( {m} - [{self} PY], [{self} PY] )", "0.0%;-0.0%;0.0%", folder));
        list.Add(new(table, $"{self} MoM", $"{m} - [{self} PM]", null, folder));
        list.Add(new(table, $"{self} MoM %", $"DIVIDE ( {m} - [{self} PM], [{self} PM] )", "0.0%;-0.0%;0.0%", folder));
        list.Add(new(table, $"{self} YOYTD", $"[{self} YTD] - [{self} PYTD]", null, folder));
        list.Add(new(table, $"{self} YOYTD %", $"DIVIDE ( [{self} YTD] - [{self} PYTD], [{self} PYTD] )", "0.0%;-0.0%;0.0%", folder));
        return list;
    }

    // ================================================================ B6. running total
    public static MeasureSpec RunningTotalOverDate(string table, string baseMeasure, string dateTable, string dateColumn)
    {
        string m = Mref(baseMeasure);
        string dcol = $"'{dateTable}'[{dateColumn}]";
        string dax = $"CALCULATE (\n    {m},\n    FILTER ( ALLSELECTED ( {dcol} ), {dcol} <= MAX ( {dcol} ) )\n)";
        return new(table, $"{StripBrackets(baseMeasure)} Running Total", dax, null, $"{StripBrackets(baseMeasure)} Cumulative");
    }

    public static MeasureSpec RunningTotalOverColumn(string table, string baseMeasure, string sortColumn)
    {
        string m = Mref(baseMeasure);
        string col = Col(sortColumn);
        string dax = $"CALCULATE (\n    {m},\n    FILTER ( ALLSELECTED ( {col} ), {col} <= MAX ( {col} ) )\n)";
        return new(table, $"{StripBrackets(baseMeasure)} Running Total ({StripBrackets(sortColumn)})", dax, null, $"{StripBrackets(baseMeasure)} Cumulative");
    }

    // ================================================================ B7. moving average
    public static MeasureSpec MovingAverage(string table, string baseMeasure, string dateTable, string dateColumn, int periods)
    {
        if (periods <= 0) throw new InvalidOperationException("periods must be greater than zero.");
        string m = Mref(baseMeasure);
        string dcol = $"'{dateTable}'[{dateColumn}]";
        string dax =
            $"VAR _lastDate = MAX ( {dcol} )\n" +
            $"VAR _window = DATESINPERIOD ( {dcol}, _lastDate, -{periods}, DAY )\n" +
            "RETURN\n" +
            $"AVERAGEX ( _window, CALCULATE ( {m} ) )";
        return new(table, $"{StripBrackets(baseMeasure)} {periods}-Day Moving Avg", dax, null, $"{StripBrackets(baseMeasure)} Rolling");
    }

    public static MeasureSpec MovingAverageMonths(string table, string baseMeasure, string dateTable, string dateColumn, int periods)
    {
        if (periods <= 0) throw new InvalidOperationException("periods must be greater than zero.");
        string m = Mref(baseMeasure);
        string dcol = $"'{dateTable}'[{dateColumn}]";
        string dax =
            $"AVERAGEX (\n    DATESINPERIOD ( {dcol}, MAX ( {dcol} ), -{periods}, MONTH ),\n    CALCULATE ( {m} )\n)";
        return new(table, $"{StripBrackets(baseMeasure)} {periods}-Month Moving Avg", dax, null, $"{StripBrackets(baseMeasure)} Rolling");
    }

    // ================================================================ B8. percent of total / parent
    public static MeasureSpec PercentOfTotal(string table, string baseMeasure, string dimension, string? scope)
    {
        string m = Mref(baseMeasure);
        string dim = Col(dimension);
        string sc = (scope ?? "ALLSELECTED").Trim().ToUpperInvariant();
        string remove = sc switch
        {
            "ALL" => $"ALL ( {dim} )",
            "ALLSELECTED" => $"ALLSELECTED ( {dim} )",
            "ALLEXCEPT" => $"ALLEXCEPT ( {Tbl(DimTable(dimension))}, {dim} )",
            _ => throw new InvalidOperationException($"scope must be ALL, ALLSELECTED or ALLEXCEPT (got '{scope}')."),
        };
        string dax = $"DIVIDE (\n    {m},\n    CALCULATE ( {m}, {remove} )\n)";
        return new(table, $"{StripBrackets(baseMeasure)} % of Total", dax, "0.0%", $"{StripBrackets(baseMeasure)} Share");
    }

    public static MeasureSpec PercentOfParent(string table, string baseMeasure, IReadOnlyList<string> hierarchyColumns)
    {
        if (hierarchyColumns == null || hierarchyColumns.Count == 0)
            throw new InvalidOperationException("Provide at least one hierarchy column.");
        string m = Mref(baseMeasure);
        // Walk from the deepest level up: when this level is in scope, divide by the parent (one level up).
        var branches = new List<string>();
        for (int i = hierarchyColumns.Count - 1; i >= 0; i--)
        {
            string here = Col(hierarchyColumns[i]);
            if (i == 0)
            {
                branches.Add($"        DIVIDE ( {m}, CALCULATE ( {m}, ALLSELECTED ( {here} ) ) )");
            }
            else
            {
                string child = Col(hierarchyColumns[i]);
                branches.Add($"        ISINSCOPE ( {child} ), DIVIDE ( {m}, CALCULATE ( {m}, ALL ( {child} ) ) )");
            }
        }
        string dax = "SWITCH (\n    TRUE (),\n" + string.Join(",\n", branches) + "\n    )";
        return new(table, $"{StripBrackets(baseMeasure)} % of Parent", dax, "0.0%", $"{StripBrackets(baseMeasure)} Share");
    }

    // ================================================================ B9. rank measure
    public static MeasureSpec RankMeasure(string table, string baseMeasure, string dimension, string order,
        string ties, string? withinGroup)
    {
        string m = Mref(baseMeasure);
        string dim = Col(dimension);
        string ord = (order ?? "DESC").Trim().ToUpperInvariant() == "ASC" ? "ASC" : "DESC";
        string tieMode = (ties ?? "SKIP").Trim().ToUpperInvariant() == "DENSE" ? "DENSE" : "SKIP";
        string scopeTable = string.IsNullOrWhiteSpace(withinGroup)
            ? $"ALLSELECTED ( {dim} )"
            : $"FILTER ( ALLSELECTED ( {dim} ), {Col(withinGroup!)} = MAX ( {Col(withinGroup!)} ) )";
        string dax =
            $"IF (\n    ISINSCOPE ( {dim} ),\n" +
            "    VAR _rank =\n" +
            $"        RANKX (\n            {scopeTable},\n            {m},\n            ,\n            {ord},\n            {tieMode}\n        )\n" +
            "    RETURN\n" +
            $"        IF ( NOT ISBLANK ( {m} ), _rank )\n)";
        return new(table, $"{StripBrackets(baseMeasure)} Rank", dax, "0", $"{StripBrackets(baseMeasure)} Rank");
    }

    // ================================================================ B10. semi-additive
    public static IReadOnlyList<MeasureSpec> SemiAdditiveMeasures(string table, string valueColumn, string dateColumn)
    {
        string val = Col(valueColumn);
        string dcol = Col(dateColumn);
        string folder = $"{StripBrackets(valueColumn)} Balance";
        return new List<MeasureSpec>
        {
            new(table, $"{StripBrackets(valueColumn)} Closing Balance",
                $"CALCULATE ( SUM ( {val} ), LASTNONBLANK ( {dcol}, CALCULATE ( SUM ( {val} ) ) ) )", null, folder),
            new(table, $"{StripBrackets(valueColumn)} Opening Balance",
                $"CALCULATE ( SUM ( {val} ), FIRSTNONBLANK ( {dcol}, CALCULATE ( SUM ( {val} ) ) ) )", null, folder),
            new(table, $"{StripBrackets(valueColumn)} Last Non-Blank",
                $"CALCULATE ( SUM ( {val} ), LASTNONBLANKVALUE ( {dcol}, CALCULATE ( SUM ( {val} ) ) ) )", null, folder),
            new(table, $"{StripBrackets(valueColumn)} First Non-Blank",
                $"CALCULATE ( SUM ( {val} ), FIRSTNONBLANKVALUE ( {dcol}, CALCULATE ( SUM ( {val} ) ) ) )", null, folder),
        };
    }

    // ================================================================ B11. dynamic segmentation
    public static MeasureSpec DynamicSegmentation(string entityTable, string measure, string boundaryTable,
        string granularityColumn, string lowerColumn, string upperColumn, string segmentColumn)
    {
        string m = Mref(measure);
        string gran = Col(granularityColumn);
        string lower = $"'{boundaryTable}'[{lowerColumn}]";
        string upper = $"'{boundaryTable}'[{upperColumn}]";
        string seg = $"'{boundaryTable}'[{segmentColumn}]";
        string dax =
            "VAR _entity =\n" +
            $"    ADDCOLUMNS ( VALUES ( {gran} ), \"@v\", {m} )\n" +
            "VAR _classified =\n" +
            $"    ADDCOLUMNS (\n        _entity,\n        \"@seg\",\n        VAR _v = [@v]\n        RETURN\n            CALCULATE (\n                SELECTEDVALUE ( {seg} ),\n                FILTER ( {Tbl(boundaryTable)}, {lower} <= _v && _v < {upper} )\n            )\n    )\n" +
            "RETURN\n" +
            $"    COUNTROWS ( FILTER ( _classified, [@seg] = SELECTEDVALUE ( {seg} ) ) )";
        return new(entityTable, $"{StripBrackets(measure)} by Segment", dax, "0", "Segmentation");
    }

    // ================================================================ B12. ABC classification
    public static MeasureSpec AbcClassificationDynamic(string entityTable, string key, string valueMeasure,
        double aThreshold, double bThreshold)
    {
        string m = Mref(valueMeasure);
        string keyCol = Col(key);
        string dax =
            $"VAR _all =\n    ADDCOLUMNS ( ALLSELECTED ( {keyCol} ), \"@v\", {m} )\n" +
            "VAR _grand = SUMX ( _all, [@v] )\n" +
            "VAR _thisValue = " + m + "\n" +
            "VAR _cumAboveIncl =\n" +
            "    SUMX ( FILTER ( _all, [@v] >= _thisValue ), [@v] )\n" +
            "VAR _cumPct = DIVIDE ( _cumAboveIncl, _grand )\n" +
            "RETURN\n" +
            $"    SWITCH ( TRUE (),\n        _cumPct <= {Inv(aThreshold)}, \"A\",\n        _cumPct <= {Inv(bThreshold)}, \"B\",\n        \"C\"\n    )";
        return new(entityTable, $"{StripBrackets(valueMeasure)} ABC Class", dax, null, "ABC");
    }

    public static string AbcClassTableDax(string entityTable, string key, string valueMeasure,
        double aThreshold, double bThreshold)
    {
        string m = Mref(valueMeasure);
        string keyCol = Col(key);
        return
            $"VAR _all = ADDCOLUMNS ( VALUES ( {keyCol} ), \"@Value\", {m} )\n" +
            "VAR _grand = SUMX ( _all, [@Value] )\n" +
            "RETURN\n" +
            "ADDCOLUMNS (\n" +
            "    _all,\n" +
            "    \"@Class\",\n" +
            "        VAR _v = [@Value]\n" +
            "        VAR _cumPct = DIVIDE ( SUMX ( FILTER ( _all, [@Value] >= _v ), [@Value] ), _grand )\n" +
            $"        RETURN SWITCH ( TRUE (), _cumPct <= {Inv(aThreshold)}, \"A\", _cumPct <= {Inv(bThreshold)}, \"B\", \"C\" )\n" +
            ")";
    }

    // ================================================================ B13. dynamic Top N
    public static string DynamicTopNTableDax(string dimension, string othersLabel)
    {
        string dim = Col(dimension);
        string others = (othersLabel ?? "Others").Replace("\"", "\"\"");
        // SELECTCOLUMNS fixes the projected column name to [Member] (UNION names by position from the first
        // table, so a bare VALUES() would inherit the dimension's own name and mismatch the column binding).
        return $"UNION (\n    SELECTCOLUMNS ( VALUES ( {dim} ), \"Member\", {dim} ),\n    ROW ( \"Member\", \"{others}\" )\n)";
    }

    public static MeasureSpec DynamicTopNMeasure(string table, string dimension, string measure,
        string nValueMeasure, string othersLabel, string topNMemberColumn)
    {
        string m = Mref(measure);
        string dim = Col(dimension);
        string nVal = Mref(nValueMeasure);
        string memberCol = Col(topNMemberColumn);
        string others = (othersLabel ?? "Others").Replace("\"", "\"\"");
        string dax =
            $"VAR _n = {nVal}\n" +
            $"VAR _current = SELECTEDVALUE ( {memberCol} )\n" +
            "VAR _ranked =\n" +
            $"    ADDCOLUMNS ( VALUES ( {dim} ), \"@r\", RANKX ( ALLSELECTED ( {dim} ), {m},, DESC, DENSE ) )\n" +
            "RETURN\n" +
            "    SWITCH ( TRUE (),\n" +
            $"        _current = \"{others}\", SUMX ( FILTER ( _ranked, [@r] > _n ), {m} ),\n" +
            $"        CALCULATE ( {m}, FILTER ( _ranked, [@r] <= _n && {dim} = _current ) )\n" +
            "    )";
        return new(table, $"{StripBrackets(measure)} Top N or Others", dax, null, "Top N");
    }

    // ================================================================ B10/B14. time-intelligence calc group items
    public static IReadOnlyList<CalcItemSpec> TimeIntelligenceCalcItems(string dateTable, string dateColumn)
    {
        string dcol = $"'{dateTable}'[{dateColumn}]";
        string sm = "SELECTEDMEASURE ()";
        int o = 0;
        return new List<CalcItemSpec>
        {
            new("Current", sm, o++, null),
            new("MTD", $"CALCULATE ( {sm}, DATESMTD ( {dcol} ) )", o++, null),
            new("QTD", $"CALCULATE ( {sm}, DATESQTD ( {dcol} ) )", o++, null),
            new("YTD", $"CALCULATE ( {sm}, DATESYTD ( {dcol} ) )", o++, null),
            new("PY", $"CALCULATE ( {sm}, SAMEPERIODLASTYEAR ( {dcol} ) )", o++, null),
            new("PYTD", $"CALCULATE ( {sm}, SAMEPERIODLASTYEAR ( DATESYTD ( {dcol} ) ) )", o++, null),
            new("YoY", $"VAR _cur = {sm}\nVAR _py = CALCULATE ( {sm}, SAMEPERIODLASTYEAR ( {dcol} ) )\nRETURN _cur - _py", o++, null),
            new("YoY %",
                $"VAR _cur = {sm}\nVAR _py = CALCULATE ( {sm}, SAMEPERIODLASTYEAR ( {dcol} ) )\nRETURN DIVIDE ( _cur - _py, _py )",
                o++, "\"0.0%;-0.0%;0.0%\""),
        };
    }

    // ================================================================ B11. currency conversion calc item
    public static CalcItemSpec CurrencyConversionCalcItem(string rateTable, string rateColumn, string currencyColumn)
    {
        string rate = $"'{rateTable}'[{rateColumn}]";
        string dax =
            "SUMX (\n" +
            "    VALUES ( 'Date'[Date] ),\n" +
            $"    CALCULATE ( SELECTEDMEASURE () ) * CALCULATE ( SELECTEDVALUE ( {rate} ) )\n" +
            ")";
        return new("Converted", dax, 1, null);
    }

    // ================================================================ B12. dynamic RLS filters
    /// <summary>Build the RLS filter expression for a given shape. userTable holds the security mapping.</summary>
    public static string DynamicRlsFilter(string shape, string securedTable, string securedColumn,
        string userTable, string userEmailColumn, string userValueColumn, string? pathColumn)
    {
        string sc = $"'{securedTable}'[{securedColumn}]";
        string uval = $"'{userTable}'[{userValueColumn}]";
        string uemail = $"'{userTable}'[{userEmailColumn}]";
        return (shape ?? "direct").Trim().ToLowerInvariant() switch
        {
            "direct" => $"{sc} = LOOKUPVALUE ( {uval}, {uemail}, USERPRINCIPALNAME () )",
            "lookup" => $"{sc} = LOOKUPVALUE ( {uval}, {uemail}, USERPRINCIPALNAME () )",
            "bridge" =>
                $"CONTAINS (\n    FILTER ( {Tbl(userTable)}, {uemail} = USERPRINCIPALNAME () ),\n    {uval}, {sc}\n)",
            "hierarchy" =>
                $"PATHCONTAINS (\n    LOOKUPVALUE ( '{userTable}'[{pathColumn ?? userValueColumn}], {uemail}, USERPRINCIPALNAME () ),\n    {sc}\n)",
            _ => throw new InvalidOperationException($"shape must be direct, bridge, hierarchy or lookup (got '{shape}')."),
        };
    }

    // ================================================================ B13. custom-calendar TI
    public static IReadOnlyList<MeasureSpec> CustomCalendarTimeIntelligence(string table, string calendarTable,
        string kind, string baseMeasure)
    {
        string m = Mref(baseMeasure);
        string self = StripBrackets(baseMeasure);
        string folder = $"{self} Custom TI ({kind})";
        // integer index columns the generator expects on the calendar table.
        string dayIdx = $"'{calendarTable}'[DayIndex]";
        string monthIdx = $"'{calendarTable}'[MonthIndex]";
        string yearCol = $"'{calendarTable}'[Year]";
        string periodIdx = $"'{calendarTable}'[PeriodIndex]";

        var list = new List<MeasureSpec>();
        // *TD: sum from the start of the current year-index group to the current max day-index.
        list.Add(new(table, $"{self} YTD (Custom)",
            $"VAR _maxDay = MAX ( {dayIdx} )\nVAR _yr = MAX ( {yearCol} )\nRETURN\nCALCULATE ( {m}, FILTER ( ALL ( {Tbl(calendarTable)} ), {yearCol} = _yr && {dayIdx} <= _maxDay ) )",
            null, folder));
        // PY: shift by exactly 364 days (52 weeks) - the case where built-in TI breaks.
        list.Add(new(table, $"{self} PY (Custom)",
            $"VAR _minDay = MIN ( {dayIdx} )\nVAR _maxDay = MAX ( {dayIdx} )\nRETURN\nCALCULATE ( {m}, FILTER ( ALL ( {Tbl(calendarTable)} ), {dayIdx} >= _minDay - 364 && {dayIdx} <= _maxDay - 364 ) )",
            null, folder));
        // PYTD: 364-day shift of the YTD window.
        list.Add(new(table, $"{self} PYTD (Custom)",
            $"VAR _maxDay = MAX ( {dayIdx} ) - 364\nVAR _yrStart = CALCULATE ( MIN ( {dayIdx} ), ALLEXCEPT ( {Tbl(calendarTable)}, {yearCol} ) ) - 364\nRETURN\nCALCULATE ( {m}, FILTER ( ALL ( {Tbl(calendarTable)} ), {dayIdx} >= _yrStart && {dayIdx} <= _maxDay ) )",
            null, folder));
        // previous week.
        list.Add(new(table, $"{self} PW (Custom)",
            $"VAR _minDay = MIN ( {dayIdx} )\nVAR _maxDay = MAX ( {dayIdx} )\nRETURN\nCALCULATE ( {m}, FILTER ( ALL ( {Tbl(calendarTable)} ), {dayIdx} >= _minDay - 7 && {dayIdx} <= _maxDay - 7 ) )",
            null, folder));
        // moving annual total over 364 days.
        list.Add(new(table, $"{self} MAT (Custom)",
            $"VAR _maxDay = MAX ( {dayIdx} )\nRETURN\nCALCULATE ( {m}, FILTER ( ALL ( {Tbl(calendarTable)} ), {dayIdx} > _maxDay - 364 && {dayIdx} <= _maxDay ) )",
            null, folder));
        return list;
    }

    // ---------------------------------------------------------------- small helpers
    public static string StripBrackets(string s)
    {
        string t = (s ?? "").Trim();
        if (t.StartsWith("[") && t.EndsWith("]")) t = t[1..^1];
        int lb = t.IndexOf('[');
        int rb = t.LastIndexOf(']');
        if (lb >= 0 && rb > lb) t = t[(lb + 1)..rb];   // Table[Col] -> Col
        return t.Trim();
    }

    private static string DimTable(string columnRef)
    {
        string s = (columnRef ?? "").Trim();
        int lb = s.IndexOf('[');
        return s[..lb].Trim().Trim('\'');
    }
}
