namespace SuperBiMcp.Services;

/// <summary>One visual's geometry as the wireframe lint sees it - format-agnostic, so the same checker
/// runs over the legacy Report/Layout reader AND the PBIR per-file reader.</summary>
public sealed record WireVisual(string Name, string? Type, double X, double Y, double W, double H,
    double Z, bool Hidden, string? Title = null);

/// <summary>One page's canvas plus its visuals, as collected by either report reader.</summary>
public sealed record WirePage(string Page, double Width, double Height, IReadOnlyList<WireVisual> Visuals);

/// <summary>
/// READ-ONLY wireframe lint: overlap pairs, off-canvas placement, tiny/zero-size visuals, z-order
/// anomalies (a data visual rendering ABOVE an overlapping slicer) and margin/gap statistics. Never
/// mutates anything - every violation names the existing FIXER tool (auto_arrange / align_visuals /
/// tidy_slicer_layout_v2 / move_visual / resize_visual / set_visual_z_order) so the caller can act.
/// Pure geometry over <see cref="WirePage"/> records, so it unit-tests without a report session.
/// </summary>
public static class WireframeAuditor
{
    // decorative/container types that legitimately sit under or across data visuals (panels, banners,
    // logos, buttons, groups) - overlap involving these is design, not a defect.
    private static readonly HashSet<string> Decorative = new(StringComparer.OrdinalIgnoreCase)
        { "textbox", "image", "shape", "basicShape", "actionButton", "visualGroup", "shapeVisual" };

    private static bool IsSlicer(WireVisual v) =>
        string.Equals(v.Type, "slicer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(v.Type, "advancedSlicerVisual", StringComparison.OrdinalIgnoreCase);

    private static bool IsDecorative(WireVisual v) => v.Type != null && Decorative.Contains(v.Type);

    private static string Label(WireVisual v) => v.Title ?? v.Name;

    /// <summary>Intersection area of two rectangles (0 when they do not overlap).</summary>
    internal static double OverlapArea(WireVisual a, WireVisual b)
    {
        double w = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
        return (w <= 0 || h <= 0) ? 0 : w * h;
    }

    /// <summary>Run the full lint over the collected pages. Returns per-page stats plus the violation
    /// list [{page, visual, kind, detail, suggestedFix}].</summary>
    public static object Audit(IReadOnlyList<WirePage> pages)
    {
        var violations = new List<object>();
        var pageStats = new List<object>();

        foreach (var page in pages)
        {
            var visible = page.Visuals.Where(v => !v.Hidden).ToList();

            // ---- per-visual checks: off-canvas + tiny/zero size ----
            foreach (var v in visible)
            {
                bool offCanvas = v.X < -0.5 || v.Y < -0.5
                                 || v.X + v.W > page.Width + 0.5 || v.Y + v.H > page.Height + 0.5;
                if (offCanvas)
                    violations.Add(new
                    {
                        page = page.Page, visual = Label(v), kind = "off-canvas",
                        detail = $"{v.Type} at ({R(v.X)},{R(v.Y)}) size {R(v.W)}x{R(v.H)} extends outside the {R(page.Width)}x{R(page.Height)} canvas.",
                        suggestedFix = IsSlicer(v) ? "tidy_slicer_layout_v2" : "move_visual (or auto_arrange for a full re-grid)",
                    });

                if (v.W < 8 || v.H < 8)
                    violations.Add(new
                    {
                        page = page.Page, visual = Label(v), kind = "tiny-visual",
                        detail = $"{v.Type} is {R(v.W)}x{R(v.H)} px - effectively invisible (zero/near-zero size is usually leftover debris).",
                        suggestedFix = "resize_visual (or delete_visual if it is debris)",
                    });
            }

            // ---- pairwise checks: overlap + z-order anomalies ----
            for (int i = 0; i < visible.Count; i++)
            {
                for (int j = i + 1; j < visible.Count; j++)
                {
                    var a = visible[i]; var b = visible[j];
                    if (IsDecorative(a) || IsDecorative(b)) continue;   // panels/banners overlap by design
                    double area = OverlapArea(a, b);
                    if (area <= 4) continue;                            // touching edges are fine

                    bool aSlicer = IsSlicer(a), bSlicer = IsSlicer(b);
                    string fix = aSlicer && bSlicer
                        ? "tidy_slicer_layout_v2"
                        : "auto_arrange (or align_visuals + move_visual for a targeted fix)";
                    violations.Add(new
                    {
                        page = page.Page, visual = $"{Label(a)} + {Label(b)}", kind = "overlap",
                        detail = $"{a.Type} '{Label(a)}' and {b.Type} '{Label(b)}' overlap by {R(area)} px2.",
                        suggestedFix = fix,
                    });

                    // z-order anomaly: a data visual stacked ABOVE an overlapping slicer hides the
                    // slicer's dropdown/selection - the classic "the slicer stopped working" defect.
                    if (aSlicer != bSlicer)
                    {
                        var slicer = aSlicer ? a : b;
                        var data = aSlicer ? b : a;
                        if (data.Z > slicer.Z)
                            violations.Add(new
                            {
                                page = page.Page, visual = Label(data), kind = "z-order",
                                detail = $"data visual '{Label(data)}' (z={R(data.Z)}) renders ABOVE overlapping slicer '{Label(slicer)}' (z={R(slicer.Z)}).",
                                suggestedFix = "set_visual_z_order (or bring_to_front on the slicer / send_to_back on the data visual)",
                            });
                    }
                }
            }

            pageStats.Add(BuildPageStats(page, visible));
        }

        return new
        {
            ok = true,
            pagesScanned = pages.Count,
            pages = pageStats,
            violations,
            violationCount = violations.Count,
            verdict = violations.Count == 0 ? "pass" : "review",
        };
    }

    /// <summary>Margin + horizontal-gap statistics for one page (the layout hygiene numbers).</summary>
    private static object BuildPageStats(WirePage page, List<WireVisual> visible)
    {
        object? margins = null;
        object? gaps = null;
        if (visible.Count > 0)
        {
            margins = new
            {
                left = R(visible.Min(v => v.X)),
                top = R(visible.Min(v => v.Y)),
                right = R(page.Width - visible.Max(v => v.X + v.W)),
                bottom = R(page.Height - visible.Max(v => v.Y + v.H)),
            };

            // horizontal gaps between row-mates: two visuals share a row when their vertical extents
            // overlap by at least half the shorter one's height.
            var gapList = new List<double>();
            var ordered = visible.OrderBy(v => v.X).ToList();
            for (int i = 0; i < ordered.Count; i++)
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    var a = ordered[i]; var b = ordered[j];
                    double vOverlap = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
                    if (vOverlap < Math.Min(a.H, b.H) / 2) continue;
                    double gap = b.X - (a.X + a.W);
                    if (gap >= 0) gapList.Add(gap);
                }
            if (gapList.Count > 0)
            {
                gapList.Sort();
                gaps = new
                {
                    count = gapList.Count,
                    min = R(gapList[0]),
                    median = R(gapList[gapList.Count / 2]),
                    max = R(gapList[^1]),
                };
            }
        }
        return new
        {
            page = page.Page, width = R(page.Width), height = R(page.Height),
            visuals = visible.Count, hidden = page.Visuals.Count - visible.Count,
            margins, gaps,
        };
    }

    private static double R(double d) => Math.Round(d, 1);
}
