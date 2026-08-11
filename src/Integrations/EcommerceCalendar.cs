namespace SuperBiMcp.Integrations;

/// <summary>
/// Shared e-commerce shaping helpers reused by the WooCommerce and Shopify connectors (both emit the SAME
/// canonical files matching <c>solutions/ecommerce/schema.json</c>: orders.csv, order_lines.csv, products.csv,
/// customers.csv, calendar.csv). The schema-builder methods here are the single source of truth for the
/// column names and types those two connectors register, and <see cref="BuildMonthSpan"/> is the shared
/// month-end calendar span both generate.
/// </summary>
internal static class EcommerceCalendar
{
    /// <summary>The contiguous list of month-ends from the earliest to the latest order month. Falls back to
    /// the last 24 months ending this month when no orders were dated.</summary>
    public static List<DateTime> BuildMonthSpan(SortedSet<DateTime> monthEnds)
    {
        var list = new List<DateTime>();
        DateTime first, last;
        if (monthEnds.Count == 0)
        {
            last = MonthEnd(DateTime.UtcNow.Date);
            first = MonthEnd(last.AddMonths(-23));
        }
        else
        {
            first = monthEnds.Min;
            last = monthEnds.Max;
        }

        var cursor = new DateTime(first.Year, first.Month, 1);
        var end = new DateTime(last.Year, last.Month, 1);
        while (cursor <= end)
        {
            list.Add(MonthEnd(cursor));
            cursor = cursor.AddMonths(1);
        }
        return list;
    }

    private static DateTime MonthEnd(DateTime d)
        => new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    // ---- schema (matches solutions/ecommerce/schema.json exactly) ------------------------------

    public static TableSchema OrdersSchema()
    {
        var t = new TableSchema("orders");
        t.Columns.Add(new ColumnSchema("OrderKey", "int64"));
        t.Columns.Add(new ColumnSchema("CustomerKey", "int64"));
        t.Columns.Add(new ColumnSchema("OrderDate", "date"));
        t.Columns.Add(new ColumnSchema("Order Total", "double"));
        t.Columns.Add(new ColumnSchema("Order Refund", "double"));
        t.Columns.Add(new ColumnSchema("Customer Type", "string"));
        return t;
    }

    public static TableSchema OrderLinesSchema()
    {
        var t = new TableSchema("order_lines");
        t.Columns.Add(new ColumnSchema("OrderKey", "int64"));
        t.Columns.Add(new ColumnSchema("ProductKey", "int64"));
        t.Columns.Add(new ColumnSchema("CustomerKey", "int64"));
        t.Columns.Add(new ColumnSchema("OrderDate", "date"));
        t.Columns.Add(new ColumnSchema("Line Sales", "double"));
        t.Columns.Add(new ColumnSchema("Line Units", "int64"));
        t.Columns.Add(new ColumnSchema("Line Refund", "double"));
        return t;
    }

    public static TableSchema ProductsSchema()
    {
        var t = new TableSchema("products");
        t.Columns.Add(new ColumnSchema("ProductKey", "int64"));
        t.Columns.Add(new ColumnSchema("Product Name", "string"));
        t.Columns.Add(new ColumnSchema("Category", "string"));
        return t;
    }

    /// <summary>The DISTINCT product-category dimension (category.csv). One row per category - it is the
    /// one-side of Product[Category] -&gt; Category[Category] in the e-commerce model, so it MUST be row-distinct
    /// (the AS bake materialises it row-for-row; a duplicate breaks the relationship's one-side uniqueness).</summary>
    public static TableSchema CategorySchema()
    {
        var t = new TableSchema("category");
        t.Columns.Add(new ColumnSchema("Category", "string"));
        return t;
    }

    /// <summary>The DISTINCT acquisition-channel dimension (channel.csv). One row per channel - the one-side of
    /// Customer[Channel] -&gt; Channel[Channel]; must be row-distinct for the same reason as the category dim.</summary>
    public static TableSchema ChannelSchema()
    {
        var t = new TableSchema("channel");
        t.Columns.Add(new ColumnSchema("Channel", "string"));
        return t;
    }

    public static TableSchema CustomersSchema()
    {
        var t = new TableSchema("customers");
        t.Columns.Add(new ColumnSchema("CustomerKey", "int64"));
        t.Columns.Add(new ColumnSchema("Customer Name", "string"));
        t.Columns.Add(new ColumnSchema("Channel", "string"));
        t.Columns.Add(new ColumnSchema("Acquisition Date", "date"));
        return t;
    }

    public static TableSchema CalendarSchema()
    {
        var t = new TableSchema("calendar");
        t.Columns.Add(new ColumnSchema("OrderDate", "date"));
        t.Columns.Add(new ColumnSchema("Period", "string"));
        t.Columns.Add(new ColumnSchema("Month Name", "string"));
        t.Columns.Add(new ColumnSchema("Year", "int64"));
        return t;
    }
}
