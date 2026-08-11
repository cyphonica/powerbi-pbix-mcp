namespace SuperBiMcp.Services;

/// <summary>
/// Binds a TOM session to a Power BI Desktop THIS process launched. It takes no <see cref="PortDiscovery"/>,
/// by design: a pipeline must never guess a port. <see cref="ModelService.Connect"/> keeps its discovery
/// fallback because a null port is what marks the INTERACTIVE attach tool - a human's Desktop is already open
/// and nobody can prove which one it is. A pipeline has no such excuse: it started the Desktop, so it can
/// descend from its own pid to the engine and refuse anything else. Routing the pipeline through this type
/// instead lets the compiler, not a comment, keep the two paths apart.
///
/// Local launched sessions only. An XMLA session has no pid and no .pbix, so it cannot reach this seam.
///
/// Deliberately not unit-tested: it needs a live <see cref="DesktopSession"/>, which no CI box has. Its safety
/// is the safety of DesktopSession.ResolveEnginePort and DesktopInterop.AssertPortOwnedByLaunchedPid, both of
/// which are unit-tested offline.
/// </summary>
internal sealed class LaunchedModelConnector
{
    private readonly SessionStore _sessions;

    internal LaunchedModelConnector(SessionStore sessions) => _sessions = sessions;

    /// <summary>
    /// Assert port ownership, connect, and register a session carrying the ownership evidence
    /// (<see cref="ModelSession.LaunchedPid"/>, <see cref="ModelSession.LaunchedStartUtc"/>,
    /// <see cref="ModelSession.PbixPath"/>) that a later save or reap needs to prove it is acting on this
    /// Desktop and this document.
    ///
    /// The returned session and <paramref name="desktop"/> share one TOM.Server: <paramref name="desktop"/>
    /// owns it, and disposing the DesktopSession tears the connection down. Both teardown paths are
    /// best-effort, so releasing it from either side is safe.
    /// </summary>
    internal ModelSession Bind(DesktopSession desktop)
    {
        var srv = desktop.Connect();                                  // re-asserts ownership before it binds
        var db = desktop.AwaitModel(srv, desktop.DeadlineUtc);        // the launch deadline, not a fresh one

        var session = new ModelSession
        {
            Id = _sessions.NewId("model"),
            Port = desktop.Port,
            Server = srv,
            Database = db,
            LaunchedPid = desktop.LaunchedPid,
            LaunchedStartUtc = desktop.LaunchedStartUtc,
            PbixPath = desktop.PbixPath,
        };
        _sessions.AddModel(session);
        return session;
    }
}
