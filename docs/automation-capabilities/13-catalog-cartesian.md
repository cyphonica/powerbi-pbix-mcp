# Visual property catalog - cartesian charts (13 types, 17 cards)

Source: learn.microsoft.com. Types: clusteredColumnChart, columnChart(stacked), hundredPercentStackedColumnChart, clusteredBarChart, barChart(stacked), hundredPercentStackedBarChart, lineChart, areaChart, stackedAreaChart, hundredPercentStackedAreaChart, lineClusteredColumnComboChart, lineStackedColumnComboChart, scatterChart, ribbonChart. All inherit commonCards (see 10). Exact keys: reportThemeSchema-2.155.json.

## Axis-role map (governs where value-axis props live)
Columns/line/area/combo/ribbon: X=Category, Y=Value (+Secondary Y for combo). Bar: X=Value, Y=Category. Scatter: both axes Value. => Range/Min/Max/Log/DisplayUnits/Invert live on the VALUE axis (Y for columns/line, X for bars, both for scatter).

## Cards + properties
- **Legend**: on; position (Top/Bottom/Left/Right/Top center/Bottom center/Left center/Right center); overflow (Pagination/Continuous); title+text; legend name (per-series); font family/size/color/bold/italic/underline.
- **X axis**: Type (Categorical/Continuous); Range (min/max/logarithmic/invert - when value axis); Title (on, text, color, size, Style Title-only/Unit-only/Both, font); Values (on, font/color/size, display units None/K/M/B/T/Auto + decimals when value axis); Concatenate labels; Gridlines (on/color/width).
- **Y axis** (primary value): Range (min/max/logarithmic/invert/round-range); Values (on, font/color/size, display units, decimals); Position (Left/Right); Title (on/text/color/size/Style); Gridlines (on/color/width).
- **Secondary Y axis** (combo): full mirror of primary; turn Values off to share primary scale; auto min/max to sync.
- **Data colors (dataPoint)**: color (single); default color; per-category (All + each); show all; per-series; color-by-category gradient; conditional fx (Gradient/Rules/Field value). NOTE line charts do NOT support CF of line color/markers.
- **Markers** (line/area/combo-line/scatter): on; show-for-all-series/categories; shape (circle/square/diamond/triangle/...); color; size; apply-to series; scatter: auto-fit markers + range scaling (Auto/Magnitude/DataRange when Size well used).
- **Lines** (line/area/combo): color; width; line style (Solid/Dashed/Dotted/Custom + dash array/scale/cap Flat/Round/Square); interpolation (Straight/Smooth Monotone|Cardinal/Step); apply-to series; shade area. **Columns/Bars spacing** (column/bar/ribbon): category spacing, series spacing, overlap series + transparency + border.
- **Data labels**: on; font family/size/color; position (visual-dependent Auto/Inside end/Inside center/Inside base/Outside end / Above/Below/Left/Right); orientation; display units; decimals; background+color+transparency; enhanced sub-cards Title/Value/Detail + visual-label-layout single/multi-line.
- **Total labels** (stacked col/bar/area/combo): on; font style/size; color; display units; decimals.
- **Series labels** (line only): on; position Right/Left; background match-series-color + transparency; values match-color; leader lines (max offset/color/transparency/style/width).
- **Ribbons** (ribbon): spacing %; match series color; transparency (default 30); border.
- **Scatter-specific**: Play Axis (animation); Size well (bubble); Values well (grouping); both axes value (min/max/log); General>Advanced>Number of data points (default 3500, max 10000); high-density sampling toggle.
- **Analytics pane** (availability matrix): Trend line (area/clustered-column/line/combo, needs time); X-axis constant line (most); Y-axis constant line (+waterfall); Min/Max/Average/Median/Percentile (area/clustered-bar/clustered-column/line/scatter); Symmetry shading (scatter); Error bars (clustered bar/column/line/combo); Forecast (line only); Anomalies (line only). Reference-line sub-props: +Add, Name, Measure (data-driven) or Value (constant), Percentile, Color, Transparency, Line style, Position front/behind, Data label (+ position/units/decimals/text Name|Value|Both/color/font). Forecast: length, units, ignore-last, confidence interval, seasonality, color/style. Error bars: By field (upper/lower, absolute/relative, symmetrical) or By percentage; style color/width/border; markers; error band (line) Fill/Line/Both; tooltip. Anomalies: sensitivity, explain-by, shape/size/rotation/color/border, expected-range style.
- **Small multiples** (bar/column/line/area): rows 1-6, columns 1-6; small-multiple title (font/color/size/align/position/background); shared y-axis toggle + scale-to-fit. Disables zoom/trend/forecast/concatenate/total-labels inside.
- **Zoom slider** (column/bar/line/scatter, X needs Continuous): on; X-axis; Y-axis; slider labels; slider tooltips.
- **Plot area**: image; transparency; image fit. (Non-image fill via General>Effects>Background.)

## Cross-type applicability: see the matrix in the source (Legend all; Markers line/area/combo/scatter; Lines line/area/combo; Total labels stacked+area+combo; Series labels line; Small multiples bar/col/line/area; Ribbons ribbon).
