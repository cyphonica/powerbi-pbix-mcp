using System.Diagnostics;
using System.Globalization;
using System.Text;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp;

/// <summary>
/// An ephemeral, private, SharePoint-deployment-mode Power BI engine (the msmdsrv.exe bundled with
/// Power BI Desktop). Writes a minimal msmdsrv.ini into a temp workdir, launches the engine on a dynamic
/// port, discovers the port, and tears the process + workdir down on dispose. SharePoint mode +
/// UseXPress9Compression + EnableDisklessTMImageSave are what make ImageSave emit the Desktop-loadable
/// DataModel image (and what let ImageLoad read one back).
///
/// SCOPE (proven, see ModelPersistService + Bake): this bare engine can host a model whose partitions are
/// engine-native (a DAX calculated / DATATABLE partition, i.e. an M-FREE model). It CANNOT host a model
/// that carries Power Query (M) import partitions - Desktop wires up a separate Mashup container that a raw
/// launch does not, so both a refresh and an <c>ImageLoad</c> of such a model fail with "M engine
/// integration is not enabled" / "MEngineHelper is not loaded". That boundary is why a data-loaded, M-based
/// report can only be persisted through Power BI Desktop itself (live session + File &gt; Save).
/// <see cref="AssertHostable"/> is the deploy-time fail-fast for that boundary.
///
/// Extracted from <see cref="Bake"/> so the offline model-persist path can reuse the exact, proven launcher.
/// </summary>
internal sealed class PbiEngine : IDisposable
{
    /// <summary>Default location of the Power BI Desktop engine; override with SUPERBI_PBI_ENGINE.</summary>
    public const string DefaultEnginePath = @"C:\Program Files\Microsoft Power BI Desktop\bin\msmdsrv.exe";

    /// <summary>Resolve the engine path from SUPERBI_PBI_ENGINE, falling back to the Desktop install.</summary>
    public static string ResolveEnginePath()
    {
        string? env = Environment.GetEnvironmentVariable("SUPERBI_PBI_ENGINE");
        return string.IsNullOrWhiteSpace(env) ? DefaultEnginePath : env;
    }

    /// <summary>
    /// Fail-fast pre-flight for hosting <paramref name="model"/> on this bare engine - call it before the
    /// model is deployed (Databases.Add + Update) or attached. The bare msmdsrv has no Mashup container, so
    /// a Power Query (M) import partition is ACCEPTED at deploy and only detonates later, at refresh or
    /// ImageLoad, with the cryptic "M engine integration is not enabled" / "MEngineHelper is not loaded".
    /// Engine-native (DATATABLE / DAX calculated) partitions pass untouched.
    /// </summary>
    /// <exception cref="InvalidOperationException">the model carries at least one M-backed partition.</exception>
    public static void AssertHostable(TOM.Model model)
    {
        var mBacked = new List<string>();
        foreach (TOM.Table table in model.Tables)
        {
            foreach (TOM.Partition partition in table.Partitions)
            {
                // Entity (dataflow/composite) and PolicyRange (incremental refresh) partitions resolve
                // through shared M expressions, so they need the Mashup container exactly like a plain
                // M partition does.
                if (partition.SourceType is TOM.PartitionSourceType.M
                    or TOM.PartitionSourceType.Entity
                    or TOM.PartitionSourceType.PolicyRange)
                    mBacked.Add($"'{table.Name}'[{partition.Name}] ({partition.SourceType})");
            }
        }
        if (mBacked.Count == 0) return;

        throw new InvalidOperationException(
            "This model carries Power Query (M) import partitions, which this bare bundled engine cannot host: " +
            "Desktop wires up a separate Mashup container that a raw msmdsrv launch does not, so the deploy " +
            "would not fail here but downstream, at refresh or ImageLoad, with 'M engine integration is not " +
            "enabled' / 'MEngineHelper is not loaded'. Host the model in Power BI Desktop instead (the " +
            "DesktopSession path), or rebuild the partitions as engine-native DATATABLE sources first. " +
            "M-backed partitions: " + string.Join(", ", mBacked) + ".");
    }

    private readonly string _exe;
    private readonly string _work;
    private Process? _proc;

    public int Port { get; private set; }
    public string ConnectionString => $"localhost:{Port}";

    /// <param name="enginePath">Path to the msmdsrv.exe to launch.</param>
    /// <param name="workRoot">Root directory the ephemeral engine workdir is created under. Defaults to the
    /// system temp; pass a per-job temp to keep the engine's scratch inside that job's tree. Only the
    /// GUID-named workdir this instance creates is deleted on dispose - never the root itself.</param>
    public PbiEngine(string enginePath, string? workRoot = null)
    {
        _exe = enginePath;
        string root = string.IsNullOrWhiteSpace(workRoot) ? Path.GetTempPath() : workRoot;
        _work = Path.Combine(root, "daxops_pbiengine_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    /// <summary>Resolved ephemeral workdir (test seam; created by the ctor, deleted by Dispose).</summary>
    internal string WorkDir => _work;

    public void Start()
    {
        File.WriteAllText(Path.Combine(_work, "msmdsrv.ini"), BuildIni(_work), new UTF8Encoding(false));

        var psi = new ProcessStartInfo(_exe, $"-c -n {Guid.NewGuid()} -s \"{_work}\"")
        {
            WorkingDirectory = _work,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start the Power BI engine.");
        _proc.OutputDataReceived += (_, __) => { };
        _proc.ErrorDataReceived += (_, __) => { };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        // discover the port: prefer msmdsrv.port.txt, fall back to the listening TCP port owned by the PID
        string portFile = Path.Combine(_work, "msmdsrv.port.txt");
        for (int i = 0; i < 60 && Port == 0 && !_proc.HasExited; i++)
        {
            Thread.Sleep(750);
            if (File.Exists(portFile))
            {
                try
                {
                    int v = int.Parse(File.ReadAllText(portFile, Encoding.Unicode).Trim(), CultureInfo.InvariantCulture);
                    if (v > 0) { Port = v; break; }
                }
                catch { /* try the TCP fallback */ }
            }
            int tcp = DesktopInterop.FindListeningPort(_proc.Id);
            if (tcp > 0) { Port = tcp; break; }
        }

        if (Port == 0)
        {
            string log = "";
            try { log = string.Join("\n", File.ReadLines(Path.Combine(_work, "msmdsrv.log")).Reverse().Take(8).Reverse()); } catch { }
            throw new InvalidOperationException($"Power BI engine did not report a port. exited={_proc.HasExited}. log tail:\n{log}");
        }

        // prove the port we are about to hand out belongs to the msmdsrv WE launched. The port file can parse
        // microseconds before the socket enters the LISTENING table, so this retries rather than asserting once.
        DesktopInterop.AssertPortOwnedByLaunchedPidWithRetry(Port, _proc.Id, attempts: 20, delayMs: 250);
    }

    private static string BuildIni(string work)
    {
        string w = System.Security.SecurityElement.Escape(work);
        return
$@"<ConfigurationSettings>
  <DeploymentMode>1</DeploymentMode>
  <DataDir>{w}</DataDir><TempDir>{w}</TempDir><LogDir>{w}</LogDir><BackupDir>{w}</BackupDir>
  <AllowedBrowsingFolders>{w}</AllowedBrowsingFolders><CrashReportsFolder>{w}</CrashReportsFolder>
  <RecoveryModel>1</RecoveryModel><CleanDataFolderOnStartup>1</CleanDataFolderOnStartup>
  <AutoSetDefaultInitialCatalog>1</AutoSetDefaultInitialCatalog><InstanceVisible>0</InstanceVisible>
  <Port>0</Port><PrivateProcess>{Process.GetCurrentProcess().Id}</PrivateProcess><Language>0</Language>
  <Network><Requests><EnableBinaryXML>1</EnableBinaryXML><EnableCompression>1</EnableCompression></Requests><Responses><EnableBinaryXML>1</EnableBinaryXML><EnableCompression>1</EnableCompression><CompressionLevel>9</CompressionLevel></Responses><ListenOnlyOnLocalConnections>1</ListenOnlyOnLocalConnections></Network>
  <Log><Exception><CrashReportsFolder>{w}</CrashReportsFolder></Exception><FlightRecorder><Enabled>0</Enabled></FlightRecorder></Log>
  <Memory><MemoryHeapType>5</MemoryHeapType></Memory>
  <Feature><ManagedCodeEnabled>1</ManagedCodeEnabled><UseXPress9Compression>1</UseXPress9Compression><SkipXPress9CompressionSizeMB>0</SkipXPress9CompressionSizeMB><CompositeModel>1</CompositeModel></Feature>
  <VertiPaq><EnableProcessingSimplifiedLocks>1</EnableProcessingSimplifiedLocks><EnableDisklessTMImageSave>1</EnableDisklessTMImageSave><ImageLoadStreamBufferMB>2147483647</ImageLoadStreamBufferMB></VertiPaq>
</ConfigurationSettings>";
    }

    public void Dispose()
    {
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        for (int i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true); break; }
            catch { Thread.Sleep(400); }
        }
    }
}
