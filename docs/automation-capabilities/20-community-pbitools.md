# Community pbi-tools/PBIX comb - report internals -> folds into M/N + Wave S

From pbi.tools, data-goblins, deneb.guide, Tabular Editor blog, crossjoin, Ben Gribaudo, MS PBIR schemas. CONFIRMED we already do: SecurityBindings strip (#25), legacy Layout UTF-16LE + double-serialized config/filters/query (#26). New/high-value below.

## 1. Filter/query AST compiler (biggest new area) -> Wave S
- [A] condition compiler: In (array-of-arrays Values), Comparison (ComparisonKind 0=Eq/1=GT/2=GTE/3=LTE/4=LT), Between (two Comparisons under And), And/Or/Not, Contains/StartsWith/DoesNotContain, TopN/VisualTopN.
- [A] **literal type-suffix encoder** (HIGH LEVERAGE, small): Value strings carry type - D=Double "0D", L=Integer "10L", M=Decimal; strings single-quoted "'EUR'"; bool bare; datetime "datetime'...'"; color "'#FF0000'". Wrong = silent blank white box.
- [C] RelativeDate/RelativeTime builder (Comparison vs DateSpan{Now, TimeUnit 0=Day..3=Year}); fixed-anchor relative date (literal instead of Now) - UI-impossible.
- [C] VisualTopN with hidden ranking measure.
- [C] prototypeQuery/query editor: change Aggregation.Function (0=Sum..8=Var) in Select; ScopedEval+AllRolesRef for context-free baselines.
- [C] Passthrough filter restatement (custom card label) + isHidden/isLockedInViewMode.

## 2. CF via fillRule / measure-bound expr -> Wave M (extends the encoder fix)
- [A] property expr swap Literal->Measure (drive ANY property by a measure returning hex/CSS, even where no fx button). THIS is the generalisation of the Wave M encoder fix.
- [B] dataViewWildcard selector -> per-X-axis-category line/series CF (UI-impossible).
- [B] selector taxonomy: static null / columns {metadata} / per-data-point selectionId / scope-identity {data:[DataViewScopeIdentity]}; gradient fillRule (inputRole Gradient, output.property fill).

## 3. SVG/image measures (model-side) -> Wave O/P
- [A] SVG measure generator + set measure dataCategory=ImageUrl (TMDL/TOM, settable on measures now).
- [C] base64-image chunking (CONCATENATEX over 32766-char chunks; ~2MB).
- [B] SVG encoding safety ("->' , %23 for #, utf8, prefix).

## 4. Theme JSON beyond schema -> Wave O
- [C] outspacePane + filterCard array keyed by "$id":"Applied"/"Available" (NOT in published schema).
- [C] named style presets visualStyles.<visual>.<presetName> + stylePreset default (Wave O add_visual_style_preset).
- [C] container cards padding/spacing/divider/dropShadow/visualHeader; embedded base64 page background/wallpaper in theme; custom CF icons (base64); ThemeDataColor {ColorId, Percent} semantic refs; pageSize default for new pages.

## 5. Bookmark explorationState internals -> Wave N (bookmark data-state)
- [B] per-visual display.mode (hidden/spotlight/maximize+maximizedOptions.dataTable/elevation).
- [C] options builder (applyOnlyToTargetVisuals/targetVisualNames/suppress Active/Data/Display).
- [B] HighlightState/orderBy/activeProjections capture (cross-highlight + sort + drill) - the DATA-state Wave N needs.
- [C] bookmarksMetadata groups (items {name}|{name,displayName,children[]}).

## 6. Deneb / Vega-Lite as first-class visual -> Wave S
- [A] Deneb spec writer: visual.objects.vega[0].properties (jsonSpec, jsonConfig, provider vega|vegaLite, renderMode svg|canvas, enableTooltips/Selection/Highlight). Spec stringified one-line, "->\" , single-quoted in expr.Literal.Value (fiddly - #1 failure mode).
- [B] dataset reserved fields (__selected__/__row__/__key__/[measure]__highlight, pbiContainer, pbiColor()/pbiFormat()).
- [B] template hydration (usermeta.deneb placeholders -> real columns).

## 7. Structural .pbix / PBIR -> Wave S + strategic
- [A] **PBIR read/write** (definition/ folder: definition.pbir, report.json, pages/<id>/page.json, visuals/<id>/visual.json, mobile.json, bookmarks/; pageBinding.name unique GUID). STRATEGIC GAP - we're legacy-Layout only. Officially supported + schema-backed; becomes only format at GA.
- [A] **DataMashup / M extract+edit** (MS-QDEFF: inner OPC zip Formulas/Section1.m plain-text M + Metadata XML + Permission Bindings SHA-256). Read/diff/rewrite connection strings + M outside Desktop. We preserve DataModel but don't touch Mashup.
- [C] mobile.json/mobileState.json separate layer; annotations arrays for engine metadata.

## 8. Page/visual props (mostly COVERED) - confirm: pageBinding.type, visibility, displayOption ActualSizeTopLeft, tabOrder -1, maintainLayerOrder, expansionStates, button action Type enum + fx (Text only), dynamic format strings (have).

PRIORITY: PBIR read/write (strategic), filter/query AST compiler + literal encoder (#1-2), measure-bound expr + dataViewWildcard CF (#7-8), Deneb writer (#22), DataMashup M edit (#28). Reference: MS PBIR json-schemas = de-facto Layout-internals docs. Verify Between/Contains/RelativeDate node spellings vs a real visual.json before shipping.
