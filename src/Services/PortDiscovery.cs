using System.Text;
using Microsoft.Extensions.Logging;

namespace SuperBiMcp.Services;

/// <summary>
/// Discovers the local Analysis Services (msmdsrv) instances that an open Power
/// BI Desktop spins up. Each open .pbix creates a workspace folder containing a
/// <c>msmdsrv.port.txt</c> with the loopback TCP port the model listens on.
/// </summary>
public sealed class PortDiscovery
{
    private readonly ILogger<PortDiscovery> _log;

    public PortDiscovery(ILogger<PortDiscovery> log) => _log = log;

    public record Instance(int Port, string WorkspaceDir, DateTime LastWrite, int OwnerPid = 0);

    /// <summary>Locate every running PBI Desktop workspace port, newest first. Workspace folders outlive
    /// a crashed Desktop, so each candidate is gated on a live msmdsrv actually owning the port.</summary>
    public IReadOnlyList<Instance> Discover() => Discover(DesktopInterop.FindMsmdsrvPidOnPort);

    /// <summary>internal + injectable so the liveness gate is unit-testable with no live msmdsrv.</summary>
    internal IReadOnlyList<Instance> Discover(Func<int, int> pidOnPort)
    {
        var roots = new List<string>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Installer (MSI) edition
        roots.Add(Path.Combine(local, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces"));
        // Microsoft Store edition
        roots.Add(Path.Combine(profile, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"));

        return Discover(roots, pidOnPort);
    }

    /// <summary>Root-injectable core: GetFolderPath cannot be redirected on Windows, so proving the scan and
    /// the liveness gate over a scratch tree needs the roots injected as well as the pid lookup.</summary>
    internal IReadOnlyList<Instance> Discover(IEnumerable<string> roots, Func<int, int> pidOnPort)
    {
        var results = new List<Instance>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] portFiles;
            try { portFiles = Directory.GetFiles(root, "msmdsrv.port.txt", SearchOption.AllDirectories); }
            catch (Exception ex) { _log.LogWarning(ex, "Scan failed under {Root}", root); continue; }

            foreach (var pf in portFiles)
            {
                try
                {
                    // The port file is UTF-16-LE with a BOM; strip BOM + whitespace.
                    string raw = File.ReadAllText(pf, Encoding.Unicode).Trim('﻿', '\r', '\n', ' ', '\t');
                    if (int.TryParse(raw, out int port) && port > 0)
                        results.Add(new Instance(port, Path.GetDirectoryName(pf)!, File.GetLastWriteTime(pf)));
                }
                catch (Exception ex) { _log.LogWarning(ex, "Could not read port file {File}", pf); }
            }
        }

        return results
            .GroupBy(r => r.Port).Select(g => g.First())   // de-dup by port
            .Select(r => r with { OwnerPid = pidOnPort(r.Port) })
            .Where(r => r.OwnerPid != 0)                   // a crashed Desktop leaves its port file behind
            .OrderByDescending(r => r.LastWrite)
            .ToList();
    }
}
