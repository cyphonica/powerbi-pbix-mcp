using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Order-date selection for the Shopify connector. The bug this pins: keying the order date off created_at
/// stamps a store migrated into Shopify with the IMPORT date, collapsing the whole sales history onto one day.
/// The connector prefers processed_at (Shopify Analytics' transaction date) and only falls back to created_at.
/// </summary>
public sealed class ShopifyConnectorTests
{
    private static JsonObject Order(string? processedAt, string? createdAt)
    {
        var o = new JsonObject();
        if (processedAt is not null) o["processed_at"] = processedAt;
        if (createdAt is not null) o["created_at"] = createdAt;
        return o;
    }

    [Fact]
    public void OrderDate_PrefersProcessedAt_OverCreatedAt()
    {
        // The migration case: created_at is today's import, processed_at is the real sale date months earlier.
        var o = Order(processedAt: "2026-04-03T10:15:00-04:00", createdAt: "2026-07-16T23:31:00-04:00");
        Assert.Equal(new DateTime(2026, 4, 3), ShopifyConnector.OrderDate(o));
    }

    [Fact]
    public void OrderDate_FallsBackToCreatedAt_WhenProcessedAtAbsent()
    {
        var o = Order(processedAt: null, createdAt: "2026-05-20T09:30:00-04:00");
        Assert.Equal(new DateTime(2026, 5, 20), ShopifyConnector.OrderDate(o));
    }

    [Fact]
    public void OrderDate_FallsBackToCreatedAt_WhenProcessedAtIsEmpty()
    {
        var o = Order(processedAt: "", createdAt: "2026-05-20T09:30:00-04:00");
        Assert.Equal(new DateTime(2026, 5, 20), ShopifyConnector.OrderDate(o));
    }

    [Fact]
    public void OrderDate_IsUtcDate_RegardlessOfSourceOffset()
    {
        // A late-evening local time on the US east coast is the next day in UTC; the connector snaps to UTC date.
        var o = Order(processedAt: "2026-04-03T22:30:00-04:00", createdAt: null);
        Assert.Equal(new DateTime(2026, 4, 4), ShopifyConnector.OrderDate(o));
    }

    [Fact]
    public void OrderDate_NeverThrows_WhenBothDatesMissing()
    {
        // No date at all: falls back to today rather than throwing mid-pull.
        var date = ShopifyConnector.OrderDate(new JsonObject());
        Assert.Equal(DateTime.UtcNow.Date, date);
    }
}
