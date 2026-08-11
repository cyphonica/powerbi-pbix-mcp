using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit tests for <see cref="AdmissionPolicy"/> and the readings it decides on. Every threshold is
/// passed explicitly rather than left to the environment, so the table proves the arithmetic and not the box
/// the suite happens to run on.
///
/// The volume tests are the important half: they pin the rule that only a ready fixed disk is a reading at
/// all. A host whose D: is an empty optical drive reports 0 bytes free there, and a floor applied to that 0
/// would hold every job forever - a production stop that presents as a silent hang.
///
/// Theories carry lanes and verdicts as bool/string rather than as the internal enums: an xUnit fact must be
/// public, and a public signature cannot name an internal type.
/// </summary>
public sealed class AdmissionPolicyTests
{
    private const long TestRamFloorMB = 1024;
    private const double TestDiskFloorGB = 10.0;

    private static readonly DateTimeOffset At = new(2026, 7, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A host with both volumes comfortably above any floor under test.</summary>
    private static HostSnapshot Host(long ramFreeMB, double jobRootFreeGB = 200, double tempFreeGB = 200) =>
        new(ramFreeMB, 65536, jobRootFreeGB, tempFreeGB, At);

    private static AdmissionVerdict Evaluate(in HostSnapshot host, in AdmissionRequest req) =>
        AdmissionPolicy.Evaluate(host, req, TestRamFloorMB, TestDiskFloorGB);

    private static Lane LaneOf(bool heavy) => heavy ? Lane.Heavy : Lane.Cheap;

    // ---------------- the RAM floor ----------------

    [Theory]
    // heavy: the reserve must clear the floor out of FREE ram, not out of total
    [InlineData(5000, 4096, "HoldRam")]   // 5000-4096 = 904 < 1024
    [InlineData(5119, 4096, "HoldRam")]   // 5119-4096 = 1023, one MB under
    [InlineData(5120, 4096, "Admit")]     // 5120-4096 = 1024, exactly on the floor
    [InlineData(6000, 4096, "Admit")]     // 6000-4096 = 1904
    [InlineData(65536, 24576, "Admit")]
    [InlineData(4096, 4096, "HoldRam")]   // nothing survives the reserve
    [InlineData(0, 4096, "HoldRam")]
    public void Heavy_IsDecidedOnFreeRamMinusReserveAgainstTheFloor(
        long ramFreeMB, long reserveMB, string expected)
    {
        var req = new AdmissionRequest(Lane.Heavy, reserveMB, LaneActive: 0, LaneLimit: 1);
        Assert.Equal(expected, Evaluate(Host(ramFreeMB), req).ToString());
    }

    [Fact]
    public void Heavy_IsNeverAdmittedWhenFreeRamIsUnknown()
    {
        // An unknown headroom is never evidence of a headroom: off Windows there is no reading to clear the floor with.
        var req = new AdmissionRequest(Lane.Heavy, ReserveMB: 4096, LaneActive: 0, LaneLimit: 4);
        Assert.Equal(AdmissionVerdict.HoldRam, Evaluate(Host(ramFreeMB: -1), req));
    }

    // ---------------- lane capacity ----------------

    [Fact]
    public void Heavy_IsHeldWhenTheLaneIsFull_EvenWithSixtyFourGigFree()
    {
        // Free RAM never buys a second Desktop: the lane limit is the last word.
        var req = new AdmissionRequest(Lane.Heavy, ReserveMB: 4096, LaneActive: 1, LaneLimit: 1);
        Assert.Equal(AdmissionVerdict.HoldLane, Evaluate(Host(ramFreeMB: 65536), req));
    }

    [Fact]
    public void Cheap_IsHeldOnlyWhenTheCheapLaneIsFull()
    {
        var host = Host(ramFreeMB: 65536);
        Assert.Equal(AdmissionVerdict.HoldLane,
            Evaluate(host, new AdmissionRequest(Lane.Cheap, 0, LaneActive: 4, LaneLimit: 4)));
        Assert.Equal(AdmissionVerdict.Admit,
            Evaluate(host, new AdmissionRequest(Lane.Cheap, 0, LaneActive: 3, LaneLimit: 4)));
    }

    [Theory]
    [InlineData(1, 1)]     // the heavy lane is full
    [InlineData(4, 1)]     // and then some
    public void Cheap_IsNeverBlockedByTheHeavyLane(int heavyActive, int heavyLimit)
    {
        // The cheap lane carries its own counters. A saturated heavy lane is not an input to a cheap verdict:
        // a PBIP zip costs nothing and must keep flowing while a refresh holds the Desktop.
        var host = Host(ramFreeMB: 65536);
        Assert.Equal(AdmissionVerdict.HoldLane,
            Evaluate(host, new AdmissionRequest(Lane.Heavy, 4096, heavyActive, heavyLimit)));
        Assert.Equal(AdmissionVerdict.Admit,
            Evaluate(host, new AdmissionRequest(Lane.Cheap, 0, LaneActive: 0, LaneLimit: 4)));
    }

    [Theory]
    [InlineData(-1)]    // unknown
    [InlineData(0)]
    [InlineData(64)]    // below any heavy reserve
    public void Cheap_IsNeverBlockedByTheRamFloor(long ramFreeMB)
    {
        // The RAM floor guards the Desktop launch. A cheap job never launches one, so it never pays the floor.
        var req = new AdmissionRequest(Lane.Cheap, ReserveMB: 0, LaneActive: 0, LaneLimit: 4);
        Assert.Equal(AdmissionVerdict.Admit, Evaluate(Host(ramFreeMB), req));
    }

    // ---------------- the disk floor ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABreachedJobRootVolume_HoldsBothLanes(bool heavy)
    {
        // A full volume kills headless ZIP work just as dead as a refresh, so the disk floor outranks the lane.
        var host = Host(ramFreeMB: 65536, jobRootFreeGB: 5);
        var req = new AdmissionRequest(LaneOf(heavy), ReserveMB: 4096, LaneActive: 0, LaneLimit: 4);
        Assert.Equal(AdmissionVerdict.HoldDisk, Evaluate(host, req));
    }

    [Fact]
    public void ABreachedTempVolume_HoldsToo()
    {
        var host = Host(ramFreeMB: 65536, jobRootFreeGB: 200, tempFreeGB: 2);
        var req = new AdmissionRequest(Lane.Cheap, 0, LaneActive: 0, LaneLimit: 4);
        Assert.Equal(AdmissionVerdict.HoldDisk, Evaluate(host, req));
    }

    [Fact]
    public void TheDiskFloorIsInclusive_AtTheFloorAdmits()
    {
        var req = new AdmissionRequest(Lane.Cheap, 0, LaneActive: 0, LaneLimit: 4);
        Assert.Equal(AdmissionVerdict.Admit, Evaluate(Host(65536, jobRootFreeGB: 10), req));
        Assert.Equal(AdmissionVerdict.HoldDisk, Evaluate(Host(65536, jobRootFreeGB: 9.9), req));
    }

    // ---------------- an unmeasurable volume is not a full volume ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnmeasurableVolume_NeverHoldsDisk(bool heavy)
    {
        // -1 is "unknown", never "full". A host that could measure nothing at all must still admit.
        var host = new HostSnapshot(ramFreeMB: 65536, ramTotalMB: 65536, jobRootFreeGB: -1, tempFreeGB: -1, At);
        Assert.Empty(host.Volumes!);

        var req = new AdmissionRequest(LaneOf(heavy), ReserveMB: 4096, LaneActive: 0, LaneLimit: 4);
        Assert.Equal(AdmissionVerdict.Admit, Evaluate(host, req));
    }

    [Fact]
    public void AZeroByteOpticalDriveReading_IsDroppedBeforeItEverReachesAFloor()
    {
        // The box this runs on has one fixed volume and a D: that is an EMPTY OPTICAL DRIVE reporting 0 bytes
        // free. HostResources reads that as -1 ("unknown"), so it is never in the snapshot, so no floor can
        // breach on it. Floored as though 0 were a measurement, it would hold every job forever.
        var host = new HostSnapshot(ramFreeMB: 65536, ramTotalMB: 65536,
            jobRootFreeGB: 47.1,   // the only fixed volume, healthy
            tempFreeGB: -1,        // the optical drive: measured as unknown, not as zero
            At);

        Assert.Single(host.Volumes!);
        Assert.DoesNotContain(host.Volumes!, v => v.FreeGB == 0);
        Assert.Equal(AdmissionVerdict.Admit,
            Evaluate(host, new AdmissionRequest(Lane.Heavy, 4096, LaneActive: 0, LaneLimit: 1)));
    }

    [Fact]
    public void HostResources_ReadsANonFixedOrNotReadyVolumeAsUnknown_NeverAsZero()
    {
        // The guarantee at its source: whatever this host is, only a ready fixed disk yields a reading, and
        // every other volume yields -1. On a box with a CD-ROM this is the assertion that keeps admission open.
        foreach (var drive in DriveInfo.GetDrives())
        {
            double free = HostResources.FreeGB(drive.Name);
            bool measurable = drive.IsReady && drive.DriveType == DriveType.Fixed;

            if (measurable)
                Assert.True(free >= 0, $"{drive.Name} is a ready fixed disk and must read as a measurement");
            else
                Assert.True(free < 0,
                    $"{drive.Name} is {drive.DriveType}/IsReady={drive.IsReady} and must read as unknown, not as {free}GB free");
        }
    }

    [Theory]
    [InlineData("\\\\no-such-host\\share\\jobs")]   // UNC: not a local volume
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0")]                              // malformed: GetFullPath throws on it
    public void HostResources_ReadsAnUnmeasurablePathAsUnknown_AndNeverThrows(string path)
    {
        // A relative path is deliberately NOT in this table: it resolves against the current directory's
        // volume, which is a real measurement.
        Assert.True(HostResources.FreeGB(path) < 0);
    }

    [SkippableFact]
    public void HostResources_ReadsAnAbsentDriveLetterAsUnknown()
    {
        string? absent = FirstAbsentDriveLetter();
        Skip.If(absent is null, "every drive letter is mounted on this host.");

        Assert.True(HostResources.FreeGB(absent!) < 0,
            $"{absent} is not mounted and must read as unknown, not as zero free");
    }

    [SkippableFact]
    public void Probe_OverAJobRootOnAnAbsentVolume_DropsItAndNeverHoldsDisk()
    {
        // The end-to-end shape of the failure this guards: point the job root at a volume that cannot be
        // measured and admission must carry on off the volumes that CAN be, not stop dead.
        string? absent = FirstAbsentDriveLetter();
        Skip.If(absent is null, "every drive letter is mounted on this host.");

        var host = HostResources.Probe(Path.Combine(absent!, "daxops", "jobs"));

        Assert.DoesNotContain(host.Volumes!,
            v => v.Root.StartsWith(absent!, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(AdmissionVerdict.HoldDisk, AdmissionPolicy.Evaluate(host,
            new AdmissionRequest(Lane.Cheap, 0, LaneActive: 0, LaneLimit: 4), TestRamFloorMB, diskFloorGB: 0.001));
    }

    // ---------------- ordering ----------------

    [Fact]
    public void TheDiskFloorOutranksRamAndLane()
    {
        // Disk first, then RAM headroom, then lane capacity. The order is what the operator's hold message
        // reports, so it has to be the order the verdict was actually decided in.
        var starved = new HostSnapshot(ramFreeMB: 0, ramTotalMB: 65536, jobRootFreeGB: 1, tempFreeGB: 1, At);
        var req = new AdmissionRequest(Lane.Heavy, ReserveMB: 4096, LaneActive: 9, LaneLimit: 1);
        Assert.Equal(AdmissionVerdict.HoldDisk, Evaluate(starved, req));
    }

    [Fact]
    public void TheRamFloorOutranksTheLane()
    {
        var req = new AdmissionRequest(Lane.Heavy, ReserveMB: 4096, LaneActive: 9, LaneLimit: 1);
        Assert.Equal(AdmissionVerdict.HoldRam, Evaluate(Host(ramFreeMB: 4096), req));
    }

    // ---------------- the reserve ----------------

    [Theory]
    [InlineData(0L, 4096L)]                           // no source: the base stands
    [InlineData(-1L, 4096L)]                          // a nonsense reading: the base stands
    [InlineData(500L * 1024 * 1024, 6096L)]           // 4096 + 4 x 500MB
    [InlineData(1024L * 1024 * 1024, 8192L)]          // 4096 + 4 x 1024MB
    [InlineData(100L * 1024 * 1024 * 1024, 24576L)]   // 100GB: clamped to the cap
    [InlineData(long.MaxValue, 24576L)]               // a wrapped multiply would go negative; it clamps instead
    public void ReserveForModel_ScalesOnSourceSizeAndClampsToTheCap(long sourceBytes, long expectedMB)
    {
        Assert.Equal(expectedMB, AdmissionPolicy.ReserveForModel(sourceBytes));
    }

    [Fact]
    public void ReserveForModel_NeverReturnsLessThanTheBase_EvenWithACapBelowIt()
    {
        // A cap below the base would make the base unreachable; the base always wins.
        Assert.Equal(4096, AdmissionPolicy.ReserveForModel(0, baseMB: 4096, capMB: 100));
        Assert.Equal(4096, AdmissionPolicy.ReserveForModel(50L * 1024 * 1024 * 1024, baseMB: 4096, capMB: 100));
    }

    [Fact]
    public void ReserveForModel_IsNeverNegative()
    {
        foreach (long bytes in new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue })
            Assert.True(AdmissionPolicy.ReserveForModel(bytes) >= 0);
    }

    // ---------------- the operator's reason ----------------

    [Fact]
    public void Explain_NamesTheVolumeAndTheNumbersAHoldWasDecidedOn()
    {
        var host = Host(ramFreeMB: 65536, jobRootFreeGB: 5);
        var req = new AdmissionRequest(Lane.Heavy, 4096, LaneActive: 0, LaneLimit: 1);

        string reason = AdmissionPolicy.Explain(AdmissionVerdict.HoldDisk, host, req);
        Assert.Contains("job root volume", reason);
        Assert.Contains("5", reason);
    }

    [Fact]
    public void Explain_SaysSoWhenRamIsUnknownRatherThanQuotingMinusOne()
    {
        var req = new AdmissionRequest(Lane.Heavy, 4096, LaneActive: 0, LaneLimit: 1);
        string reason = AdmissionPolicy.Explain(AdmissionVerdict.HoldRam, Host(ramFreeMB: -1), req);

        Assert.Contains("unknown", reason);
        Assert.DoesNotContain("-1MB", reason);
    }

    [Fact]
    public void Explain_ReportsTheLaneCounters()
    {
        var req = new AdmissionRequest(Lane.Heavy, 4096, LaneActive: 1, LaneLimit: 1);
        string reason = AdmissionPolicy.Explain(AdmissionVerdict.HoldLane, Host(65536), req);

        Assert.Contains("heavy", reason);
        Assert.Contains("1/1", reason);
    }

    /// <summary>A drive letter this host has nothing mounted on, or null when they are all in use.</summary>
    private static string? FirstAbsentDriveLetter()
    {
        var mounted = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        foreach (char c in "QRSTUVWXYZ")
            if (!mounted.Contains(c)) return c + ":\\";
        return null;
    }
}
