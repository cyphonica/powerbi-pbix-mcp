# Visual property catalog - table / matrix / slicer / button-slicer

Source: learn.microsoft.com per-visual format docs. Format: visualType -> card -> properties (type + range/enum). UI labels map 1:1 to the JSON keys in the versioned theme schema (reportThemeSchema-<ver>.json, microsoft/powerbi-desktop-samples) - pull that for exact keys at build time.

## TABLE (tableEx)
- **Style preset**: Style (Default/None/Minimal/Bold header/Alternating rows/Contrast alternating rows/Flashy rows/Bold header flashy rows/Sparse/Condensed)
- **Grid**: horizontal gridlines on + color + width(1-10px); vertical gridlines on + color + width; border (selection/position/color/width); row padding; global font size
- **Column headers**: font family/size(8-60pt)/style(normal/bold/italic/underline); text color; background; header alignment(L/C/R); title alignment; text wrap; auto-size column width
- **Values**: font family/size/style; text color; background; alternate text color; alternate background; text wrap (honors newlines); URL icon; image height(8-512px)
- **Total**: totals on; font/size/style; text color; background; apply-to-labels
- **Specific column**: apply-to series + apply-to header/total/values; text color; background; alignment(Auto/L/C/R)
- **Layout (column width)**: auto-size behavior(Fit to content/Grow to fit/Fixed width); default width(px, fx-capable); custom per-column widths
- **Conditional formatting** (values/totals/subtotals only): Background color + Font color (Gradient: field/summarization/min/center/max colors; Rules: conditions + Percent/Number; Field value: CSS color from field); Data bars (show-bar-only, min/max, positive/negative/axis color, direction); Icons (layout L/R/icon-only; sets Directional/Shapes/Indicators/Ratings of 3/4/5; rules); Web URL

## MATRIX (pivotTable) - all of TABLE plus:
- **Layout & style**: Style(presets); Layout(Compact/Outline/Tabular); repeat row headers
- **Grid**: + border selection(All/Column header/Row header/Values) + position(Top/Bottom/Left/Right)
- **Blank rows**: on + color + transparency + border position/color/width
- **Values**: + switch values to rows (show on rows)
- **Column headers**: + auto expand; column width controls
- **Row headers**: font/color/background; banded row color; alignment; text wrap; auto expand; +/- icons color + size(8-60px) [Compact=stepped/indented]
- **Column subtotals**: on; per-column-level; show subtotal; label; font/color/background; apply-to-labels
- **Row subtotals**: on; per-row-level; show subtotal; label; label position(Top/Bottom); font/color/background; apply-to-labels
- **Column grand total / Row grand total**: font/color/background; apply-to-labels (inherit subtotal if unset)
- **Specific column**: + display units(None/K/M/B/T) + decimals(0-15)
- **Cell elements**: per-series toggles -> background/font color/data bars/icons/web URL (CF)
- **URL icon**: values / column headers / row headers
- **Image size**: height + width (8-512px)

## SLICER (slicer)
- **Slicer settings/Options**: Style(Dropdown/Vertical list/Tile/Between/Before/After/Relative date/Relative time/Date picker); Search; Show summary; Responsive
- **Selection**: single select; multi-select-with-CTRL; show "Select all"
- **Slicer header**: title text on + editable; font family/size/color; background; border; alignment
- **Values (items)**: font family/size/color; background; padding; outline(None/Bottom/Top/Left+Right/Frame)
- **Selection icon** (vertical list/dropdown): color
- **Slider** (Between/Before/After/Date picker): on + color
- **Numeric range** (Between/Less-than-or-equal/Greater-than-or-equal): lower/upper handle + input
- **Relative date/time**: Last/Next/This + count + Days/Weeks/Weeks(Cal)/Months/Months(Cal)/Years/Years(Cal)
- **Date picker** (preview): + anchor(Today/First/Last date) + offset + manual calendar/slider
- **General**: title (general, default off); lock aspect; background+transparency; border; shadow; zoom

## BUTTON SLICER (advancedSlicerVisual / textSlicer)
- **Selection**: single select; force selection; show "Select all"; multi-select via Ctrl
- **Multi-button layout**: orientation(Vertical/Horizontal/Grid); fixed size; height/width(px); rows/cols(grid); fit-to-space
- **Buttons (per state Default/On hover/On press/Selected/Disabled)**: fill color(fx-capable); border color/width/radius; font family/size/color/bold/italic; accent bar (color/width/position); padding; shadow/glow
- **Shape**: Rectangle/Rounded rectangle/Snipped tab; corner radius
- **Callout value / Callout label / Highlight label**: font/color; image/icon
- CF-capable: callout values, callout labels, button backgrounds, borders, effects

COMMON VALUE RANGES: font 8-60pt; gridline width 1-10px; icon/expand size 8-60px; image 8-512px; decimals 0-15; transparency 0-100%.
