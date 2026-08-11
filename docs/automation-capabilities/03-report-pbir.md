# Report + project authoring - PBIP / PBIR (enhanced report format) + theme JSON

Source: learn.microsoft.com/power-bi/developer/projects + public schemas github.com/microsoft/json-schemas/fabric/item/report/definition. PBIR (enhanced, version 4.0+) is the code-authorable report format (per-file JSON schemas). Our engine currently edits the LEGACY report.json (Report/Layout inside .pbix); PBIR is the folder-based successor with the SAME conceptual surface (names below match what Wave A wrote).

## PBIR definition/ folder
report.json, version.json, reportExtensions.json (report-level measures), pages/pages.json (+ per-page page.json + visuals/<v>/visual.json + mobile.json), bookmarks/<name>.bookmark.json + bookmarks.json.

## report.json (report scope)
- themeCollection (baseTheme + customTheme), objects (outspacePane filter-pane chrome, section), filterConfig (report-wide filters), resourcePackages (register themes/images/custom visuals), publicCustomVisuals / organizationCustomVisuals.
- **settings (ExplorationSettings)** - many code-flippable behaviours: useStylableVisualContainerHeader, hideVisualContainerHeader, defaultFilterActionIsDataFilter (filter vs highlight), defaultDrillFilterOtherVisuals, useCrossReportDrillthrough, allowChangeFilterTypes, allowInlineExploration (personalize), useEnhancedTooltips/useScaledTooltips, filterPaneHiddenInEditMode, disableFilterPaneSearch, pagesPosition, isPersistentUserStateDisabled, exportDataMode, queryLimitOption.
- slowDataSourceSettings (apply buttons / cross-highlight).

## reportExtensions.json - report-level measures
entities[].measures[]: name, dataType, expression (DAX), formatString, dataCategory, hidden, description, displayFolder. (Measures only, no calc columns.)

## page.json (page scope)
name, displayName, displayOption (FitToPage/FitToWidth/ActualSize/...), height/width, visibility (AlwaysVisible/HiddenInViewMode), howCreated, filterConfig (page filters), **pageBinding** (type Default/Drillthrough/Tooltip + parameters wiring drillthrough/tooltip fields + referenceScope CrossReport + acceptsFilterContext), objects (pageInformation, pageSize, background, displayArea, outspace, outspacePane, filterCard, pageRefresh, personalizeVisual), **visualInteractions[]** ({source,target,type Default/DataFilter/HighlightFilter/NoFilter} = edit interactions), autoPageGenerationConfig.

## visual.json (visual scope) - richest
- position (x,y,z,width,height,tabOrder); visual XOR visualGroup; parentGroupName; filterConfig (visual filters); isHidden; howCreated.
- visual.visualType (open string), query.queryState (ProjectionState by role -> projections[] {queryRef, displayName, active, hidden, format}), sortDefinition (sort[] + isDefaultSort), **objects** (data-role formatting cards), **visualContainerObjects** (chrome: title, subTitle, divider, spacing, background, padding, border, dropShadow, visualLink, visualTooltip, stylePreset, visualHeader, visualHeaderTooltip), expansionStates (drill state), **syncGroup** (groupName, fieldChanges, filterChanges = slicer sync), drillFilterOtherVisuals.
- visualGroup: displayName, groupMode (ScaleMode/ScrollMode), objects.

## Formatting value model (formattingObjectDefinitions)
card -> array of {properties{prop: valueExpr}, selector}. **selector** scopes formatting: metadata (field), id, data (DataRepetitionSelector: scopeId/roles/total/dataViewWildcard) -> enables per-series / per-category / totals / conditional formatting. Value exprs: Literal ({expr:{Literal:{Value:"4L"|"true"|"'str'"}}}), solid color ({solid:{color:"#RRGGBB"}}), ThemeDataColor ({ColorId, Percent}), FillRule (conditional/gradient, data-bound, NOT in theme files).

## filterConfiguration (report/page/visual)
filters[] FilterContainer: name, displayName, ordinal, field, type (Categorical/Range/Advanced/Passthrough/TopN/Include/Exclude/RelativeDate/Tuple/RelativeTime/VisualTopN), filter (FilterDefinition From+Where), howCreated, **isHiddenInViewMode**, **isLockedInViewMode**, objects (filter card formatting incl. requireSingleSelect/isInvertedSelectionMode). + filterSortOrder.

## semanticQuery - the query/filter expression language
QueryExpressionContainer nodes: SourceRef/Column/Measure/Hierarchy/HierarchyLevel/GroupRef/RoleRef, Aggregation/Min/Max/Percentile/Arithmetic, Comparison/And/Or/Not/Between/In/Contains/StartsWith, DateSpan/DateAdd/Now, Literal/DefaultValue/AnyValue, Subquery/Conditional/FillRule/ThemeDataColor. QueryDefinition: Version=2, From/Select/Where/OrderBy/GroupBy/Top/VisualShape/Transform.

## bookmarks (<name>.bookmark.json)
options: applyOnlyToTargetVisuals, targetVisualNames[], suppressActiveSection, suppressData, suppressDisplay (= the Data/Display/Current-page/Selected-visuals toggles). explorationState: activeSection, sections{} (SectionState -> visualContainers{} VisualContainerState -> singleVisual + highlight). VisualContainerDisplayState.mode = maximize/spotlight/elevation/hidden. SingleVisualConfigState carries objects (formatting), orderBy, activeProjections, **parameters (field parameters)**, expansionStates. bookmarks.json: items[] single or group (children[]).

## theme JSON (reportThemeSchema)
dataColors[], good/neutral/bad, maximum/center/minimum/null (CF gradient stops); structural colors (firstLevelElements/foreground ... secondaryBackground, tableAccent); textClasses (callout/title/header/label + 8 secondary, fontFace/fontSize/color); **visualStyles{visualName{stylePreset{card[{prop:value}]}}}** (named style presets). Cannot set in theme: CF rules, data-bound items.

## GAP-RELEVANT for the report layer (vs our tools)
report settings (ExplorationSettings toggles), report-level measures (reportExtensions), page displayOption/visibility, pageBinding tooltip+drillthrough+params (Wave B), visualInteractions (Wave A done), syncGroup (Wave A done), filter scopes + pane + lock/hide (Wave A done), bookmark options toggles + spotlight/maximize/focus modes, field parameters in visual state, expansionStates (drill), the selector model for per-series/per-category/total formatting (deeper than current set_visual_format), visualGroup (Wave B), theme visualStyles presets.

NOTE: PBIR is preview and will be the only format at GA. Our legacy-Layout tools remain valid for .pbix today; a future PBIR read/write path would future-proof.
