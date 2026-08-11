# Visual property catalog - part-to-whole + single-number

Source: learn.microsoft.com per-visual docs. CAVEAT: Learn is narrative for most visuals (cards named, sub-properties often not enumerated with types/ranges); the format-pane-overview (General tab) + reportThemeSchema-2.155.json are the only fully-enumerated sources -> build ingests the schema for exact keys. All inherit commonCards (see 10).

## COMMON General-tab sections (format-pane-overview - authoritative)
Properties (size H/W + lock-aspect, position H/V, padding T/B/L/R, responsive, maintain-layer-order), Title (+subtitle+divider+spacing), Effects (background+transparency, visual border color/rounded/width, shadow color/offset/position), Data format (apply-to + format), Header icons (master + ~20 per-icon toggles + colors), Tooltips (type Default/Report-page + page, text, background), Alt text (description + fx).

## PIE / DONUT (pieChart, donutChart)
- Legend (on, position, title+text, color/font/size)
- Data colors (show all, per-category color)
- Detail labels (label contents Category/Data value/Percent of total/combos/All; position Inside/Outside/Preferred; display units; value+percent decimals; overflow text; font/size/color; background+transparency)
- Rotation (pie - paginated only on Learn); Slices spacing/border (not enumerated)
- DONUT extra: Slices > inner radius (0-95, 0=pie; 3rd-party)

## TREEMAP
- Legend; Data colors (show all, per-category, diverging min/center/max color+value); Category labels (color/size/font); Data labels/Values (color, display units, decimals, font/size). Field wells Category/Details/Values/Color-saturation.

## FUNNEL (no legend)
- Data colors (default, show-all per-category, diverging); Category labels; Data labels (style Data value / +percent-of-first / +percent-of-previous / percent-of-first / percent-of-previous; position Inside center/Inside end/Inside base/Outside end/Auto; color/units/decimals/size/font); Conversion rate labels (position/color/font/size); Percent bars (color/transparency/stroke/dashes); Y axis (color/size/font/title).

## WATERFALL
- Breakdown (max breakdowns default 5 -> rest = "Other"); Sentiment colors (Increase green / Decrease red / Total blue / Other); X axis (values/font/color/size, type Continuous/Categorical, concatenate labels, title+style Title/Units/Both); Y axis (values/font, display units, range min/max, logarithmic, round range, invert, title, gridlines color/width); Data labels (color/style/size/units/decimals); Connectors (color/transparency/stroke - 3rd-party); Zoom slider; Legend.

## GAUGE (radial - no legend/totals)
- Gauge axis (Min auto=0, Max auto=2x value, Target editable only when Target field-well empty); Data colors (Fill arc +fx, Target needle color+thickness); Callout value (font/size/color+fx, display units, decimals); Target label / Data labels (color/size).

## KPI (modern)
- Callout value (display units, decimals, text formatting); Icons (master - green check up / red ! down); Trend axis (master, direction High-is-good/Low-is-good, color settings, transparency); Target label / Distance to goal (style, direction); Date (font/style/color). Classic cards (Indicator/Goals/Color-coding good-neutral-bad) via Restore-default.

## CARD (legacy single-value)
- Callout value (font family/size/B-I-U/color, display units None/K/M/B/T, decimals 0-15); Category label (on, font/size/color/BIU); Word wrap.

## MULTI-ROW CARD (legacy, Learn retired -> merged into cardVisual)
- Data labels (font/size/color); Category labels (on, font/size/italic/bold/color); Cards > Card Title (on, font/size/color); Cards > Style (Outline None/Bottom/Top/Left/Right/Top+Bottom/Left+Right/Frame, border position, outline color, stroke width, background, padding); Cards > Accent bar (on, color+fx, thickness); Title.

## NEW CARD VISUAL (cardVisual - unified GA, "Apply settings to" dropdown)
- Callout (layout, value font/size/BIU/color, display units, decimals, image on + type Select-from-data/upload/URL + fit Fit/Stretch/Fill/Center + size px)
- Reference labels (add label measure, detail + field, divider, color+fx, display units/decimals)
- Reference labels layout (background, gaps, padding, backgrounds)
- Cards > Layout (arrangement Horizontal/Vertical/Collage, order Image/Callout/Reference, callout size %, per-section backgrounds, padding 12px, individual padding)
- Image hero (on, states Default/Hover, source/fit/transparency/effects/background)
- Category header (background, image, fit, transparency)
- Multi-card layout (arrangement Grid/Vertical/Horizontal, columns/rows default 10/10, fixed size, fit-to-space); Multi-category (autogrid max 4 rows / off + rows)

## SHAPES (shape) + TEXTBOX (textbox) - see also 11
- shape: shape type gallery, rotation deg, fill color+transparency, border color/weight/rounded, shadow/glow, text inside, size/position, action, maintain-layer-order. (Title=No, Background=Yes)
- textbox: inline font color/size/sub-superscript/alignment/indent/bullet+numbered lists/family/BIU/text; dynamic Values (fx field, display units None/K/M/B/T, decimals 0-15, text/background color, alignment, dynamic format string). Textbox CAN pin to dashboard.
