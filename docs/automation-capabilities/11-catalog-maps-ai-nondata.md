# Visual property catalog - maps, AI, non-data

Source: learn.microsoft.com per-visual docs. [DOC]=verbatim, [STD]=standard card present. NOTE: AI visuals' full format cards are NOT doc-enumerated (only behaviour) - use reportThemeSchema-2.155.json for their exact keys. All inherit commonCards (see 10-catalog-backbone).

## MAPS
- **map** (Bing bubble): field wells Location/Lat/Long/Size/Legend; bubbles (size/scale, default color, per-category colors); category labels (on, font/size/color/background/transparency); map styles (Aerial/Road/Dark/Light/Grayscale); map controls (auto-zoom, zoom buttons, lasso, labels, country/state/county borders, buildings/roads); heat map (radius, transparency, gradient stops).
- **filledMap** (Bing choropleth): fill colors -> default color + fx (Gradient/Rules/Field-value; min/max/center color+value); data colors per category; zoom/map controls; map styles; category labels.
- **shapeMap** (preview, max 1500 pts): map settings (map type AU/AT/BR/CA/FR/DE/IE/IT/MX/NL/UK/USA/Custom; upload topojson/geojson; URL; projection Equirectangular/Mercator/Orthographic); colors (default+fx, per-region, per-legend, saturation gradient, blank area); border (color+thickness); zoom.
- **azureMap** (8 layers):
  - Map settings/Style: style(blank/grayscale dark|light/high-contrast/night/road/satellite...), labels, borders(country/state/county), buildings, road details.
  - Map settings/View: auto-zoom, zoom 0-22, center lat/long, heading 0-360, pitch 0-60.
  - Map settings/Controls: world wrap, style picker, navigation, selection(circle/rect/polygon/travel-time), geocoding culture.
  - Shared layer: unselected transparency, min/max data value, layer position(Above labels/Below labels/Below roads), min/max zoom.
  - Marker layer (bubble): zoom, pitch/rotation alignment, apply-to(All/category/point), shape(Icon/Image, change icon, size 8px, scale, min/max, rotation+fx, range scaling), color+fx+transparency, border(high-contrast/match-fill/color/transparency/width 2px), legend, category labels.
  - Pie chart layer: size, fill transparency, outline color/transparency/width, min/max zoom, position.
  - 3D column layer: shape(Box/Cylinder), height, scale-on-zoom, width, fill color, transparency, min/max zoom.
  - Heat map layer: size/radius(px 1-200 default 20 / meters 1-4M), units, transparency 0-100, intensity 0-1 (0.5), use-size-as-weight, gradient(low/center/high), min/max zoom 1-22.
  - Cluster bubbles: size 1-50px(12), color, text size 1-60px(18), text color, border color, border width 1-20px(2).
  - Reference layer: data source(File/URL; json/geojson/wkt/kml/shp/csv), points/lines/polygons fill+border, unmapped objects (renders first 30k features).
  - Tile layer: url({x}/{y}/{z}/{quadkey}/{bbox}), tile size, N/S/E/W bounds, transparency, is-TMS, min/max zoom, position.
  - Filled map layer: toggle, shape fill transparency, colors+conditional, border(color/width 1-10/transparency), min/max zoom 0-22, position.
  - Legend via Data colors (no standalone legend card).

## AI (format cards mostly undocumented - schema file has exact keys)
- **decompositionTreeVisual**: field wells Analyze/Explain-by; Analysis card (AI splits on/off, type Absolute/Relative); behaviour High/Low value, lock level. Limits 50 levels/5000 pts. (tree/data-bars/labels cards exist, undocumented.)
- **keyDriversVisual** (key influencers): field wells Analyze/Explain-by/Expand-by; Analysis card (type Categorical/Continuous, enable counts, count type absolute/relative); tabs Key influencers/Top segments.
- **qnaVisual** (deprecating Dec 2026): question field card (background, accepted/unrecognized/warning underline colors, hover color); title card.
- **aiNarratives** (smart narrative): Copilot/Custom mode; text formatting (bold/color); dynamic values (add value, format currency/decimals/separator, show-auto-values); header-icons smart-narrative toggle. Max 4/visual, 16/page.
- **scorecard**: add single/multiple metrics; font size/style/colors/backgrounds; header toggle; status overview cards; metric elements (owners/due-date toggles); goal fields (name/owners/current/target/status/dates/tracking-cycle).

## NON-DATA
- **textbox**: text runs (font family/size/color, bold/italic/underline, sub/superscript, alignment, indent, bullet/numbered lists, hyperlink); Values (fx measure, display units None/K/M/B/T, decimals 0-15, text/background color, alignment, dynamic format strings); general (size/lock/position/padding/responsive, title, effects, alt text).
- **image**: image source (Upload/URL/from-data, per-state Default/Hover/Pressed); image fit (Fit/Fill/Center/Stretch); shape (corner radius); border (color/width per state); background; effects per-state (exposure/contrast/saturation/blur); action (toggle + type); general (alt text fx).
- **shape** (basic shapes): shape types (rectangle/oval/line/arrow/triangle + button-family); shape style (fill+color, fill transparency, border/line color/weight 1-10/transparency, rounded corners radius, rotation deg); text on shape (font controls); effects (shadow color/position/offset, glow); action (toggle+type); line-specific (no fill).
- **actionButton/button** (states Default/Hover/Press/Disabled/Loading): Text (toggle, text+fx, font family/size/color/bold/italic/underline, H/V align, padding); Icon (toggle, type built-in/custom, custom image, color, weight, transparency, fit, placement L/R/Above/Below, align, size); Fill (toggle, color per state, transparency, image); Outline (toggle, color, weight, transparency, rounded corners, line style); Shape (type rectangle/rounded/parallelogram/arrow/chevron/pentagon/oval, corner radius); Effects (shadow, glow, shape rotation deg, text rotation); Action (toggle, type Back/Bookmark/Drillthrough/PageNavigation/BookmarkNavigation/Qna/WebUrl/ApplyAllSlicers/ClearAllSlicers/DataFunction, destination/bookmark/drillthrough-page/web-url/tooltip + fx).
- **pythonVisual / scriptVisual (R)**: NO visual-specific cards - rendering is in-script; only common cards (general/title/effects/header-icons/alt-text). Python 150k rows/250MB.
