using System.Text.Json.Nodes;

namespace SuperBiMcp;

/// <summary>
/// 300 ready-made report starting points. Each is a {recipe + theme + titles + a config skeleton with
/// &lt;placeholders&gt;}. A front end lists these (`SuperBiMcp templates`), the user picks one, maps the
/// &lt;placeholder&gt; fields to their own pbix's tables/measures, and feeds it to `build`. 30 report
/// archetypes x 10 brand themes = 300 distinct, customisable launch points.
/// </summary>
public static class TemplateLibrary
{
    public sealed record Template(string id, string name, string category, string description, string recipe, JsonObject config);

    private static readonly (string name, string brand, string accent)[] Themes =
    {
        ("Navy & Gold", "#16365C", "#C9A227"),
        ("Slate Blue",  "#2B3A4A", "#5B9BD5"),
        ("Forest",      "#1F5132", "#9BC53D"),
        ("Sunset",      "#7A2E2E", "#E8A33D"),
        ("Berry",       "#5B2A4E", "#D6336C"),
        ("Ocean",       "#0B4F6C", "#2BB3C0"),
        ("Charcoal",    "#2E2E38", "#9A6BFF"),
        ("Teal & Gold", "#0E5C5C", "#E0B040"),
        ("Crimson",     "#6B1F2A", "#C0392B"),
        ("Bronze",      "#3E2F23", "#C08457"),
    };

    public static readonly IReadOnlyList<Template> All = Build();

    private static List<Template> Build()
    {
        var blueprints = new (string name, string category, string recipe, string desc, Func<JsonObject> cfg)[]
        {
            ("Executive Overview", "Executive", "executive",
                "One-page leadership summary: KPI cards, hero trend, growth-coloured segment bars.",
                () => Exec("Executive Overview", "Performance summary", "TOTAL SALES")),
            ("Brand Scorecard", "Executive", "executive",
                "Single-brand deep-dive: headline KPIs, trend and segment mix for one brand.",
                () => Exec("Brand Scorecard", "Brand performance deep-dive", "BRAND SALES")),
            ("Growth Tracker", "Executive", "executive",
                "Growth-first view: YoY deltas on every KPI and growth-coloured bars.",
                () => Exec("Growth Tracker", "Year-on-year momentum", "SALES GROWTH")),
            ("Promotion Review", "Executive", "executive",
                "Promo lens: lift, value and volume through the promotional period.",
                () => Exec("Promotion Review", "Promotional performance", "PROMO SALES")),
            ("Category Review", "Category", "category",
                "The flagship 5-page scan: Performance, Price & Volume, Share, Distribution, Ranking.",
                () => Cat("Category Review", "Category   -   Review", "TOTAL SCAN SALES")),
            ("Retail Ranking", "Category", "category",
                "Ranking-led scan with a Brand / Segment / Item view-switcher on the Ranking page.",
                () => Cat("Retail Ranking", "Ranking   -   Who is winning", "RANKED SALES")),
            ("Price & Volume", "Category", "category",
                "Price architecture and volume mix by segment, with the full scan around it.",
                () => Cat("Price & Volume", "Price   -   Volume   -   Mix", "AVG PRICE")),
            ("Share & Trend", "Category", "category",
                "Segment share and chronological trend, with the supporting scan pages.",
                () => Cat("Share & Trend", "Share   -   Trend", "SHARE OF CATEGORY")),
            ("Distribution Tracker", "Category", "category",
                "Distribution and on-shelf availability by brand, plus the scan template.",
                () => Cat("Distribution Tracker", "Distribution   -   Availability", "DISTRIBUTION")),
            ("Total Market Compare", "Cross-Retailer", "crossretailer",
                "Two-retailer total-market view (pairs with conform_dimension): A vs B by entity.",
                () => Xr("Total Market", "Retailer A   vs   Retailer B")),

            // ---- 20 flexible GRID dashboards (the full visual palette, one per analysis category) ----
            ("Sales Overview", "Dashboard", "grid", "KPI cards, sales trend, segment share and a brand bar - the daily driver.",
                () => Grid("Sales Overview", "Performance at a glance", "TOTAL SALES", new JsonArray(
                    Kpi("SALES", "<sales measure>"), Kpi("VOLUME", "<volume measure>"), Kpi("AVG PRICE", "<avg price measure>"), Kpi("GROWTH %", "<growth % measure>"),
                    Vz("lineChart", "Sales trend", 8, 3, DateCol(), Meas("<sales window measure>")),
                    Vz("donutChart", "Share by segment", 4, 3, Dim("<segment>"), Meas("<sales measure>")),
                    Vz("clusteredColumnChart", "Sales by brand", 6, 3, Dim("<brand>"), Meas("<sales measure>")),
                    Vz("tableEx", "Top items", 6, 3, Dim("<item>"), Meas("<sales measure>", "<growth % measure>"))))),
            ("Brand Performance", "Brand", "grid", "Ranked brand bar, brand table and segment pie.",
                () => Grid("Brand Performance", "Who is winning", "BRAND SALES", new JsonArray(
                    Vz("clusteredBarChart", "Sales by brand", 7, 4, Dim("<brand>"), Meas("<sales measure>")),
                    Vz("pieChart", "Brand share", 5, 4, Dim("<brand>"), Meas("<sales measure>")),
                    Vz("tableEx", "Brand scorecard", 12, 3, Dim("<brand>"), Meas("<sales measure>", "<volume measure>", "<growth % measure>"))))),
            ("Store Performance", "Store", "grid", "Store ranking table with a store bar and a region column.",
                () => Grid("Store Performance", "Store-level ranking", "STORE SALES", new JsonArray(
                    Vz("tableEx", "Store ranking", 7, 5, Dim("<store>"), Meas("<sales measure>", "<growth % measure>")),
                    Vz("clusteredColumnChart", "Sales by region", 5, 3, Dim("<region>"), Meas("<sales measure>")),
                    Vz("clusteredBarChart", "Top stores", 5, 2, Dim("<store>"), Meas("<sales measure>"))))),
            ("Region Comparison", "Region", "grid", "Region columns split by banner, a trend and a stacked column.",
                () => Grid("Region Comparison", "Geography", "SALES BY REGION", new JsonArray(
                    Vz("clusteredColumnChart", "Region x banner", 7, 4, Dim("<region>"), Meas("<sales measure>"), Dim("<banner>")),
                    Vz("lineChart", "Trend", 5, 4, DateCol(), Meas("<sales window measure>")),
                    Vz("stackedColumnChart", "Segment mix by region", 12, 3, Dim("<region>"), Meas("<sales measure>"), Dim("<segment>"))))),
            ("Supplier Mix", "Supplier", "grid", "Supplier treemap, a supplier table and a vendor bar.",
                () => Grid("Supplier Mix", "Manufacturer view", "SUPPLIER SALES", new JsonArray(
                    Vz("treemap", "Supplier treemap", 7, 4, Dim("<supplier>"), Meas("<sales measure>")),
                    Vz("clusteredBarChart", "Top suppliers", 5, 4, Dim("<supplier>"), Meas("<sales measure>")),
                    Vz("tableEx", "Supplier scorecard", 12, 3, Dim("<supplier>"), Meas("<sales measure>", "<growth % measure>"))))),
            ("Promotion Effectiveness", "Promotion", "grid", "Promo vs base columns, a lift trend and KPI cards.",
                () => Grid("Promotion Effectiveness", "Promo lift", "PROMO SALES", new JsonArray(
                    Kpi("PROMO SALES", "<sales measure>"), Kpi("BASE SALES", "<base measure>"), Kpi("LIFT %", "<lift % measure>"),
                    Vz("clusteredColumnChart", "Promo vs base by segment", 7, 4, Dim("<segment>"), Meas("<sales measure>", "<base measure>")),
                    Vz("lineChart", "Promo trend", 5, 4, DateCol(), Meas("<sales window measure>"))))),
            ("Price Architecture", "Pricing", "grid", "Price-vs-volume scatter, a price bar and a price-ladder table.",
                () => Grid("Price Architecture", "Price and volume", "AVG PRICE", new JsonArray(
                    Vz("scatterChart", "Price vs volume", 7, 4, Dim("<item>"), Meas("<avg price measure>", "<volume measure>", "<sales measure>")),
                    Vz("clusteredBarChart", "Avg price by brand", 5, 4, Dim("<brand>"), Meas("<avg price measure>")),
                    Vz("tableEx", "Price ladder", 12, 3, Dim("<item>"), Meas("<avg price measure>", "<volume measure>", "<sales measure>"))))),
            ("Distribution & Availability", "Distribution", "grid", "Distribution bar, an availability table and a segment column.",
                () => Grid("Distribution & Availability", "On-shelf", "DISTRIBUTION", new JsonArray(
                    Vz("clusteredBarChart", "Distribution by brand", 7, 4, Dim("<brand>"), Meas("<distribution measure>")),
                    Vz("clusteredColumnChart", "By segment", 5, 4, Dim("<segment>"), Meas("<distribution measure>")),
                    Vz("tableEx", "Availability tracker", 12, 3, Dim("<brand>"), Meas("<distribution measure>", "<sales measure>"))))),
            ("Pack Size Analysis", "Pack", "grid", "Pack-size columns, a pack pie and a pack table.",
                () => Grid("Pack Size Analysis", "Pack mix", "SALES BY PACK", new JsonArray(
                    Vz("clusteredColumnChart", "Sales by pack", 7, 4, Dim("<pack size>"), Meas("<sales measure>")),
                    Vz("pieChart", "Pack share", 5, 4, Dim("<pack size>"), Meas("<sales measure>")),
                    Vz("tableEx", "Pack scorecard", 12, 3, Dim("<pack size>"), Meas("<sales measure>", "<volume measure>"))))),
            ("Segment Share", "Segment", "grid", "Segment donut, a stacked-area trend and a segment table.",
                () => Grid("Segment Share", "Share and trend", "SHARE OF CATEGORY", new JsonArray(
                    Vz("donutChart", "Segment share", 4, 4, Dim("<segment>"), Meas("<sales measure>")),
                    Vz("stackedAreaChart", "Segment trend", 8, 4, DateCol(), Meas("<sales window measure>"), Dim("<segment>")),
                    Vz("tableEx", "Segment scorecard", 12, 3, Dim("<segment>"), Meas("<sales measure>", "<growth % measure>"))))),
            ("Growth Decomposition", "Growth", "grid", "Growth columns, a YoY trend and a contributor table.",
                () => Grid("Growth Decomposition", "What is driving growth", "SALES GROWTH", new JsonArray(
                    Vz("clusteredColumnChart", "Growth by segment", 7, 4, Dim("<segment>"), Meas("<growth % measure>")),
                    Vz("lineChart", "YoY trend", 5, 4, DateCol(), Meas("<sales window measure>")),
                    Vz("tableEx", "Top contributors", 12, 3, Dim("<brand>"), Meas("<sales measure>", "<growth % measure>"))))),
            ("New Product Tracker", "Innovation", "grid", "Launch trend, a new-lines table and a column by brand.",
                () => Grid("New Product Tracker", "Innovation", "NPD SALES", new JsonArray(
                    Vz("lineChart", "Launch ramp", 8, 4, DateCol(), Meas("<sales window measure>")),
                    Vz("clusteredColumnChart", "New lines by brand", 4, 4, Dim("<brand>"), Meas("<sales measure>")),
                    Vz("tableEx", "New products", 12, 3, Dim("<item>"), Meas("<sales measure>", "<volume measure>"))))),
            ("Trend & Seasonality", "Trend", "grid", "A line, an area and a seasonal column.",
                () => Grid("Trend & Seasonality", "Over time", "SALES TREND", new JsonArray(
                    Vz("lineChart", "Sales trend", 12, 3, DateCol(), Meas("<sales window measure>")),
                    Vz("areaChart", "Volume trend", 6, 3, DateCol(), Meas("<volume measure>")),
                    Vz("clusteredColumnChart", "By month", 6, 3, Dim("<month>"), Meas("<sales measure>"))))),
            ("Velocity & Ranking", "Velocity", "grid", "Ranked bar, a velocity scatter and a ranking table.",
                () => Grid("Velocity & Ranking", "Rate of sale", "UNITS / STORE / WEEK", new JsonArray(
                    Vz("clusteredBarChart", "Top by velocity", 7, 4, Dim("<item>"), Meas("<usw measure>")),
                    Vz("scatterChart", "Velocity vs distribution", 5, 4, Dim("<item>"), Meas("<distribution measure>", "<usw measure>", "<sales measure>")),
                    Vz("tableEx", "Velocity ranking", 12, 3, Dim("<item>"), Meas("<usw measure>", "<sales measure>"))))),
            ("Margin Analysis", "Margin", "grid", "Margin columns, a margin trend and a margin table.",
                () => Grid("Margin Analysis", "Profitability", "MARGIN", new JsonArray(
                    Vz("clusteredColumnChart", "Margin by brand", 7, 4, Dim("<brand>"), Meas("<margin measure>")),
                    Vz("lineChart", "Margin trend", 5, 4, DateCol(), Meas("<margin window measure>")),
                    Vz("tableEx", "Margin scorecard", 12, 3, Dim("<brand>"), Meas("<sales measure>", "<margin measure>"))))),
            ("Shopper / Penetration", "Shopper", "grid", "Penetration columns, a frequency line and KPI cards.",
                () => Grid("Shopper / Penetration", "Who is buying", "PENETRATION", new JsonArray(
                    Kpi("PENETRATION", "<penetration measure>"), Kpi("FREQUENCY", "<frequency measure>"), Kpi("SPEND/BASKET", "<spend per basket measure>"),
                    Vz("clusteredColumnChart", "Penetration by segment", 7, 4, Dim("<segment>"), Meas("<penetration measure>")),
                    Vz("lineChart", "Frequency trend", 5, 4, DateCol(), Meas("<frequency window measure>"))))),
            ("Competitor Benchmark", "Competitor", "grid", "Clustered bar by competitor and a benchmark table.",
                () => Grid("Competitor Benchmark", "You vs the field", "RELATIVE SALES", new JsonArray(
                    Vz("clusteredBarChart", "You vs competitors", 7, 5, Dim("<brand>"), Meas("<sales measure>"), Dim("<segment>")),
                    Vz("tableEx", "Benchmark", 5, 5, Dim("<brand>"), Meas("<sales measure>", "<growth % measure>")),
                    Vz("ribbonChart", "Rank over time", 12, 3, DateCol(), Meas("<sales window measure>"), Dim("<brand>"))))),
            ("Basket Analysis", "Basket", "grid", "Basket columns, a treemap and KPI cards.",
                () => Grid("Basket Analysis", "What is in the basket", "BASKETS", new JsonArray(
                    Kpi("BASKETS", "<baskets measure>"), Kpi("UNITS/BASKET", "<units per basket measure>"), Kpi("SPEND/BASKET", "<spend per basket measure>"),
                    Vz("treemap", "Category basket", 6, 4, Dim("<segment>"), Meas("<baskets measure>")),
                    Vz("clusteredColumnChart", "Baskets by brand", 6, 4, Dim("<brand>"), Meas("<baskets measure>"))))),
            ("Channel Mix", "Channel", "grid", "Stacked column by channel, a channel donut and a table.",
                () => Grid("Channel Mix", "Where it sells", "SALES BY CHANNEL", new JsonArray(
                    Vz("stackedColumnChart", "Segment x channel", 7, 4, Dim("<channel>"), Meas("<sales measure>"), Dim("<segment>")),
                    Vz("donutChart", "Channel share", 5, 4, Dim("<channel>"), Meas("<sales measure>")),
                    Vz("tableEx", "Channel scorecard", 12, 3, Dim("<channel>"), Meas("<sales measure>", "<growth % measure>"))))),
            ("Category Scorecard", "Scorecard", "grid", "The full one-pager: KPIs, trend, share, brand bar and a table.",
                () => Grid("Category Scorecard", "Everything on one page", "CATEGORY SALES", new JsonArray(
                    Kpi("SALES", "<sales measure>"), Kpi("VOLUME", "<volume measure>"), Kpi("AVG PRICE", "<avg price measure>"), Kpi("GROWTH %", "<growth % measure>"),
                    Vz("lineChart", "Trend", 6, 3, DateCol(), Meas("<sales window measure>")),
                    Vz("donutChart", "Segment share", 3, 3, Dim("<segment>"), Meas("<sales measure>")),
                    Vz("clusteredColumnChart", "By brand", 3, 3, Dim("<brand>"), Meas("<sales measure>")),
                    Vz("tableEx", "Scorecard", 12, 3, Dim("<brand>"), Meas("<sales measure>", "<volume measure>", "<growth % measure>"))))),
        };

        var list = new List<Template>();
        int i = 1;
        foreach (var bp in blueprints)
            foreach (var th in Themes)
            {
                var config = bp.cfg();
                config["brandColor"] = th.brand;
                config["accentColor"] = th.accent;
                config["canvasWidth"] = 1280;
                config["canvasHeight"] = 720;
                config["logoPath"] = "<optional: path to brand logo png>";
                list.Add(new Template($"tpl-{i:000}", $"{bp.name} - {th.name}", bp.category, bp.desc, bp.recipe, config));
                i++;
            }
        return list;
    }

    private static JsonArray Slicers(params string[] cols)
    {
        var a = new JsonArray();
        foreach (var c in cols) a.Add(new JsonObject { ["table"] = "<dim>", ["column"] = c, ["title"] = c });
        return a;
    }

    private static JsonObject Exec(string title, string subtitle, string headlineLabel) => new()
    {
        ["title"] = title,
        ["subtitle"] = subtitle,
        ["headline"] = "$0.00bn  +0.0%",
        ["headlineLabel"] = headlineLabel,
        ["factTable"] = "<fact table>",
        ["dateTable"] = "<calendar>",
        ["dateColumn"] = "<date>",
        ["trendMeasure"] = "<sales measure>",
        ["segmentTable"] = "<dim>",
        ["segmentColumn"] = "<segment>",
        ["segmentValueMeasure"] = "<sales measure>",
        ["growthMeasure"] = "<growth % measure>",
        ["slicers"] = Slicers("<segment>", "<brand>"),
        ["kpis"] = new JsonArray(
            new JsonObject { ["measure"] = "<sales measure>", ["label"] = "SALES", ["delta"] = "<growth % measure>", ["trend"] = "<sales window measure>" },
            new JsonObject { ["measure"] = "<volume measure>", ["label"] = "VOLUME" },
            new JsonObject { ["measure"] = "<avg price measure>", ["label"] = "AVG PRICE" }),
    };

    private static JsonObject Cat(string title, string subtitle, string headlineLabel) => new()
    {
        ["title"] = title,
        ["subtitle"] = subtitle,
        ["headline"] = "$0.00bn  +0.0%",
        ["headlineLabel"] = headlineLabel,
        ["factTable"] = "<fact table>",
        ["brandTable"] = "<dim>",
        ["brandColumn"] = "<brand>",
        ["segmentTable"] = "<dim>",
        ["segmentColumn"] = "<segment>",
        ["dateTable"] = "<calendar>",
        ["dateColumn"] = "<date>",
        ["itemColumn"] = "<item description>",
        ["slicers"] = Slicers("<period>", "<brand>", "<segment>"),
        ["measures"] = new JsonObject
        {
            ["sales"] = "<sales measure>",
            ["volume"] = "<volume measure>",
            ["price"] = "<avg price measure>",
            ["growth"] = "<growth % measure>",
            ["salesTrend"] = "<sales window measure>",
            ["distribution"] = "<distribution measure (optional)>",
            ["usw"] = "<avg units/store/week (optional)>",
        },
    };

    private static JsonObject Xr(string title, string subtitle) => new()
    {
        ["title"] = title,
        ["subtitle"] = subtitle,
        ["headline"] = "$0.00m",
        ["headlineLabel"] = "TOTAL MARKET",
        ["compareTable"] = "<conformed dimension (from conform_dimension)>",
        ["compareColumn"] = "<brand>",
        ["retailerA"] = new JsonObject { ["label"] = "Retailer A", ["table"] = "<fact A>", ["measure"] = "<sales A measure>" },
        ["retailerB"] = new JsonObject { ["label"] = "Retailer B", ["table"] = "<fact B>", ["measure"] = "<sales B measure>" },
        ["totalMeasure"] = new JsonObject { ["table"] = "<fact A>", ["measure"] = "<total market measure>" },
        ["slicers"] = Slicers("<brand>"),
    };

    // ---- grid recipe helpers (the flexible composer behind the dashboard templates) ----
    private static JsonObject Grid(string title, string subtitle, string headlineLabel, JsonArray visuals) => new()
    {
        ["title"] = title,
        ["subtitle"] = subtitle,
        ["headline"] = "$0.00bn  +0.0%",
        ["headlineLabel"] = headlineLabel,
        ["slicers"] = Slicers("<period>", "<brand>", "<segment>"),
        ["visuals"] = visuals,
    };
    private static JsonObject Dim(string col) => new() { ["table"] = "<dim>", ["column"] = col };
    private static JsonObject DateCol() => new() { ["table"] = "<calendar>", ["column"] = "<date>" };
    private static JsonArray Meas(params string[] m)
    {
        var a = new JsonArray();
        foreach (var x in m) a.Add(new JsonObject { ["table"] = "<fact>", ["measure"] = x });
        return a;
    }
    private static JsonObject Vz(string type, string title, int span, int rows, JsonObject? cat, JsonArray? vals, JsonObject? ser = null)
    {
        var o = new JsonObject { ["type"] = type, ["title"] = title, ["span"] = span, ["rows"] = rows };
        if (cat != null) o["category"] = cat;
        if (ser != null) o["series"] = ser;
        if (vals != null) o["values"] = vals;
        return o;
    }
    private static JsonObject Kpi(string label, string measure) => Vz("card", label, 3, 1, null, Meas(measure));
}
