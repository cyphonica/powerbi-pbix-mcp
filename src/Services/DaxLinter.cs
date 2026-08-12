namespace SuperBiMcp.Services;

/// <summary>
/// PURE OFFLINE DAX static linter (Wave G3) - no live engine, no session required. A lightweight
/// tokenizer feeds a set of pattern rules, each reporting a line number, severity and a rewrite
/// hint. Unlike run_bpa (which needs a live model), this lints a raw candidate expression BEFORE
/// it is ever applied - the pre-flight gate for agent-authored DAX. The known-function catalogue
/// is a maintained static list (Microsoft DAX reference), so UNKNOWN_FUNCTION catches
/// hallucinated function names; genuinely new engine functions can be admitted per call via
/// extraFunctions. SuggestRewrite goes further and produces concrete before/after texts for the
/// rules that have a mechanical fix.
/// </summary>
public static class DaxLinter
{
    // ---------------------------------------------------------------- token model

    internal enum TokKind { Ident, Number, Str, TableRef, BracketRef, Op, LParen, RParen, Comma, LBrace, RBrace }

    internal sealed record Tok(TokKind Kind, string Text, int Line, int Pos, int End);

    /// <summary>One lint finding: rule id, severity (error|warning|info), 1-based line, message, hint.</summary>
    public sealed record Finding(string Rule, string Severity, int Line, string Message, string Hint);

    /// <summary>One concrete rewrite: the exact source snippet and its suggested replacement.</summary>
    public sealed record Rewrite(string Rule, int Line, string Before, string After, int Start, int End);

    // ---------------------------------------------------------------- tokenizer

    /// <summary>Tokenize a DAX expression. Comments (// -- /* */) and whitespace are dropped;
    /// strings, 'Table' refs and [bracket] refs are kept as single tokens with escapes honoured.</summary>
    internal static List<Tok> Tokenize(string dax)
    {
        var toks = new List<Tok>();
        int i = 0, line = 1, n = dax.Length;
        while (i < n)
        {
            char c = dax[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // comments: // and -- to end of line, /* */ block (line counter kept accurate)
            if (c == '/' && i + 1 < n && dax[i + 1] == '/') { while (i < n && dax[i] != '\n') i++; continue; }
            if (c == '-' && i + 1 < n && dax[i + 1] == '-') { while (i < n && dax[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < n && dax[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(dax[i] == '*' && dax[i + 1] == '/')) { if (dax[i] == '\n') line++; i++; }
                i = Math.Min(n, i + 2);
                continue;
            }

            int start = i;
            if (c == '"')
            {
                i++;
                while (i < n) { if (dax[i] == '"') { if (i + 1 < n && dax[i + 1] == '"') { i += 2; continue; } i++; break; } if (dax[i] == '\n') line++; i++; }
                toks.Add(new Tok(TokKind.Str, dax[start..i], line, start, i));
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < n) { if (dax[i] == '\'') { if (i + 1 < n && dax[i + 1] == '\'') { i += 2; continue; } i++; break; } if (dax[i] == '\n') line++; i++; }
                toks.Add(new Tok(TokKind.TableRef, dax[start..i], line, start, i));
                continue;
            }
            if (c == '[')
            {
                i++;
                while (i < n) { if (dax[i] == ']') { if (i + 1 < n && dax[i + 1] == ']') { i += 2; continue; } i++; break; } if (dax[i] == '\n') line++; i++; }
                toks.Add(new Tok(TokKind.BracketRef, dax[start..i], line, start, i));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(dax[i + 1])))
            {
                i++;
                while (i < n && (char.IsDigit(dax[i]) || dax[i] == '.' || dax[i] == 'e' || dax[i] == 'E'
                       || ((dax[i] == '+' || dax[i] == '-') && (dax[i - 1] == 'e' || dax[i - 1] == 'E')))) i++;
                toks.Add(new Tok(TokKind.Number, dax[start..i], line, start, i));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                i++;
                // dots stay inside the identifier so INFO.VIEW.MEASURES reads as one function name
                while (i < n && (char.IsLetterOrDigit(dax[i]) || dax[i] == '_' || dax[i] == '.')) i++;
                toks.Add(new Tok(TokKind.Ident, dax[start..i], line, start, i));
                continue;
            }
            switch (c)
            {
                case '(': toks.Add(new Tok(TokKind.LParen, "(", line, start, i + 1)); break;
                case ')': toks.Add(new Tok(TokKind.RParen, ")", line, start, i + 1)); break;
                case ',': toks.Add(new Tok(TokKind.Comma, ",", line, start, i + 1)); break;
                case '{': toks.Add(new Tok(TokKind.LBrace, "{", line, start, i + 1)); break;
                case '}': toks.Add(new Tok(TokKind.RBrace, "}", line, start, i + 1)); break;
                default:
                    // two-char operators kept whole so '<' '>' never splits '<>' / '<=' / '>=' / '&&' / '||' / '=='
                    if (i + 1 < n)
                    {
                        string two = dax.Substring(i, 2);
                        if (two is "<>" or "<=" or ">=" or "&&" or "||" or "==")
                        { toks.Add(new Tok(TokKind.Op, two, line, start, i + 2)); i += 2; continue; }
                    }
                    toks.Add(new Tok(TokKind.Op, c.ToString(), line, start, i + 1));
                    break;
            }
            i++;
        }
        return toks;
    }

    // ---------------------------------------------------------------- known-function catalogue

    /// <summary>The maintained static catalogue of DAX function names (Microsoft DAX function
    /// reference). Case-insensitive. INFO.* names are admitted by prefix because the INFO view
    /// family grows with every engine release.</summary>
    internal static readonly HashSet<string> KnownFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // aggregation
        "SUM", "SUMX", "AVERAGE", "AVERAGEA", "AVERAGEX", "MIN", "MINA", "MINX", "MAX", "MAXA", "MAXX",
        "COUNT", "COUNTA", "COUNTAX", "COUNTX", "COUNTBLANK", "COUNTROWS", "DISTINCTCOUNT",
        "DISTINCTCOUNTNOBLANK", "PRODUCT", "PRODUCTX", "APPROXIMATEDISTINCTCOUNT",
        // filter
        "CALCULATE", "CALCULATETABLE", "FILTER", "ALL", "ALLBLANK", "ALLEXCEPT", "ALLNOBLANKROW",
        "ALLSELECTED", "ALLCROSSFILTERED", "KEEPFILTERS", "REMOVEFILTERS", "SELECTEDVALUE",
        "LOOKUPVALUE", "EARLIER", "EARLIEST", "INDEX", "OFFSET", "WINDOW", "RANK", "ROWNUMBER",
        "ORDERBY", "PARTITIONBY", "MATCHBY",
        // filter-context info
        "FILTERS", "HASONEFILTER", "HASONEVALUE", "ISCROSSFILTERED", "ISFILTERED", "ISINSCOPE",
        "SELECTEDMEASURE", "SELECTEDMEASURENAME", "SELECTEDMEASUREFORMATSTRING", "ISSELECTEDMEASURE",
        "CROSSFILTER", "USERELATIONSHIP", "TREATAS", "VALUES", "DISTINCT", "SELECTCOLUMNS",
        // logical
        "AND", "OR", "NOT", "IF", "IF.EAGER", "IFERROR", "SWITCH", "TRUE", "FALSE", "COALESCE", "BITAND",
        "BITOR", "BITXOR", "BITLSHIFT", "BITRSHIFT",
        // information
        "ISBLANK", "ISEMPTY", "ISERROR", "ISEVEN", "ISODD", "ISLOGICAL", "ISNONTEXT", "ISNUMBER",
        "ISTEXT", "ISSUBTOTAL", "ISONORAFTER", "CONTAINS", "CONTAINSROW", "CONTAINSSTRING",
        "CONTAINSSTRINGEXACT", "CUSTOMDATA", "USERNAME", "USERPRINCIPALNAME", "USEROBJECTID",
        "USERCULTURE", "HASONEVALUE", "NONVISUAL", "ISATLEVEL", "ISDATETIME", "ISSTRING", "ISINT64",
        "ISDOUBLE", "ISDECIMAL", "ISBOOLEAN",
        // math / trig
        "ABS", "ACOS", "ACOSH", "ACOT", "ACOTH", "ASIN", "ASINH", "ATAN", "ATANH", "CEILING", "COMBIN",
        "COMBINA", "CONVERT", "COS", "COSH", "COT", "COTH", "CURRENCY", "DEGREES", "DIVIDE", "EVEN",
        "EXP", "FACT", "FLOOR", "GCD", "INT", "ISO.CEILING", "LCM", "LN", "LOG", "LOG10", "MOD", "MROUND",
        "ODD", "PI", "POWER", "QUOTIENT", "RADIANS", "RAND", "RANDBETWEEN", "ROUND", "ROUNDDOWN",
        "ROUNDUP", "SIGN", "SIN", "SINH", "SQRT", "SQRTPI", "TAN", "TANH", "TRUNC",
        // statistical
        "BETA.DIST", "BETA.INV", "CHISQ.DIST", "CHISQ.DIST.RT", "CHISQ.INV", "CHISQ.INV.RT",
        "CONFIDENCE.NORM", "CONFIDENCE.T", "EXPON.DIST", "GEOMEAN", "GEOMEANX", "LINEST", "LINESTX",
        "MEDIAN", "MEDIANX", "NORM.DIST", "NORM.INV", "NORM.S.DIST", "NORM.S.INV", "PERCENTILE.EXC",
        "PERCENTILE.INC", "PERCENTILEX.EXC", "PERCENTILEX.INC", "POISSON.DIST", "RANK.EQ", "RANKX",
        "SAMPLE", "STDEV.P", "STDEV.S", "STDEVX.P", "STDEVX.S", "T.DIST", "T.DIST.2T", "T.DIST.RT",
        "T.INV", "T.INV.2T", "VAR.P", "VAR.S", "VARX.P", "VARX.S", "SAMPLEAXISWITHLOCALMINMAX",
        // text
        "COMBINEVALUES", "CONCATENATE", "CONCATENATEX", "EXACT", "FIND", "FIXED", "FORMAT", "LEFT",
        "LEN", "LOWER", "MID", "REPLACE", "REPT", "RIGHT", "SEARCH", "SUBSTITUTE", "TRIM", "UNICHAR",
        "UNICODE", "UPPER", "VALUE",
        // date / time
        "CALENDAR", "CALENDARAUTO", "DATE", "DATEDIFF", "DATEVALUE", "DAY", "EDATE", "EOMONTH", "HOUR",
        "MINUTE", "MONTH", "NETWORKDAYS", "NOW", "QUARTER", "SECOND", "TIME", "TIMEVALUE", "TODAY",
        "UTCNOW", "UTCTODAY", "WEEKDAY", "WEEKNUM", "YEAR", "YEARFRAC",
        // time intelligence
        "CLOSINGBALANCEMONTH", "CLOSINGBALANCEQUARTER", "CLOSINGBALANCEYEAR", "DATEADD", "DATESBETWEEN",
        "DATESINPERIOD", "DATESMTD", "DATESQTD", "DATESYTD", "ENDOFMONTH", "ENDOFQUARTER", "ENDOFYEAR",
        "FIRSTDATE", "FIRSTNONBLANK", "FIRSTNONBLANKVALUE", "LASTDATE", "LASTNONBLANK",
        "LASTNONBLANKVALUE", "NEXTDAY", "NEXTMONTH", "NEXTQUARTER", "NEXTYEAR", "OPENINGBALANCEMONTH",
        "OPENINGBALANCEQUARTER", "OPENINGBALANCEYEAR", "PARALLELPERIOD", "PREVIOUSDAY", "PREVIOUSMONTH",
        "PREVIOUSQUARTER", "PREVIOUSYEAR", "SAMEPERIODLASTYEAR", "STARTOFMONTH", "STARTOFQUARTER",
        "STARTOFYEAR", "TOTALMTD", "TOTALQTD", "TOTALYTD",
        // relationships / table
        "RELATED", "RELATEDTABLE", "ADDCOLUMNS", "ADDMISSINGITEMS", "CROSSJOIN", "CURRENTGROUP",
        "DATATABLE", "DETAILROWS", "EXCEPT", "GENERATE", "GENERATEALL", "GENERATESERIES", "GROUPBY",
        "IGNORE", "INTERSECT", "NATURALINNERJOIN", "NATURALLEFTOUTERJOIN", "ROLLUP", "ROLLUPADDISSUBTOTAL",
        "ROLLUPGROUP", "ROLLUPISSUBTOTAL", "ROW", "SUBSTITUTEWITHINDEX", "SUMMARIZE", "SUMMARIZECOLUMNS",
        "TABLE", "TOPN", "TOPNSKIP", "UNION", "NAMEOF", "TOCSV", "TOJSON", "EVALUATEANDLOG",
        // parent-child
        "PATH", "PATHCONTAINS", "PATHITEM", "PATHITEMREVERSE", "PATHLENGTH",
        // financial
        "ACCRINT", "ACCRINTM", "AMORDEGRC", "AMORLINC", "COUPDAYBS", "COUPDAYS", "COUPDAYSNC", "COUPNCD",
        "COUPNUM", "COUPPCD", "CUMIPMT", "CUMPRINC", "DB", "DDB", "DISC", "DOLLARDE", "DOLLARFR",
        "DURATION", "EFFECT", "FV", "INTRATE", "IPMT", "ISPMT", "MDURATION", "NOMINAL", "NPER", "ODDFPRICE",
        "ODDFYIELD", "ODDLPRICE", "ODDLYIELD", "PDURATION", "PMT", "PPMT", "PRICE", "PRICEDISC",
        "PRICEMAT", "PV", "RATE", "RECEIVED", "RRI", "SLN", "SYD", "TBILLEQ", "TBILLPRICE", "TBILLYIELD",
        "VDB", "XIRR", "XNPV", "YIELD", "YIELDDISC", "YIELDMAT", "IRR", "NPV", "MIRR", "FVSCHEDULE", "DOLLAR",
        // other
        "BLANK", "ERROR", "EXACT", "GENERATESERIES", "ISAFTER", "UNICODE", "CURRENTVALUE",
        "COLUMNSTATISTICS", "EXTERNALMEASURE", "AI.GENERATE", "AI.CLASSIFY", "AI.SUMMARIZE",
    };

    /// <summary>Names that read like function calls but are DAX keywords / query constructs.</summary>
    private static readonly HashSet<string> KeywordNames = new(StringComparer.OrdinalIgnoreCase)
        { "VAR", "RETURN", "EVALUATE", "DEFINE", "MEASURE", "ORDER", "BY", "ASC", "DESC", "IN", "NOT", "FUNCTION" };

    // ---------------------------------------------------------------- lint

    /// <summary>A resolved function call: catalogue name token index + the token index of its '('.</summary>
    private sealed record Call(int NameIdx, int OpenIdx, string Name);

    /// <summary>Find every Ident token immediately followed by '(' - a DAX function call.</summary>
    private static List<Call> FindCalls(List<Tok> toks)
    {
        var calls = new List<Call>();
        for (int i = 0; i + 1 < toks.Count; i++)
            if (toks[i].Kind == TokKind.Ident && toks[i + 1].Kind == TokKind.LParen
                && !KeywordNames.Contains(toks[i].Text))
                calls.Add(new Call(i, i + 1, toks[i].Text));
        return calls;
    }

    /// <summary>Token index of the ')' matching the '(' at openIdx (or the last token when unbalanced).</summary>
    private static int MatchClose(List<Tok> toks, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < toks.Count; i++)
        {
            if (toks[i].Kind is TokKind.LParen or TokKind.LBrace) depth++;
            else if (toks[i].Kind is TokKind.RParen or TokKind.RBrace)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return toks.Count - 1;
    }

    /// <summary>The enclosing call (innermost) for a token index, from the call list.</summary>
    private static Call? EnclosingCall(List<Tok> toks, List<Call> calls, int idx)
    {
        Call? best = null;
        int bestOpen = -1;
        foreach (var c in calls)
        {
            int close = MatchClose(toks, c.OpenIdx);
            if (c.OpenIdx < idx && idx <= close && c.OpenIdx > bestOpen) { best = c; bestOpen = c.OpenIdx; }
        }
        return best;
    }

    /// <summary>Lint one DAX expression. extraKnown admits model UDFs / newly shipped functions.</summary>
    public static List<Finding> Lint(string dax, IEnumerable<string>? extraKnown = null)
    {
        if (string.IsNullOrWhiteSpace(dax)) throw new ArgumentException("A DAX expression is required.");
        var toks = Tokenize(dax);
        var calls = FindCalls(toks);
        var extra = new HashSet<string>(extraKnown ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var findings = new List<Finding>();

        foreach (var call in calls)
        {
            string name = call.Name;
            int line = toks[call.NameIdx].Line;

            // UNKNOWN_FUNCTION - the hallucination catcher. INFO.* admitted by prefix (the view
            // family grows per release); UDFs and per-call admissions via extra.
            if (!KnownFunctions.Contains(name) && !extra.Contains(name)
                && !name.StartsWith("INFO.", StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding("UNKNOWN_FUNCTION", "error", line,
                    $"'{name}' is not a recognised DAX function.",
                    "Check the spelling against the DAX function reference - this is the classic AI-hallucinated-function failure. If it is a model UDF or a brand-new engine function, pass it in extraFunctions."));

            // NESTED_CALCULATE - a CALCULATE inside another CALCULATE's arguments.
            if (name.Equals("CALCULATE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CALCULATETABLE", StringComparison.OrdinalIgnoreCase))
            {
                var parent = EnclosingCall(toks, calls, call.NameIdx);
                while (parent != null)
                {
                    if (parent.Name.Equals("CALCULATE", StringComparison.OrdinalIgnoreCase)
                        || parent.Name.Equals("CALCULATETABLE", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new Finding("NESTED_CALCULATE", "warning", line,
                            $"{name} nested inside {parent.Name} - context transitions stack and the result is hard to reason about.",
                            "Hoist the inner logic into a VAR evaluated before the outer CALCULATE, or collapse both filter sets into ONE CALCULATE."));
                        break;
                    }
                    parent = EnclosingCall(toks, calls, parent.NameIdx);
                }
            }

            // FILTER_WHOLE_TABLE_IN_CALCULATE - FILTER(Table, ...) as a direct CALCULATE filter arg.
            if (name.Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                var parent = EnclosingCall(toks, calls, call.NameIdx);
                bool inCalc = parent != null
                    && (parent.Name.Equals("CALCULATE", StringComparison.OrdinalIgnoreCase)
                        || parent.Name.Equals("CALCULATETABLE", StringComparison.OrdinalIgnoreCase));
                if (inCalc && call.OpenIdx + 2 < toks.Count)
                {
                    var first = toks[call.OpenIdx + 1];
                    var second = toks[call.OpenIdx + 2];
                    bool bareTable = (first.Kind == TokKind.Ident || first.Kind == TokKind.TableRef)
                                     && second.Kind == TokKind.Comma;
                    if (bareTable)
                        findings.Add(new Finding("FILTER_WHOLE_TABLE_IN_CALCULATE", "warning", line,
                            $"FILTER over the whole table {first.Text} inside {parent!.Name} - iterates every row and kills filter-context optimisation.",
                            "Filter the COLUMN, not the table: pass a boolean predicate (Table[Col] = value) or FILTER(ALL(Table[Col]), ...) as the filter argument."));
                }
            }

            // IFERROR - row-by-row error handling is expensive and hides real data problems.
            if (name.Equals("IFERROR", StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding("IFERROR_WRAPPING", "warning", line,
                    "IFERROR forces row-by-row error handling in the engine and hides genuine data errors.",
                    "For division use DIVIDE(numerator, denominator [, alternate]); otherwise guard the specific error case explicitly (ISBLANK / ISERROR on the input)."));

            // EARLIER / EARLIEST - superseded by variables.
            if (name.Equals("EARLIER", StringComparison.OrdinalIgnoreCase)
                || name.Equals("EARLIEST", StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding("EARLIER_USAGE", "warning", line,
                    $"{name.ToUpperInvariant()} is hard to read and superseded by variables.",
                    "Capture the outer row context in a VAR before the iterator: VAR v = Table[Col] ... FILTER(Table, Table[Col] = v)."));

            // SUMMARIZE used for aggregation - a Str token at the call's own argument depth is a
            // "Name", expression pair, the deprecated aggregation-inside-SUMMARIZE pattern.
            if (name.Equals("SUMMARIZE", StringComparison.OrdinalIgnoreCase))
            {
                int close = MatchClose(toks, call.OpenIdx);
                int depth = 0;
                for (int i = call.OpenIdx; i < close; i++)
                {
                    if (toks[i].Kind is TokKind.LParen or TokKind.LBrace) depth++;
                    else if (toks[i].Kind is TokKind.RParen or TokKind.RBrace) depth--;
                    else if (toks[i].Kind == TokKind.Str && depth == 1)
                    {
                        findings.Add(new Finding("SUMMARIZE_AGGREGATION", "warning", toks[i].Line,
                            "SUMMARIZE with inline named aggregations - the deprecated pattern with known wrong-total edge cases.",
                            "Use SUMMARIZECOLUMNS, or ADDCOLUMNS ( SUMMARIZE ( grouping columns only ), \"Name\", CALCULATE ( ... ) )."));
                        break;
                    }
                }
            }
        }

        // DIVISION_OPERATOR - '/' where DIVIDE belongs (divide-by-zero returns an error, not a blank).
        foreach (var t in toks)
            if (t.Kind == TokKind.Op && t.Text == "/")
                findings.Add(new Finding("DIVISION_OPERATOR", "info", t.Line,
                    "'/' raises an error on divide-by-zero.",
                    "Use DIVIDE(numerator, denominator [, alternate]) - it returns BLANK (or the alternate) instead of erroring."));

        // PLUS_ZERO - '+ 0' (or '0 +') blank suppression: turns BLANK into 0, defeating the
        // engine's blank-row elimination so visuals densify with all-zero rows.
        for (int i = 0; i + 1 < toks.Count; i++)
        {
            bool plusZero = toks[i].Kind == TokKind.Op && toks[i].Text == "+"
                && toks[i + 1].Kind == TokKind.Number && IsZero(toks[i + 1].Text)
                && (i + 2 >= toks.Count || toks[i + 2].Kind is TokKind.RParen or TokKind.Comma);
            bool zeroPlus = toks[i].Kind == TokKind.Number && IsZero(toks[i].Text)
                && toks[i + 1].Kind == TokKind.Op && toks[i + 1].Text == "+"
                && (i == 0 || toks[i - 1].Kind is TokKind.LParen or TokKind.Comma);
            if (plusZero || zeroPlus)
                findings.Add(new Finding("PLUS_ZERO", "warning", toks[i].Line,
                    "'+ 0' blank suppression - BLANK becomes 0, so every combination materialises and visuals fill with zero rows.",
                    "Drop the + 0; if a visible zero is genuinely wanted, use COALESCE(expr, 0) deliberately or a format string."));
        }

        return findings
            .OrderBy(f => f.Line)
            .ThenBy(f => f.Rule, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsZero(string numberText) =>
        double.TryParse(numberText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d == 0;

    // ---------------------------------------------------------------- concrete rewrites

    /// <summary>
    /// Produce concrete before/after rewrites for the mechanically fixable findings: '/' to
    /// DIVIDE, IFERROR(x / y, alt) to DIVIDE(x, y, alt), a trailing '+ 0' removed, and
    /// FILTER(Table, single-column predicate) inside CALCULATE to the bare predicate. Also
    /// returns hint-only notes for findings with no safe mechanical rewrite, plus the full
    /// suggested expression with all non-overlapping rewrites applied.
    /// </summary>
    public static (List<Rewrite> rewrites, List<Finding> notes, string suggested) SuggestRewrite(
        string dax, IEnumerable<string>? extraKnown = null)
    {
        var findings = Lint(dax, extraKnown);
        var toks = Tokenize(dax);
        var calls = FindCalls(toks);
        var rewrites = new List<Rewrite>();

        // IFERROR(x / y, alt) -> DIVIDE(x, y, alt). Handled BEFORE the bare '/' pass so the
        // division inside an already-rewritten IFERROR is not rewritten twice.
        var claimed = new List<(int start, int end)>();
        foreach (var call in calls.Where(c => c.Name.Equals("IFERROR", StringComparison.OrdinalIgnoreCase)))
        {
            int close = MatchClose(toks, call.OpenIdx);
            var argSplits = TopLevelCommas(toks, call.OpenIdx, close);
            if (argSplits.Count != 1) continue;                     // IFERROR takes exactly two args
            int comma = argSplits[0];
            int slash = FindTopLevelSlash(toks, call.OpenIdx + 1, comma - 1);
            if (slash < 0) continue;                                // first arg is not a division
            string num = Slice(dax, toks, call.OpenIdx + 1, slash - 1);
            string den = Slice(dax, toks, slash + 1, comma - 1);
            string alt = Slice(dax, toks, comma + 1, close - 1);
            bool altBlank = alt.Replace(" ", "").Equals("BLANK()", StringComparison.OrdinalIgnoreCase);
            string after = altBlank ? $"DIVIDE({num}, {den})" : $"DIVIDE({num}, {den}, {alt})";
            int s = toks[call.NameIdx].Pos, e = toks[close].End;
            rewrites.Add(new Rewrite("IFERROR_WRAPPING", toks[call.NameIdx].Line, dax[s..e], after, s, e));
            claimed.Add((s, e));
        }

        // bare '/' -> DIVIDE(numerator, denominator), operand spans found by precedence-aware scan
        for (int i = 0; i < toks.Count; i++)
        {
            if (toks[i].Kind != TokKind.Op || toks[i].Text != "/") continue;
            if (claimed.Any(c => toks[i].Pos >= c.start && toks[i].Pos < c.end)) continue;
            int lo = OperandStart(toks, i - 1);
            int hi = OperandEnd(toks, i + 1);
            if (lo < 0 || hi < 0) continue;
            string num = Slice(dax, toks, lo, i - 1);
            string den = Slice(dax, toks, i + 1, hi);
            int s = toks[lo].Pos, e = toks[hi].End;
            rewrites.Add(new Rewrite("DIVISION_OPERATOR", toks[i].Line, dax[s..e], $"DIVIDE({num}, {den})", s, e));
        }

        // trailing '+ 0' removed - the span starts at the PREVIOUS token's end so the removal
        // takes the joining whitespace with it and never leaves a doubled space behind
        for (int i = 0; i + 1 < toks.Count; i++)
        {
            if (!(toks[i].Kind == TokKind.Op && toks[i].Text == "+"
                  && toks[i + 1].Kind == TokKind.Number && IsZero(toks[i + 1].Text)
                  && (i + 2 >= toks.Count || toks[i + 2].Kind is TokKind.RParen or TokKind.Comma))) continue;
            int s = i > 0 ? toks[i - 1].End : toks[i].Pos;
            int e = toks[i + 1].End;
            rewrites.Add(new Rewrite("PLUS_ZERO", toks[i].Line, dax[s..e].Trim(), "", s, e));
        }

        // FILTER(Table, Table[Col] <op> literal) inside CALCULATE -> the bare predicate (the safe
        // single-column boolean-filter case only; anything richer keeps a hint note instead)
        foreach (var call in calls.Where(c => c.Name.Equals("FILTER", StringComparison.OrdinalIgnoreCase)))
        {
            var parent = EnclosingCall(toks, calls, call.NameIdx);
            if (parent == null || !(parent.Name.Equals("CALCULATE", StringComparison.OrdinalIgnoreCase)
                || parent.Name.Equals("CALCULATETABLE", StringComparison.OrdinalIgnoreCase))) continue;
            int close = MatchClose(toks, call.OpenIdx);
            var commas = TopLevelCommas(toks, call.OpenIdx, close);
            if (commas.Count != 1) continue;
            var first = toks[call.OpenIdx + 1];
            if (!((first.Kind == TokKind.Ident || first.Kind == TokKind.TableRef)
                  && toks[call.OpenIdx + 2].Kind == TokKind.Comma)) continue;
            // predicate = table-column comparison against a literal: Table [Col] op literal
            int p = commas[0] + 1;
            bool simple = close - p == 4
                && (toks[p].Kind == TokKind.Ident || toks[p].Kind == TokKind.TableRef)
                && toks[p + 1].Kind == TokKind.BracketRef
                && toks[p + 2].Kind == TokKind.Op
                && toks[p + 3].Kind is TokKind.Number or TokKind.Str;
            if (!simple) continue;
            string predicate = Slice(dax, toks, p, close - 1);
            int s = toks[call.NameIdx].Pos, e = toks[close].End;
            rewrites.Add(new Rewrite("FILTER_WHOLE_TABLE_IN_CALCULATE", toks[call.NameIdx].Line,
                dax[s..e], predicate, s, e));
        }

        // findings with no mechanical rewrite surface as hint-only notes
        var covered = new HashSet<string>(rewrites.Select(r => r.Rule), StringComparer.Ordinal);
        var notes = findings.Where(f => !covered.Contains(f.Rule)).ToList();

        // apply non-overlapping rewrites right-to-left for the full suggested text
        string suggested = dax;
        var applied = new List<(int start, int end)>();
        foreach (var r in rewrites.OrderByDescending(r => r.Start))
        {
            if (applied.Any(a => r.Start < a.end && r.End > a.start)) continue;
            suggested = suggested[..r.Start] + r.After + suggested[r.End..];
            applied.Add((r.Start, r.End));
        }
        // tidy doubled spaces left by a removal-style rewrite (PLUS_ZERO)
        suggested = string.Join("\n", suggested.Split('\n').Select(l => l.TrimEnd()));

        return (rewrites.OrderBy(r => r.Start).ToList(), notes, suggested);
    }

    // token-index [from..to] inclusive -> the exact source slice, trimmed
    private static string Slice(string dax, List<Tok> toks, int from, int to)
    {
        if (from > to || from < 0 || to >= toks.Count) return "";
        return dax[toks[from].Pos..toks[to].End].Trim();
    }

    // indices of commas at depth 1 between open and close (the call's own argument separators)
    private static List<int> TopLevelCommas(List<Tok> toks, int openIdx, int closeIdx)
    {
        var result = new List<int>();
        int depth = 0;
        for (int i = openIdx; i <= closeIdx; i++)
        {
            if (toks[i].Kind is TokKind.LParen or TokKind.LBrace) depth++;
            else if (toks[i].Kind is TokKind.RParen or TokKind.RBrace) depth--;
            else if (toks[i].Kind == TokKind.Comma && depth == 1) result.Add(i);
        }
        return result;
    }

    private static int FindTopLevelSlash(List<Tok> toks, int from, int to)
    {
        int depth = 0;
        for (int i = from; i <= to; i++)
        {
            if (toks[i].Kind is TokKind.LParen or TokKind.LBrace) depth++;
            else if (toks[i].Kind is TokKind.RParen or TokKind.RBrace) depth--;
            else if (depth == 0 && toks[i].Kind == TokKind.Op && toks[i].Text == "/") return i;
        }
        return -1;
    }

    // operators that BIND LOOSER than '/' - an operand span stops at one of these at depth 0
    private static readonly HashSet<string> LooseOps = new(StringComparer.Ordinal)
        { "+", "-", "=", "==", "<>", "<", "<=", ">", ">=", "&", "&&", "||", "IN" };

    /// <summary>Walk LEFT from idx to the start of the '/' numerator operand (inclusive index).</summary>
    private static int OperandStart(List<Tok> toks, int idx)
    {
        int depth = 0, start = -1;
        for (int i = idx; i >= 0; i--)
        {
            var t = toks[i];
            if (t.Kind is TokKind.RParen or TokKind.RBrace) depth++;
            else if (t.Kind is TokKind.LParen or TokKind.LBrace)
            {
                if (depth == 0) break;                     // the enclosing call's own '(' - operand boundary
                depth--;
                // consumed a full ( ... ) group: include a function name directly before it
                if (depth == 0 && i - 1 >= 0 && toks[i - 1].Kind == TokKind.Ident) { start = i - 1; i--; continue; }
            }
            else if (depth == 0 && (t.Kind == TokKind.Comma || (t.Kind == TokKind.Op && (LooseOps.Contains(t.Text) || t.Text == "*" || t.Text == "/" || t.Text == "^"))))
                break;
            start = i;
        }
        return start;
    }

    /// <summary>Walk RIGHT from idx to the end of the '/' denominator operand (inclusive index).
    /// Multiplicative neighbours are included going right (left-associativity: a / b * c parses
    /// as (a / b) * c, so the denominator stops before '*') - hence only tighter-binding tokens.</summary>
    private static int OperandEnd(List<Tok> toks, int idx)
    {
        int depth = 0, end = -1;
        for (int i = idx; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind is TokKind.LParen or TokKind.LBrace) { depth++; end = i; continue; }
            if (t.Kind is TokKind.RParen or TokKind.RBrace)
            {
                if (depth == 0) break;
                depth--; end = i; continue;
            }
            if (depth == 0 && (t.Kind == TokKind.Comma
                || (t.Kind == TokKind.Op && (LooseOps.Contains(t.Text) || t.Text == "*" || t.Text == "/" || t.Text == "^"))))
                break;
            end = i;
        }
        return end;
    }

    // ---------------------------------------------------------------- result shaping

    /// <summary>Shape one expression's findings for the tool result.</summary>
    public static object LintResult(string label, string dax, IEnumerable<string>? extraKnown = null)
    {
        var findings = Lint(dax, extraKnown);
        return new
        {
            target = label,
            findingCount = findings.Count,
            errors = findings.Count(f => f.Severity == "error"),
            warnings = findings.Count(f => f.Severity == "warning"),
            infos = findings.Count(f => f.Severity == "info"),
            findings = findings.Select(f => new { rule = f.Rule, severity = f.Severity, line = f.Line, message = f.Message, hint = f.Hint }).ToList(),
        };
    }
}
