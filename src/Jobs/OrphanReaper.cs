using System.Globalization;

namespace SuperBiMcp.Jobs;

/// <summary>One process the reaper intends to kill, with the job that owned it, why it qualified, and the
/// recorded start time the post-kill verification compares against - the same identity Plan matched on.</summary>
internal readonly record struct Victim(string JobId, int Pid, string Name, string Reason, DateTime RecordedStartUtc);

/// <summary>
/// Kills the Desktop and engine processes left behind by jobs that are no longer live - the orphans a crashed
/// runner leaves, which the parent-scoped CleanupDesktop cannot see because their parent is already gone.
///
/// The safety argument is structural, not a filter: the ONLY input is <see cref="JobQueue.RecordedProcesses"/>,
/// so a process this queue never recorded cannot be named, cannot be planned and cannot be killed. An
/// operator's own interactive Power BI Desktop and any concurrent non-queue run are invisible here by
/// construction. Nothing in this class inspects the process table for candidates, and nothing may.
///
/// Three further guards narrow what remains: a job in a live state is never touched, a pid whose live start
/// time does not match the recorded one has been RECYCLED onto a stranger and is never touched, and a pid
/// younger than the age floor is never touched.
/// </summary>
internal static class OrphanReaper
{
    /// <summary>How old a recorded pid must be before it can be a victim. Overridable by env
    /// SUPERBI_REAP_MIN_AGE_SEC; a value that does not parse leaves the default standing.</summary>
    internal static readonly TimeSpan DefaultMinAge = TimeSpan.FromSeconds(120);

    /// <summary>The tolerance a start time loses on its way through text storage, matching
    /// <see cref="DesktopInterop.PidAlive(int, DateTime)"/>. A pid cannot be reused inside this window.</summary>
    private const double StartToleranceSeconds = 1.0;

    /// <summary>The verification budget after a kill: how long a process tree gets to finish dying before the
    /// victim is declared a survivor and its record is kept for the next sweep. The production kill delegate
    /// (<see cref="DesktopInterop.KillTree"/>) swallows every exception, so a kill call returning normally
    /// proves NOTHING - only observed absence does.</summary>
    private const int VerifyKillWaitMs = 5000;
    private const int VerifyKillPollMs = 250;

    /// <summary>Decides victims. Pure: both the clock and the start-time lookup are supplied.</summary>
    internal static IReadOnlyList<Victim> Plan(IEnumerable<RecordedProc> recorded,
                                               Func<int, DateTime?> startTimeUtc,
                                               DateTimeOffset now, TimeSpan minAge)
    {
        var victims = new List<Victim>();

        foreach (RecordedProc r in recorded)
        {
            if (r.Pid <= 0) continue;
            if (IsLive(r.State)) continue;              // its job still owns it

            DateTime? actualStart;
            try { actualStart = startTimeUtc(r.Pid); }
            catch { continue; }                         // an unidentified process is never a target

            if (actualStart is null) continue;          // already gone: nothing to kill

            // Pid + start time is the identity. A mismatch means Windows handed this number to something else.
            if (Math.Abs((actualStart.Value - r.RecordedStartUtc).TotalSeconds) >= StartToleranceSeconds) continue;

            if (now.UtcDateTime - actualStart.Value < minAge) continue;

            victims.Add(new Victim(r.JobId, r.Pid, r.Name, $"orphan:{r.State}", r.RecordedStartUtc));
        }

        return victims;
    }

    /// <summary>
    /// Plans, kills each victim's tree, VERIFIES each victim is actually gone, then releases only the rows
    /// whose processes are all confirmed dead. Returns the number verifiably killed. Never throws: a reap is
    /// called on the runner's start and between jobs, and a box that cannot be tidied must still take work.
    /// </summary>
    internal static int Reap(JobQueue queue, Action<string> log, DateTimeOffset now,
                            Func<int, DateTime?>? startTimeUtc = null, Action<int>? killTree = null,
                            TimeSpan? minAge = null, Action<int>? sleep = null)
    {
        Func<int, DateTime?> started = startTimeUtc ?? DesktopInterop.PidStartTimeUtc;
        Action<int> kill = killTree ?? DesktopInterop.KillTree;
        Action<int> nap = sleep ?? Thread.Sleep;

        IReadOnlyList<RecordedProc> recorded;
        try
        {
            recorded = queue.RecordedProcesses();
        }
        catch (Exception ex)
        {
            // A queue that cannot be read names no victims. Reaping on a guess is how a stranger dies.
            log($"reap: queue unreadable ({ex.GetType().Name}); nothing reaped");
            return 0;
        }

        IReadOnlyList<Victim> victims = Plan(recorded, started, now, minAge ?? EnvMinAge());
        if (victims.Count == 0) return 0;

        int killed = 0;
        var failedJobs = new HashSet<string>(StringComparer.Ordinal);

        foreach (Victim v in victims)
        {
            bool gone;
            try
            {
                kill(v.Pid);
                gone = VerifiedGone(v, started, nap);
            }
            catch (Exception ex)
            {
                failedJobs.Add(v.JobId);
                log($"reap: could not kill {v.Name} pid {v.Pid} of job {v.JobId} ({ex.GetType().Name})");
                continue;
            }

            if (gone)
            {
                killed++;
                log($"reap: killed {v.Name} pid {v.Pid} of job {v.JobId} ({v.Reason})");
            }
            else
            {
                // The kill call returned but the process is still there - the production kill delegate
                // swallows access-denied and every other failure, so a normal return was never evidence.
                // Counting this as killed and clearing the record would put the survivor beyond every later
                // reap; keeping the row dirty means the next sweep tries again.
                failedJobs.Add(v.JobId);
                log($"reap: {v.Name} pid {v.Pid} of job {v.JobId} survived the kill - keeping its record for the next sweep");
            }
        }

        // ClearDesktop releases BOTH recorded pids of a job at once, so a job with a survivor keeps its row:
        // clearing it would erase the only record of a process still running, putting it beyond every later
        // reap. A row is released only when every one of its victims was VERIFIED gone; a row left dirty is
        // retried next time; a row cleared early leaks a Desktop forever.
        foreach (string jobId in victims.Select(v => v.JobId).Distinct(StringComparer.Ordinal))
        {
            if (failedJobs.Contains(jobId)) continue;
            try { queue.ClearDesktop(jobId); }
            catch (Exception ex) { log($"reap: could not release job {jobId} ({ex.GetType().Name})"); }
        }

        return killed;
    }

    /// <summary>
    /// True only when the victim's process is PROVEN gone after the kill: its pid no longer resolves to a
    /// start time, or resolves to a different one (Windows already recycled the number onto a stranger, so the
    /// recorded process is dead either way). Polls briefly - a multi-GB Desktop tree takes a moment to finish
    /// dying - and a lookup that throws proves nothing, so it reads as a survivor: keeping the record is the
    /// recoverable mistake, clearing it is not.
    /// </summary>
    private static bool VerifiedGone(Victim v, Func<int, DateTime?> startTimeUtc, Action<int> sleep)
    {
        for (int waitedMs = 0; ; waitedMs += VerifyKillPollMs)
        {
            DateTime? actual;
            try { actual = startTimeUtc(v.Pid); }
            catch { return false; }                     // unreadable = unproven = survivor

            if (actual is null) return true;            // absent: the kill landed
            if (Math.Abs((actual.Value - v.RecordedStartUtc).TotalSeconds) >= StartToleranceSeconds)
                return true;                            // recycled onto a stranger: ours is gone

            if (waitedMs >= VerifyKillWaitMs) return false;
            sleep(VerifyKillPollMs);
        }
    }

    private static bool IsLive(JobState state)
        => state is JobState.ADMITTED or JobState.RUNNING or JobState.VERIFYING;

    private static TimeSpan EnvMinAge()
        => int.TryParse(Environment.GetEnvironmentVariable("SUPERBI_REAP_MIN_AGE_SEC"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int sec) && sec >= 0
            ? TimeSpan.FromSeconds(sec)
            : DefaultMinAge;
}
