# Visual property catalog - BACKBONE (authoritative)

Source: reportThemeSchema-2.155.json (PBI Desktop 2.155, June 2026), parsed directly.
Raw: https://raw.githubusercontent.com/microsoft/powerbi-desktop-samples/main/Report%20Theme%20JSON%20Schema/reportThemeSchema-2.155.json
(the unversioned reportThemeSchema.json path 404s; repo ships versioned files only.)

## *** BUILD KEY ***: this schema file IS the complete property registry.
visualStyles -> <visualType> -> <preset|"*"> -> $ref visual-<type> = allOf[commonCards, visual-specific cards]. Every card body is an ARRAY of objects (`"title":[{...}]`). color-typed props take a fill object `{solid:{color:"#RRGGBB"}}` not a bare hex. The build should INGEST this schema (Node JSON.parse - PS 5.1 chokes on case-colliding keys like gridlineColor/gridLineColor) to drive a property catalog/registry + validation, layered on the universal set_visual_format.

## Master visualType list (52 keys, canonical)
Containers/page: report, page, filter, group.
Cartesian: clusteredColumnChart, columnChart (=stacked column), hundredPercentStackedColumnChart, clusteredBarChart, barChart (=stacked bar), hundredPercentStackedBarChart, lineChart, areaChart, stackedAreaChart, hundredPercentStackedAreaChart, lineClusteredColumnComboChart, lineStackedColumnComboChart, scatterChart, ribbonChart.
Part-to-whole: pieChart, donutChart, treemap, funnel, waterfallChart.
Single-number: card, cardVisual (new card), multiRowCard, kpi, gauge.
Tabular: tableEx (table), pivotTable (matrix).
Slicers: slicer, advancedSlicerVisual, textSlicer, listSlicer.
Maps: map, filledMap, shapeMap, azureMap.
AI: decompositionTreeVisual, keyDriversVisual (=key influencers), qnaVisual, aiNarratives (=smart narrative), scorecard.
Non-data/script: textbox, image, shape (=basic shape), actionButton, bookmarkNavigator, pageNavigator, pythonVisual, scriptVisual (=R), rdlVisual (paginated).

NAMING RECONCILIATIONS (vs guessed names): stacked col/bar = columnChart/barChart (NO stackedColumnChart/stackedBarChart); key influencers = keyDriversVisual; smart narrative = aiNarratives; basic shape = shape; new card = cardVisual; R = scriptVisual; no rVisual/shapeMapVisual/smartNarrative keys.

## commonCards - EVERY visual inherits these (card -> properties : type)
Shared types: fontSize number 6-45; alignment enum left|center|right; heading enum Normal|Heading2..6; color = fill object.

- general: x, y, width, height (number); altText (text); keepLayerOrder, allowBinnedLineSample, allowOverlappingPointsSample (bool)
- title: show(bool), text, heading, fontColor(color), background(color), alignment, fontFamily, fontSize, bold, italic, underline, titleWrap
- subTitle: show, text, heading, fontColor, alignment, fontFamily, fontSize, bold, italic, underline, titleWrap
- divider: show, color, width, style(solid|dashed|dotted), ignorePadding
- spacing: customizeSpacing, verticalSpacing, spaceBelowTitle, spaceBelowSubTitle, spaceBelowTitleArea
- background: show, color, transparency
- border: show, color, width, radius
- dropShadow: show, color, position(Outer|Inner), preset(BottomRight|Bottom|BottomLeft|CenterRight|Center|CenterLeft|TopRight|Top|TopLeft|Custom), angle, shadowBlur, shadowDistance, shadowSpread, transparency
- padding: top, bottom, left, right
- visualHeader: show, background, border, foreground, transparency + ~20 show* toggles (showDrillDownExpandButton, showDrillUpButton, showFocusModeButton, showPinButton, showOptionsMenu, showFilterRestatementButton, showTooltipButton, showSmartNarrativeButton, showCopilotSummaryButton, showSeeDataLayoutToggleButton, showPersonalizeVisualButton, showSetAlertButton, ...)
- visualHeaderTooltip: type(Default|Canvas), section, text, background, titleFontColor, fontFamily, fontSize, bold, italic, underline, transparency
- visualTooltip: show, type(Default|Canvas), section, background, titleFontColor, valueFontColor, actionFontColor, font*, transparency, sentenceTemplate, showSentenceFormat, showActionsInTooltips, showChartSpecificTooltips, showTooltipFieldsOnly, showValuesInBold
- visualLink: show, type(Back|Bookmark|Drillthrough|PageNavigation|Qna|WebUrl|ApplyAllSlicers|ClearAllSlicers|DataFunction), bookmark, webUrl, navigationSection, drillthroughSection, dataFunction, tooltip(s), showDefaultTooltip, suppressDefaultTooltip
- stylePreset: name
- lockAspect: show

## Theme top-level (siblings of visualStyles)
- Colors: dataColors[], good/neutral/bad, maximum/center/minimum/null (CF gradient).
- Structural (6 + accent): firstLevelElements(=foreground), secondLevelElements(=foregroundNeutralSecondary), thirdLevelElements(=backgroundLight), fourthLevelElements(=foregroundNeutralTertiary), background, secondaryBackground(=backgroundNeutral), tableAccent.
- textClasses: 4 primary (callout/header/title/label) + 8 secondary (largeTitle/semiboldLabel/largeLabel/smallLabel/lightLabel/boldLabel/largeLightLabel/smallLightLabel); each fontSize/fontFace/color (+bold/titleBold).
