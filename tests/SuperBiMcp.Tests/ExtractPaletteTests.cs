using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Proves the logo-palette extraction (GenerateTheme's logoPath source; ExtractPalette underneath) on the
/// System.Drawing/GDI+ implementation: authored PNGs with known colour blocks come back with the dominant
/// brand colour first, the white background filtered out, and fully transparent pixels skipped. GDI+ is
/// Windows-only, so each test skips elsewhere - mirroring the engine's own runtime degrade.
/// </summary>
public sealed class ExtractPaletteTests
{
    private static (ReportService svc, string sid) NewReport()
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);
        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 32),
            ["displayName"] = "Page1", ["ordinal"] = 0,
            ["visualContainers"] = new JsonArray(),
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720,
            ["displayOption"] = 1,
        };
        var root = new JsonObject { ["sections"] = new JsonArray { section }, ["config"] = "{}", ["filters"] = "[]" };
        var session = new ReportSession
        {
            Id = store.NewId("report"),
            PbixPath = "in-memory.pbix",
            Layout = new ReportLayout { Root = root, LayoutPartName = "Report/Layout" },
        };
        store.AddReport(session);
        return (svc, session.Id);
    }

    private static JsonObject Result(object result) =>
        JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject ?? new JsonObject();

    private static (int r, int g, int b) Channels(string hex) => (
        Convert.ToInt32(hex.Substring(1, 2), 16),
        Convert.ToInt32(hex.Substring(3, 2), 16),
        Convert.ToInt32(hex.Substring(5, 2), 16));

    // the resample can nudge a bucket average by a point or two - assert per-channel closeness, not equality
    private static void AssertNear(string expectedHex, string? actualHex)
    {
        Assert.NotNull(actualHex);
        var e = Channels(expectedHex); var a = Channels(actualHex!);
        Assert.True(Math.Abs(e.r - a.r) <= 6 && Math.Abs(e.g - a.g) <= 6 && Math.Abs(e.b - a.b) <= 6,
            $"expected ~{expectedHex} but got {actualHex}");
    }

    [SkippableFact]
    [SupportedOSPlatform("windows6.1")]
    public void GenerateTheme_FromLogo_DominantColourFirst_BackgroundFiltered()
    {
        Skip.IfNot(OperatingSystem.IsWindowsVersionAtLeast(6, 1), "GDI+ palette extraction is Windows-only");
        string path = Path.Combine(Path.GetTempPath(), $"sbi-logo-{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new Bitmap(64, 48, PixelFormat.Format32bppArgb))
            {
                using (var gfx = Graphics.FromImage(bmp))
                {
                    gfx.Clear(Color.White);                                 // background - must be filtered out
                    using var red = new SolidBrush(Color.FromArgb(200, 30, 40));
                    using var blue = new SolidBrush(Color.FromArgb(30, 80, 200));
                    gfx.FillRectangle(red, 0, 0, 40, 48);                   // dominant block #C81E28
                    gfx.FillRectangle(blue, 48, 0, 16, 48);                 // smaller block #1E50C8
                }
                bmp.Save(path, ImageFormat.Png);
            }
            var (svc, sid) = NewReport();
            var res = Result(svc.GenerateTheme(sid, "Logo", null, null, "executive", "Segoe UI",
                false, 8, true, true, false, path));
            Assert.Equal("logo", (string?)res["source"]);
            var palette = (JsonArray)res["palette"]!;
            AssertNear("#C81E28", (string?)palette[0]);                     // most-used brand colour first
            AssertNear("#1E50C8", (string?)palette[1]);                     // then the next distinct one - never the white
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows6.1")]
    public void GenerateTheme_FromLogo_TransparentPixelsSkipped()
    {
        Skip.IfNot(OperatingSystem.IsWindowsVersionAtLeast(6, 1), "GDI+ palette extraction is Windows-only");
        string path = Path.Combine(Path.GetTempPath(), $"sbi-logo-{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            {
                using (var gfx = Graphics.FromImage(bmp))
                {
                    // saturated red under ZERO alpha: if the alpha<24 skip ever broke, this would win the count
                    gfx.Clear(Color.FromArgb(0, 255, 0, 0));
                    using var green = new SolidBrush(Color.FromArgb(46, 158, 79));
                    gfx.FillRectangle(green, 16, 16, 32, 32);               // the only opaque colour #2E9E4F
                }
                bmp.Save(path, ImageFormat.Png);
            }
            var (svc, sid) = NewReport();
            var res = Result(svc.GenerateTheme(sid, "Logo", null, null, "executive", "Segoe UI",
                false, 8, true, true, false, path));
            Assert.Equal("logo", (string?)res["source"]);
            AssertNear("#2E9E4F", (string?)((JsonArray)res["palette"]!)[0]);
        }
        finally { File.Delete(path); }
    }
}
