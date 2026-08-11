using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Exercises the pure, offline parts of the shared Graph helper: the relative-path -> absolute-URL builder
/// (and @odata.nextLink passthrough), and the Str() flattener that turns a JSON cell (string / number /
/// bool / nested object) into a single CSV cell. The value-matrix -> rows shaping is covered end to end by
/// <see cref="GraphValueMatrixTests"/> against the real RowToStrings/CsvSink path.
/// </summary>
public sealed class GraphClientTests
{
    // ---- Url() : relative path -> absolute, absolute passthrough -------------------------------

    [Fact]
    public void Url_RelativePathWithLeadingSlash_IsAppendedToBase()
        => Assert.Equal(GraphClient.Base + "/sites/abc/lists", GraphClient.Url("/sites/abc/lists"));

    [Fact]
    public void Url_RelativePathWithoutLeadingSlash_GetsSlashInserted()
        => Assert.Equal(GraphClient.Base + "/sites/abc", GraphClient.Url("sites/abc"));

    [Fact]
    public void Url_AbsoluteHttpsUrl_IsReturnedVerbatim()
    {
        // an @odata.nextLink is an absolute Graph URL with a skiptoken; it must NOT be re-based.
        string next = "https://graph.microsoft.com/v1.0/sites/abc/lists/x/items?$skiptoken=ABC123";
        Assert.Equal(next, GraphClient.Url(next));
    }

    [Fact]
    public void Url_AbsoluteHttpUrl_IsReturnedVerbatim()
    {
        string u = "http://example.test/whatever";
        Assert.Equal(u, GraphClient.Url(u));
    }

    [Fact]
    public void Url_IsCaseInsensitiveOnScheme()
    {
        string u = "HTTPS://graph.microsoft.com/v1.0/x";
        Assert.Equal(u, GraphClient.Url(u));
    }

    // ---- Str() : JSON cell -> single CSV cell --------------------------------------------------

    [Fact]
    public void Str_NullNode_IsEmpty()
        => Assert.Equal("", GraphClient.Str(null));

    [Fact]
    public void Str_StringValue_IsRawText_NoQuotes()
        => Assert.Equal("Auckland", GraphClient.Str(JsonValue.Create("Auckland")));

    [Fact]
    public void Str_Number_IsLiteralText()
        => Assert.Equal("42", GraphClient.Str(JsonValue.Create(42)));

    [Fact]
    public void Str_Decimal_IsLiteralText()
        => Assert.Equal("1250.5", GraphClient.Str(JsonValue.Create(1250.5)));

    [Fact]
    public void Str_Bool_IsLiteralText()
        => Assert.Equal("true", GraphClient.Str(JsonValue.Create(true)));

    [Fact]
    public void Str_NestedObject_FlattensToCompactJson_NotDropped()
    {
        // a Lists field holding a structured value (e.g. a person/lookup) must flatten to one cell.
        var node = new JsonObject { ["LookupId"] = 7, ["LookupValue"] = "Jane" };
        string s = GraphClient.Str(node);
        Assert.Contains("\"LookupId\":7", s);
        Assert.Contains("\"LookupValue\":\"Jane\"", s);
        Assert.DoesNotContain("\n", s); // compact, single-cell
    }

    [Fact]
    public void Str_NestedArray_FlattensToCompactJson()
    {
        var node = new JsonArray { "a", "b", "c" };
        string s = GraphClient.Str(node);
        Assert.Equal("[\"a\",\"b\",\"c\"]", s);
    }
}
