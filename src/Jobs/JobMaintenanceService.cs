using Microsoft.Extensions.Hosting;

namespace SuperBiMcp.Jobs;

/// <summary>
/// A hosted-service owner of <see cref="Runtime"/>'s lifetime for long-running hosts. It is the ONLY
/// production caller of <see cref="Runtime.Stop"/>, so shutdown is deterministic - <see cref="Runtime.Start"/>
/// is idempotent, so whichever thread arrives first wins and the other is a no-op.
///
/// The log sink writes to Console.Error under a `[tag]` convention; a host that redirects
/// Console.Error to a log file needs no file handle here.
/// </summary>
internal sealed class JobMaintenanceService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Runtime.Start(m => Console.Error.WriteLine($"[jobs] {m}"));
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Runtime.Stop();
        return base.StopAsync(cancellationToken);
    }
}
