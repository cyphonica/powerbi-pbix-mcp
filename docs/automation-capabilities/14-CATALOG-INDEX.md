# Complete Power BI visual property catalog + remaining-gap index

The answer to "every property for every visual" + "everything programmatic we don't have yet". Combed from learn.microsoft.com + the authoritative theme schema.

## THE AUTHORITATIVE SOURCE (build backbone)
`reportThemeSchema-2.155.json` (microsoft/powerbi-desktop-samples / Report Theme JSON Schema). It enumerates EVERY visual type's EVERY formatting card's EVERY property + types/enums. Structure: visualStyles -> <visualType> -> <preset|"*"> -> $ref visual-<type> = allOf[commonCards, visual-specific cards]. This file IS the property registry - the build ingests it (Node JSON.parse; PS chokes on case-colliding keys) rather than hand-typing.

## 52 visual types (canonical visualType keys) - see 10-catalog-backbone
report/page/filter/group; cartesian (clusteredColumnChart, columnChart, hundredPercentStackedColumnChart, clusteredBarChart, barChart, hundredPercentStackedBarChart, lineChart, areaChart, stackedAreaChart, hundredPercentStackedAreaChart, lineClusteredColumnComboChart, lineStackedColumnComboChart, scatterChart, ribbonChart); pie/donut/treemap/funnel/waterfallChart; card/cardVisual/multiRowCard/kpi/gauge; tableEx/pivotTable; slicer/advancedSlicerVisual/textSlicer/listSlicer; map/filledMap/shapeMap/azureMap; decompositionTreeVisual/keyDriversVisual/qnaVisual/aiNarratives/scorecard; textbox/image/shape/actionButton/bookmarkNavigator/pageNavigator/pythonVisual/scriptVisual/rdlVisual.

## Catalog files (this folder)
- 10-catalog-backbone.md - 52 types + commonCards (general/title/subTitle/divider/spacing/background/border/dropShadow/padding/visualHeader/visualHeaderTooltip/visualTooltip/visualLink/stylePreset/lockAspect) EVERY visual inherits + theme top-level (colors/structural/textClasses).
- 13-catalog-cartesian.md - 13 chart types, 17 cards (legend, X/Y/secondary axis, data colors, markers, lines, data labels, total labels, series labels, ribbons, analytics pane, small multiples, zoom slider, plot area).
- 12-catalog-parttowhole-singlenumber.md - pie/donut/treemap/funnel/waterfall/gauge/kpi/card/multiRowCard/cardVisual/shape/textbox.
- 09-catalog-table-slicer.md - tableEx/pivotTable/slicer/advancedSlicerVisual (the most property-dense: grid/headers/values/totals/subtotals/CF/slicer-controls).
- 11-catalog-maps-ai-nondata.md - map/filledMap/shapeMap/azureMap(8 layers)/decompositionTree/keyDrivers/qna/aiNarratives/scorecard/image/button/python.
- 08-remaining-gaps.md - everything programmatic beyond per-visual properties still missing.

## Sourcing reality (important)
Microsoft Learn is NARRATIVE for most visuals (names cards, rarely enumerates every sub-property/type/range). The doc catalogs above capture all cards + the documented properties/enums. The COMPLETE, exact, machine-readable enumeration (every property key + type) is ONLY in reportThemeSchema-2.155.json. So: the engine's universal set_visual_format ALREADY sets any property by name; the schema gives the complete known-property catalog for discovery/validation. Together = "every property of every visual represented".

## BUILD PLAN to represent every property
1. **Ingest reportThemeSchema-2.155.json** into the engine as a bundled property registry (visualType -> cards -> properties -> {type, enum, range}). One generated data file.
2. **list_visual_properties(visualType)** / **get_visual_schema(visualType)** - return the full card+property catalog for any visual (discovery).
3. **validate** in set_visual_format / set_visual_format_selector against the registry (warn on unknown card/property/value; coerce by declared type).
4. **list_visual_types** - the 52 canonical types.
5. Keep set_visual_format as the universal setter (already ships) - the registry makes it complete + safe, not 1000 setters.

## REMAINING non-property gaps to build (from 08) - grouped
- Report: visual calculations, native sparklines, drill-down/up + drill-through field config, page/bookmark navigator visuals, full theme authoring (dataColors/structural/textClasses/named presets), mobile detail, accessibility (alt text/tab order), personalization, custom-visual import, wallpaper, PBIR read/write.
- Model: Q&A synonyms (linguistic schema), table detail rows, calc-group precedence/ordinal, DirectLake/hybrid, composite source groups, annotations/extended properties, TMSL admin + TMDL import, model-health/VertiPaq metadata.
- Power Query: ~10 more transforms (unpivot/merge-columns/expand/top-bottom-N/sort/select), M parameters, folding hints, connector source steps.
- Service tier (F, deferred): publish/refresh/executeQueries/ExportToFile/scanner/git/pipelines/gateway/push/paginated.
