using System.Security.Cryptography;

namespace SuperBiMcp.Jobs;

/// <summary>
/// Crockford base32, uppercase, 26 chars: 10 chars of 48-bit ms timestamp + 16
/// chars of 80-bit randomness. Ids minted by one instance are strictly ordinal-
/// increasing, which is what lets job ids stand in for queue position, "oldest
/// first" reaping order and retained-artifact order without a separate sequence.
/// </summary>
internal sealed class UlidMinter
{
    internal const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int TimeChars = 10;
    private const int RandChars = 16;
    private const int RandBytes = 10;   // 80 bits, exactly RandChars * 5

    private readonly Func<long> _unixMsClock;
    private readonly Action<byte[]> _fillRandom;
    private readonly object _gate = new();
    private readonly byte[] _random = new byte[RandBytes];

    // Below any real clock reading, so the first Next() always draws fresh randomness.
    private long _lastMs = long.MinValue;

    internal UlidMinter(Func<long> unixMsClock, Action<byte[]> fillRandom)
    {
        _unixMsClock = unixMsClock ?? throw new ArgumentNullException(nameof(unixMsClock));
        _fillRandom = fillRandom ?? throw new ArgumentNullException(nameof(fillRandom));
    }

    /// <summary>Mint the next id. Thread-safe, and monotonic within a shared millisecond.</summary>
    internal string Next()
    {
        lock (_gate)
        {
            long ms = _unixMsClock();
            if (ms > _lastMs)
            {
                _lastMs = ms;
                _fillRandom(_random);
            }
            else
            {
                // Same millisecond, or the clock stepped back: hold the last timestamp and carry the
                // 80-bit random component up by one. A step back must never re-issue a lower id, so
                // the reading is discarded rather than used. On overflow the only way up is the
                // timestamp, which outranks any randomness and so needs a fresh draw, not a carry.
                if (!Increment(_random))
                {
                    _lastMs++;
                    _fillRandom(_random);
                }
            }

            return Encode(_lastMs, _random);
        }
    }

    /// <summary>Adds one to a big-endian integer in place. False on overflow (all bytes were 0xFF).</summary>
    private static bool Increment(byte[] b)
    {
        for (int i = b.Length - 1; i >= 0; i--)
            if (++b[i] != 0) return true;
        return false;
    }

    private static string Encode(long unixMs, byte[] random)
    {
        return string.Create(TimeChars + RandChars, (unixMs, random), static (dst, state) =>
        {
            var (ms, rnd) = state;
            for (int i = 0; i < TimeChars; i++)
                dst[i] = Crockford[(int)((ms >>> (5 * (TimeChars - 1 - i))) & 31)];
            for (int i = 0; i < RandChars; i++)
                dst[TimeChars + i] = Crockford[FiveBitsAt(rnd, i * 5)];
        });
    }

    /// <summary>Reads the 5 bits starting at <paramref name="bitOffset"/> of a big-endian bit stream.</summary>
    private static int FiveBitsAt(byte[] b, int bitOffset)
    {
        int index = bitOffset >> 3;
        int shift = bitOffset & 7;
        int window = b[index] << 8 | (index + 1 < b.Length ? b[index + 1] : 0);
        return (window >>> (11 - shift)) & 31;
    }
}

internal static class JobId
{
    internal const int Length = 26;

    private const int TimeChars = 10;

    private static readonly UlidMinter Shared = new(
        () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        buf => RandomNumberGenerator.Fill(buf));

    // Reverse of UlidMinter.Crockford. Uppercase only: directory names compare OrdinalIgnoreCase
    // while JsonObject index keys compare Ordinal, so accepting both cases would let one directory
    // carry two queue rows. Unmapped codes stay -1, which is what makes IsValid the strict guard.
    private static readonly sbyte[] Decode = BuildDecode();

    private static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private static sbyte[] BuildDecode()
    {
        var map = new sbyte[128];
        Array.Fill(map, (sbyte)-1);
        for (int i = 0; i < UlidMinter.Crockford.Length; i++)
            map[UlidMinter.Crockford[i]] = (sbyte)i;
        return map;
    }

    /// <summary>Mint a job id from the process-wide monotonic minter.</summary>
    internal static string New() => Shared.Next();

    /// <summary>
    /// True only for a well-formed id. Every jobId-derived path is gated on this, so it is the guard
    /// that keeps a hostile or malformed id inside the job root: a fixed alphabet at a fixed length
    /// admits no separator, no drive letter and no "..".
    /// </summary>
    internal static bool IsValid(string? s)
    {
        if (s is null || s.Length != Length) return false;
        foreach (char c in s)
            if (c >= 128 || Decode[c] < 0) return false;
        return true;
    }

    /// <summary>The instant the id was minted, to the millisecond.</summary>
    internal static DateTimeOffset TimestampOf(string ulid)
    {
        if (!IsValid(ulid)) throw new ArgumentException($"Not a job id: '{ulid}'.", nameof(ulid));

        long ms = 0;
        for (int i = 0; i < TimeChars; i++)
            ms = ms << 5 | (byte)Decode[ulid[i]];

        // 10 Crockford chars carry 50 bits where a ULID timestamp is 48, so a syntactically valid id
        // can still name an instant no DateTimeOffset holds.
        if (ms > MaxUnixMs) throw new ArgumentException($"Job id '{ulid}' carries an out-of-range timestamp.", nameof(ulid));
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    /// <summary>An isolated minter with an injected clock and randomness. Never shares state with <see cref="New"/>.</summary>
    internal static UlidMinter MinterForTest(Func<long> clock, Action<byte[]> fillRandom) => new(clock, fillRandom);
}
