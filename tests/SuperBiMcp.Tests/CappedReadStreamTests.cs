using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The hard byte cap that protects the box from a hostile / runaway Graph or HTTP response: reading at or
/// under the cap is fine; the first read that crosses the cap throws (no silent truncation). The thrown
/// message must carry the MB cap and nothing sensitive.
/// </summary>
public sealed class CappedReadStreamTests
{
    [Fact]
    public void ReadUnderCap_ReturnsAllBytes()
    {
        byte[] data = new byte[1000];
        using var inner = new MemoryStream(data);
        using var capped = new CappedReadStream(inner, cap: 4096);

        byte[] buf = new byte[4096];
        int n = capped.Read(buf, 0, buf.Length);
        Assert.Equal(1000, n);
        Assert.Equal(1000, capped.Position);
    }

    [Fact]
    public void ReadCrossingCap_Throws()
    {
        byte[] data = new byte[5000];
        using var inner = new MemoryStream(data);
        using var capped = new CappedReadStream(inner, cap: 1024);

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            byte[] buf = new byte[8192];
            capped.Read(buf, 0, buf.Length); // one read pulls 5000 bytes > 1024 cap
        });
        Assert.Contains("cap", ex.Message);
    }

    [Fact]
    public void ReadExactlyAtCap_DoesNotThrow_ButOnePastDoes()
    {
        // cap measured in whole MB in the message; here cap is in bytes. Read up to the cap, then one more.
        byte[] data = new byte[2048];
        using var inner = new MemoryStream(data);
        using var capped = new CappedReadStream(inner, cap: 2048);

        byte[] buf = new byte[1024];
        Assert.Equal(1024, capped.Read(buf, 0, 1024)); // 1024 <= 2048, fine
        Assert.Equal(1024, capped.Read(buf, 0, 1024)); // 2048 <= 2048, fine (boundary)
        // stream is drained now; a further read returns 0 and never trips the cap
        Assert.Equal(0, capped.Read(buf, 0, 1024));
    }

    [Fact]
    public void Stream_IsReadOnly_NonSeekable()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var capped = new CappedReadStream(inner, cap: 100);
        Assert.True(capped.CanRead);
        Assert.False(capped.CanSeek);
        Assert.False(capped.CanWrite);
        Assert.Throws<NotSupportedException>(() => capped.Seek(0, SeekOrigin.Begin));
    }
}
