using System.Globalization;

namespace SuperBiMcp.Jobs;

/// <summary>Which of the two capacity pools a job draws from. Heavy is the pool that launches Power BI Desktop.</summary>
internal enum Lane { Cheap, Heavy }

internal enum AdmissionVerdict { Admit, HoldDisk, HoldRam, HoldLane }

/// <summary>
/// One job's claim on the host. ReserveMB is what the job is expected to need, not what it has taken:
/// admission has to be decided before the process exists, so the reserve is the only forward-looking input.
/// </summary>
internal readonly record struct AdmissionRequest(Lane Lane, long ReserveMB, int LaneActive, int LaneLimit);

/// <summary>
/// The arithmetic of admission, and nothing else. It reads no host state of its own: every reading arrives
/// in the <see cref="HostSnapshot"/>, which is what makes the whole decision reproducible in a test with no
/// Desktop and no VM. Counting jobs was the old failure; this decides on bytes and floors, and lane capacity
/// is only the last of three inputs.
///
/// Thresholds:
///   env SUPERBI_HEAVY_RESERVE_MB     - RAM a heavy job is assumed to need before any model scaling (default 4096)
///   env SUPERBI_HEAVY_RESERVE_CAP_MB - ceiling on the scaled reserve, so one huge model cannot hold the lane shut (default 24576)
///   env SUPERBI_RAM_FLOOR_MB         - free RAM that must survive a heavy admission (default 1024)
///   env SUPERBI_DISK_FLOOR_GB        - free space each measured volume must hold (default 10)
///
/// The disk floor is evaluated ONLY over the volume readings the snapshot supplies, and only a reading of a
/// ready fixed disk is a reading: a volume that cannot constrain the host arrives as
/// <see cref="HostResources.NoConstraintGB"/> and no floor can breach it. An optical or absent drive reports
/// zero bytes free, and floored as though that were a measurement it would hold every job forever.
/// </summary>
internal static class AdmissionPolicy
{
    internal const long   BaseReserveMB = 4096;    // env SUPERBI_HEAVY_RESERVE_MB
    internal const long   CapReserveMB  = 24576;   // env SUPERBI_HEAVY_RESERVE_CAP_MB
    internal const long   RamFloorMB    = 1024;    // env SUPERBI_RAM_FLOOR_MB
    internal const double DiskFloorGB   = 10.0;    // env SUPERBI_DISK_FLOOR_GB

    private const long BytesPerMB = 1024L * 1024L;

    // A refresh holds the model, the mashup engine's working set and the compressed source at once, so the
    // source size is a floor on the need, not the need itself.
    private const long ReserveMultiplier = 4;

    /// <summary>Base + 4x the source .pbix size in MB, clamped to [baseMB, capMB]. sourceBytes &lt;= 0 -> baseMB.</summary>
    internal static long ReserveForModel(long sourceBytes,
                                         long baseMB = BaseReserveMB, long capMB = CapReserveMB)
    {
        if (baseMB < 0) baseMB = 0;
        // A cap below the base would make the base unreachable; the base always wins.
        long ceiling = Math.Max(baseMB, capMB);
        if (sourceBytes <= 0) return Math.Min(baseMB, ceiling);

        long sourceMB = sourceBytes / BytesPerMB;
        // Guard the multiply: a nonsense reading must clamp, never wrap into a negative reserve.
        long scaled = sourceMB > (long.MaxValue - baseMB) / ReserveMultiplier
            ? ceiling
            : baseMB + sourceMB * ReserveMultiplier;

        return Math.Clamp(scaled, Math.Min(baseMB, ceiling), ceiling);
    }

    /// <summary>Order is fixed and load-bearing: disk floor, then RAM headroom, then lane capacity.</summary>
    internal static AdmissionVerdict Evaluate(in HostSnapshot host, in AdmissionRequest req,
                                              long? ramFloorMB = null, double? diskFloorGB = null)
    {
        // A full volume kills headless ZIP work just as dead as a refresh, so the disk floor outranks the lane.
        if (FirstBreach(host, diskFloorGB ?? EnvDouble("SUPERBI_DISK_FLOOR_GB", DiskFloorGB)) is not null)
            return AdmissionVerdict.HoldDisk;

        if (req.Lane == Lane.Cheap)
            return req.LaneActive >= req.LaneLimit ? AdmissionVerdict.HoldLane : AdmissionVerdict.Admit;

        // An unknown RAM reading never admits heavy: off Windows there is no reading to clear the floor with.
        if (host.RamFreeMB < 0) return AdmissionVerdict.HoldRam;
        if (host.RamFreeMB - req.ReserveMB < (ramFloorMB ?? EnvLong("SUPERBI_RAM_FLOOR_MB", RamFloorMB)))
            return AdmissionVerdict.HoldRam;

        // Free RAM never buys a second Desktop: the lane limit is the last word.
        if (req.LaneActive >= req.LaneLimit) return AdmissionVerdict.HoldLane;

        return AdmissionVerdict.Admit;
    }

    /// <summary>The operator-facing reason, in the numbers the verdict was actually decided on.</summary>
    internal static string Explain(AdmissionVerdict v, in HostSnapshot host, in AdmissionRequest req)
    {
        string lane = req.Lane == Lane.Heavy ? "heavy" : "cheap";
        switch (v)
        {
            case AdmissionVerdict.HoldDisk:
            {
                double floor = EnvDouble("SUPERBI_DISK_FLOOR_GB", DiskFloorGB);
                var breach = FirstBreach(host, floor);
                string where = breach?.Where ?? "disk";
                double free = breach?.FreeGB ?? 0;
                return $"hold: {where} has {Gb(free)}GB free, below the {Gb(floor)}GB floor";
            }
            case AdmissionVerdict.HoldRam:
                return host.RamFreeMB < 0
                    ? "hold: free RAM is unknown on this host, so no heavy job can be admitted"
                    : $"hold: {host.RamFreeMB}MB free RAM leaves {host.RamFreeMB - req.ReserveMB}MB after a " +
                      $"{req.ReserveMB}MB reserve, below the {EnvLong("SUPERBI_RAM_FLOOR_MB", RamFloorMB)}MB floor";
            case AdmissionVerdict.HoldLane:
                return $"hold: the {lane} lane is full ({req.LaneActive}/{req.LaneLimit} active)";
            default:
                return $"admit: {lane} lane {req.LaneActive}/{req.LaneLimit}, {host.RamFreeMB}MB free RAM, " +
                       $"{req.ReserveMB}MB reserved";
        }
    }

    /// <summary>The production entry point: the same request, with every threshold resolved from the environment.</summary>
    internal static AdmissionRequest FromEnv(Lane lane, long sourceBytes, int laneActive, int laneLimit)
    {
        long reserve = ReserveForModel(sourceBytes,
            EnvLong("SUPERBI_HEAVY_RESERVE_MB", BaseReserveMB),
            EnvLong("SUPERBI_HEAVY_RESERVE_CAP_MB", CapReserveMB));

        return new AdmissionRequest(lane, reserve, laneActive, laneLimit);
    }

    /// <summary>
    /// The first volume reading below the floor, named for the operator, or null. Only a real measurement of a
    /// ready fixed disk can breach: a volume the snapshot could not measure arrives as NoConstraintGB, and a
    /// negative reading is "unknown", never "full".
    /// </summary>
    private static (string Where, double FreeGB)? FirstBreach(in HostSnapshot host, double floorGB)
    {
        if (Breaches(host.JobRootFreeGB, floorGB)) return ("the job root volume", host.JobRootFreeGB);
        if (Breaches(host.TempFreeGB, floorGB)) return ("the temp volume", host.TempFreeGB);
        return null;
    }

    private static bool Breaches(double freeGB, double floorGB) => freeGB >= 0 && freeGB < floorGB;

    private static string Gb(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture);

    // A value that does not parse, or is negative, leaves the default standing: a mistyped override must not
    // silently disable a floor.
    private static long EnvLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

    private static double EnvDouble(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;
}
