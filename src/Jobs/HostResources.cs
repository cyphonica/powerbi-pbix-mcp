using System.Runtime.InteropServices;

namespace SuperBiMcp.Jobs;

/// <summary>Free space on one measurable volume. Root is the volume root ("C:\") or, for a synthetic reading,
/// the label the operator should see in a hold message.</summary>
internal readonly record struct VolumeFree(string Root, double FreeGB);

/// <summary>
/// One reading of the host's free RAM and free disk, taken at AtUtc.
///
/// A negative reading means "unknown", never "full", and no floor may be applied to it: RamFreeMB == -1 is a
/// host whose RAM cannot be read (non-Windows), and callers must treat it as "do not admit heavy" - an unknown
/// headroom is never evidence of a headroom.
///
/// Volumes carries ONLY volumes that were measured: a ready fixed disk. A volume that is absent, not ready or
/// not a fixed disk is simply not in the list, so it can never breach a floor. That is load-bearing, not
/// tidiness - the host these run on has one fixed volume (C:) and a D: that is an empty optical drive
/// reporting 0 bytes free, and a floor applied to that 0 would hold every job forever.
/// </summary>
internal readonly record struct HostSnapshot(
    long RamFreeMB, long RamTotalMB, IReadOnlyList<VolumeFree>? Volumes, DateTimeOffset AtUtc)
{
    /// <summary>Two anonymous volume readings, in the order the job root and the temp volume are probed. A
    /// negative reading is unknown and is dropped rather than floored.</summary>
    internal HostSnapshot(long ramFreeMB, long ramTotalMB, double jobRootFreeGB, double tempFreeGB, DateTimeOffset atUtc)
        : this(ramFreeMB, ramTotalMB, Label(jobRootFreeGB, tempFreeGB), atUtc) { }

    /// <summary>Free space on the volume hosting the job root; -1 when it was not measured.</summary>
    internal double JobRootFreeGB => At(0);

    /// <summary>Free space on the volume hosting the temp directory; -1 when it shares the job root's volume
    /// (one volume is one reading) or was not measured.</summary>
    internal double TempFreeGB => At(1);

    /// <summary>The job root volume's reading, under the name the log and health payloads emit.</summary>
    internal double CFreeGB => At(0);

    /// <summary>The temp volume's reading, under the name the log and health payloads emit.</summary>
    internal double DFreeGB => At(1);

    private double At(int i)
    {
        var v = Volumes;
        return v is not null && i < v.Count ? v[i].FreeGB : -1;
    }

    private static IReadOnlyList<VolumeFree> Label(double jobRootFreeGB, double tempFreeGB)
    {
        var list = new List<VolumeFree>(2);
        if (jobRootFreeGB >= 0) list.Add(new VolumeFree("the job root volume", jobRootFreeGB));
        if (tempFreeGB >= 0) list.Add(new VolumeFree("the temp volume", tempFreeGB));
        return list;
    }
}

/// <summary>
/// The only place in the codebase that reads free RAM and free disk.
///
/// Disk is read BY VOLUME, resolved from a path - never from a hard-coded drive letter, because a letter is a
/// reading of whatever happens to be mounted there. Only a ready <see cref="DriveType.Fixed"/> volume is a
/// reading at all: a CD-ROM reports 0 bytes free and an unmapped or network path throws, and either one read
/// as "no space" would breach a floor and hold every job forever - a production stop that presents as a silent
/// hang. Anything else reads -1 ("unknown"), matching the RAM convention, and is dropped from the snapshot.
///
/// Seams:
///   ProbeForTest    - replaces the whole reading, so admission is testable with no real host at all
///   JobRootProvider - supplies the job root path, so the disk floor tracks the real job root volume without
///                     this class taking a dependency on the job path layout
/// </summary>
internal static class HostResources
{
    /// <summary>Test seam; null = real probe.</summary>
    internal static Func<HostSnapshot>? ProbeForTest { get; set; }

    /// <summary>
    /// Supplies the job root path, set by the job subsystem once it knows the resolved root. Unset (or
    /// throwing) leaves the temp volume the only volume probed - on a single-volume host that is the same
    /// volume, so the floor still holds.
    /// </summary>
    internal static Func<string>? JobRootProvider { get; set; }

    /// <summary>
    /// Reads the host now. <paramref name="jobRootPath"/> wins over <see cref="JobRootProvider"/>; with
    /// neither set the temp directory stands in. The job root and temp volumes are de-duplicated by volume
    /// root, so one volume is one reading and is not floored twice.
    /// </summary>
    internal static HostSnapshot Probe(string? jobRootPath = null)
    {
        var forTest = ProbeForTest;
        if (forTest is not null) return forTest();

        string temp = TempPath();
        string? root = jobRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            try { root = JobRootProvider?.Invoke(); }
            catch { root = null; }   // a provider that throws must not take the whole reading down with it
        }
        if (string.IsNullOrWhiteSpace(root)) root = temp;

        var (freeMB, totalMB) = PhysicalMemory();
        return new HostSnapshot(freeMB, totalMB, MeasureVolumes(root, temp), DateTimeOffset.UtcNow);
    }

    /// <summary>The measurable volumes behind the given paths, in order, de-duplicated by volume root.</summary>
    private static IReadOnlyList<VolumeFree> MeasureVolumes(params string?[] paths)
    {
        var list = new List<VolumeFree>(paths.Length);
        foreach (string? path in paths)
        {
            string? root = VolumeRoot(path);
            if (root is null) continue;
            if (list.Any(v => string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase))) continue;

            double free = FreeGB(root);
            if (free < 0) continue;   // not a ready fixed disk: it constrains nothing, so it is not a reading
            list.Add(new VolumeFree(root, free));
        }
        return list;
    }

    /// <summary>
    /// Free GB on the volume hosting <paramref name="path"/> (a drive root, or any path on it), rounded to
    /// 0.1GB. -1 when that volume is not a ready fixed disk, or when the path names no volume this host can
    /// measure (UNC, unmapped, malformed): unknown, never "full". Never throws.
    /// </summary>
    internal static double FreeGB(string path)
    {
        string? root = VolumeRoot(path);
        if (root is null) return -1;
        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return -1;
            return Math.Round(drive.AvailableFreeSpace / 1024d / 1024d / 1024d, 1);
        }
        catch { return -1; }   // absent, unmapped, UNC: unknown, and an unknown never holds a job
    }

    /// <summary>The volume root of a path, or null when the path names none this host can measure.</summary>
    private static string? VolumeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            return root.Length == 0 ? null : root;
        }
        catch { return null; }
    }

    private static string TempPath()
    {
        try { return Path.GetTempPath(); }
        catch { return AppContext.BaseDirectory; }
    }

    /// <summary>Available and total physical RAM in MB; (-1, -1) where it cannot be read.</summary>
    private static (long freeMB, long totalMB) PhysicalMemory()
    {
        if (!OperatingSystem.IsWindows()) return (-1, -1);
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return (-1, -1);
            const ulong mb = 1024UL * 1024UL;
            return ((long)(status.ullAvailPhys / mb), (long)(status.ullTotalPhys / mb));
        }
        catch (DllNotFoundException) { return (-1, -1); }
        catch (EntryPointNotFoundException) { return (-1, -1); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
