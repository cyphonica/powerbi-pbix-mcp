using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using SuperBiMcp.Tools;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The Desktop Bridge protocol client and the decision logic behind the Wave G5 tools (bridge_status,
/// bridge_screenshot, bridge_reload, open_desktop), proven against in-memory streams and scratch trees -
/// no live Power BI Desktop anywhere.
///
/// The protocol facts under test were verified against Microsoft's shipped
/// @microsoft/powerbi-desktop-bridge-cli (the reference client for the same pipe): LSP-style
/// "Content-Length: N\r\n\r\n" framing where N counts UTF-8 BYTES, request params wrapped as
/// {client, clientActivityId, args}, manifest-driven method gating, and METHOD_NOT_AVAILABLE riding in
/// error.data. Each test states which wrong implementation it would catch.
/// </summary>
public sealed class DesktopBridgeTests : IDisposable
{
    private readonly string _scratch = NewScratch();

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "desktopbridge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        // Only the directory this test itself created, under a name no other run holds.
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    // ================================================================ framing

    [Fact]
    public void Frame_CountsUtf8Bytes_NotChars()
    {
        // "Māori" is 5 chars but 6 UTF-8 bytes; a char-counting frame would under-declare the body and the
        // bridge's reader would truncate the JSON one byte short - a parse error on every non-ASCII payload.
        string json = "{\"name\":\"Māori\"}";
        byte[] framed = BridgeRpc.Frame(json);
        int bodyBytes = Encoding.UTF8.GetByteCount(json);
        string expectedHeader = $"Content-Length: {bodyBytes}\r\n\r\n";

        Assert.Equal(expectedHeader, Encoding.ASCII.GetString(framed, 0, expectedHeader.Length));
        Assert.Equal(expectedHeader.Length + bodyBytes, framed.Length);
    }

    [Fact]
    public void ReadMessage_RoundTripsAFramedMessage()
    {
        var ms = new MemoryStream(BridgeRpc.Frame("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"x\":1}}"));

        var msg = BridgeRpc.ReadMessageAsync(ms, CancellationToken.None).GetAwaiter().GetResult();

        Assert.NotNull(msg);
        Assert.Equal(7, (int)msg!["id"]!);
        Assert.Equal(1, (int)msg["result"]!["x"]!);
    }

    [Fact]
    public void ReadMessage_ToleratesExtraHeaders_TheWayVscodeJsonrpcDoes()
    {
        // vscode-jsonrpc may emit Content-Type alongside Content-Length; a reader that assumes the first
        // header line is the length would mis-parse every such message.
        string body = "{\"id\":1,\"result\":{}}";
        byte[] framed = Encoding.UTF8.GetBytes(
            $"Content-Type: application/vscode-jsonrpc; charset=utf-8\r\ncontent-length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

        var msg = BridgeRpc.ReadMessageAsync(new MemoryStream(framed), CancellationToken.None).GetAwaiter().GetResult();

        Assert.NotNull(msg);
        Assert.Equal(1, (int)msg!["id"]!);
    }

    [Fact]
    public void ReadMessage_WithNoContentLength_IsAFramingError_NotAHang()
    {
        byte[] junk = Encoding.UTF8.GetBytes("Content-Type: text/plain\r\n\r\n{}");
        var ex = Record.Exception(() => BridgeRpc.ReadMessageAsync(new MemoryStream(junk), CancellationToken.None).GetAwaiter().GetResult());
        Assert.IsType<IOException>(ex);
        Assert.Contains("Content-Length", ex!.Message);
    }

    [Fact]
    public void ReadMessage_AtACleanEof_ReturnsNull()
        // EOF BETWEEN messages is the pipe closing normally - null, so the caller reports "closed before
        // answering" rather than a framing error.
        => Assert.Null(BridgeRpc.ReadMessageAsync(new MemoryStream(), CancellationToken.None).GetAwaiter().GetResult());

    [Fact]
    public void ReadMessage_WhenThePipeClosesMidBody_Throws()
    {
        // Header promises 100 bytes, the stream carries 2: silently returning a short body would hand the
        // JSON parser garbage that MIGHT still parse (a truncated-but-valid prefix) - corrupt data, not an error.
        byte[] framed = Encoding.UTF8.GetBytes("Content-Length: 100\r\n\r\n{}");
        Assert.Throws<IOException>(() =>
            BridgeRpc.ReadMessageAsync(new MemoryStream(framed), CancellationToken.None).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("Content-Length: 42\r\n", 42)]
    [InlineData("CONTENT-LENGTH: 7\r\n", 7)]
    [InlineData("Content-Type: x\r\nContent-Length: 0\r\n", 0)]
    public void ParseContentLength_IsCaseInsensitive_AndSkipsForeignHeaders(string block, int expected)
        => Assert.Equal(expected, BridgeRpc.ParseContentLength(block));

    // ================================================================ request envelope

    [Fact]
    public void BuildRequest_CarriesTheBridgeEnvelope()
    {
        // The bridge rejects bare params: every request must wrap its args as {client, clientActivityId,
        // args} (verified from Microsoft's CLI). A client that sends args at the top level gets refused.
        var req = BridgeRpc.BuildRequest(3, "file.reload/v1", new JsonObject { ["reloadModelDefinition"] = true }, "activity-1");

        Assert.Equal("2.0", (string)req["jsonrpc"]!);
        Assert.Equal(3, (int)req["id"]!);
        Assert.Equal("file.reload/v1", (string)req["method"]!);
        var p = Assert.IsType<JsonObject>(req["params"]);
        Assert.Equal("super-bi-mcp", (string)p["client"]!);
        Assert.Equal("activity-1", (string)p["clientActivityId"]!);
        Assert.True((bool)p["args"]!["reloadModelDefinition"]!);
    }

    // ================================================================ response parsing

    [Fact]
    public void ParseResponse_ResultPath_ReturnsTheResultObject()
    {
        var msg = (JsonObject)JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"success\":true}}")!;
        var (result, error) = BridgeRpc.ParseResponse(msg);
        Assert.Null(error);
        Assert.True((bool)result!["success"]!);
    }

    [Fact]
    public void ParseResponse_ErrorPath_LiftsTheBridgeCodeAndAvailableMethodsOutOfData()
    {
        // The bridge's own error shape rides in error.data: {code:"METHOD_NOT_AVAILABLE", details:
        // {requiredMethod, availableMethods}}. A parser reading only error.code/message would lose the
        // one field that tells the operator what this Desktop CAN do.
        var msg = (JsonObject)JsonNode.Parse(@"{
            ""jsonrpc"":""2.0"",""id"":2,
            ""error"":{""code"":-32601,""message"":""method not found"",
                       ""data"":{""code"":""METHOD_NOT_AVAILABLE"",
                                 ""details"":{""requiredMethod"":""report.snapshot.capture/v1"",
                                              ""availableMethods"":[""bridge.manifest"",""application.state.get/v1""]}}}}")!;

        var (result, error) = BridgeRpc.ParseResponse(msg);

        Assert.Null(result);
        Assert.Equal(-32601, error!.JsonRpcCode);
        Assert.Equal("METHOD_NOT_AVAILABLE", error.BridgeCode);
        Assert.True(error.IsMethodNotAvailable);
        Assert.Equal(new[] { "bridge.manifest", "application.state.get/v1" }, error.AvailableMethods);
    }

    [Fact]
    public void ParseResponse_MethodNotFound_IsRecognisedByJsonRpcCodeAlone()
    {
        // A bridge build that answers a plain -32601 with no data payload must still read as
        // method-not-available, or the graceful-degradation branches never fire.
        var msg = (JsonObject)JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32601,\"message\":\"nope\"}}")!;
        var (_, error) = BridgeRpc.ParseResponse(msg);
        Assert.True(error!.IsMethodNotAvailable);
        Assert.Null(error.BridgeCode);
    }

    [Fact]
    public void ParseResponse_ReadsTheRetryableFlag()
    {
        var msg = (JsonObject)JsonNode.Parse("{\"id\":1,\"error\":{\"code\":1,\"message\":\"busy\",\"data\":{\"code\":\"BUSY\",\"retryable\":true}}}")!;
        Assert.True(BridgeRpc.ParseResponse(msg).Error!.Retryable);
    }

    // ================================================================ manifest gating

    [Fact]
    public void MethodNames_ReadsTheManifestMethodsArray()
    {
        var manifest = (JsonObject)JsonNode.Parse(
            "{\"methods\":[{\"name\":\"bridge.manifest\"},{\"name\":\"file.reload/v1\"},{\"noName\":true}]}")!;
        var names = BridgeRpc.MethodNames(manifest);
        Assert.Equal(2, names.Count);
        Assert.Contains("file.reload/v1", names);
    }

    [Fact]
    public void MethodNames_OnAMissingOrShapelessManifest_IsEmpty_NotAThrow()
    {
        // Forward-compat: a future manifest shape must degrade to "nothing declared" so RequireMethod
        // refuses loudly, rather than the whole probe exploding.
        Assert.Empty(BridgeRpc.MethodNames(null));
        Assert.Empty(BridgeRpc.MethodNames(new JsonObject()));
        Assert.Empty(BridgeRpc.MethodNames((JsonObject)JsonNode.Parse("{\"methods\":\"not-an-array\"}")!));
    }

    [Fact]
    public void RequireMethod_Throws_ListingWhatIsAvailable()
    {
        var methods = new HashSet<string>(StringComparer.Ordinal) { "bridge.manifest", "application.state.get/v1" };
        var ex = Assert.Throws<InvalidOperationException>(() => BridgeRpc.RequireMethod(methods, "file.reload/v1"));
        Assert.Contains("file.reload/v1", ex.Message);
        Assert.Contains("application.state.get/v1", ex.Message);   // the operator learns what this build CAN do
        BridgeRpc.RequireMethod(methods, "bridge.manifest");        // and a declared method passes silently
    }

    // ================================================================ the client over in-memory streams

    /// <summary>A duplex stub: reads come from a scripted buffer, writes are captured for assertion.
    /// The Memory-based async paths are overridden to complete SYNCHRONOUSLY - the base Stream wrappers
    /// post every read to the thread pool, and under full-suite parallel load that starvation once pushed
    /// an in-memory exchange past its deadline (a flake, not a finding). The real pipe overrides these
    /// natively, so production behaviour is untouched.</summary>
    private sealed class ScriptedDuplex : Stream
    {
        private readonly MemoryStream _read;
        private readonly MemoryStream _written = new();
        internal ScriptedDuplex(byte[] responses) => _read = new MemoryStream(responses);
        internal byte[] Written => _written.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => new(_read.Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => Task.FromResult(_read.Read(buffer, offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { _written.Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { _written.Write(buffer, offset, count); return Task.CompletedTask; }
    }

    /// <summary>A stream whose reads never complete (respecting cancellation) - the silent-bridge case.</summary>
    private sealed class NeverAnswersStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("sync read must not be reached");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private static byte[] Framed(params string[] messages)
        => messages.SelectMany(m => BridgeRpc.Frame(m)).ToArray();

    [Fact]
    public void Call_RoundTrips_AndSendsAWellFormedFramedRequest()
    {
        var duplex = new ScriptedDuplex(Framed("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"currentFilePath\":\"C:\\\\r.pbip\"}}"));
        var client = new BridgeRpcClient(duplex);

        var result = client.Call("application.state.get/v1", new JsonObject(), TimeSpan.FromSeconds(5));

        Assert.Equal("C:\\r.pbip", (string)result["currentFilePath"]!);
        // and what went out on the wire is itself one well-framed request with the envelope intact
        var sent = BridgeRpc.ReadMessageAsync(new MemoryStream(duplex.Written), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal("application.state.get/v1", (string)sent!["method"]!);
        Assert.Equal(1, (int)sent["id"]!);
        Assert.Equal("super-bi-mcp", (string)sent["params"]!["client"]!);
        Assert.NotNull(sent["params"]!["clientActivityId"]);
    }

    [Fact]
    public void Call_SkipsNotificationsAndForeignIds_UntilItsOwnAnswerArrives()
    {
        // The pipe is a shared conversation: progress notifications (no id) and answers to other requests
        // can precede ours. A client that takes the FIRST message as the answer returns someone else's data.
        var duplex = new ScriptedDuplex(Framed(
            "{\"jsonrpc\":\"2.0\",\"method\":\"progress\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{\"foreign\":true}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"mine\":true}}"));

        var result = new BridgeRpcClient(duplex).Call("bridge.manifest", new JsonObject(), TimeSpan.FromSeconds(5));

        Assert.True((bool)result["mine"]!);
        Assert.Null(result["foreign"]);
    }

    [Fact]
    public void Call_OnAnErrorResponse_ThrowsABridgeRpcException_WithTheNormalisedError()
    {
        var duplex = new ScriptedDuplex(Framed(
            "{\"id\":1,\"error\":{\"code\":-32601,\"message\":\"no\",\"data\":{\"code\":\"METHOD_NOT_AVAILABLE\",\"details\":{\"availableMethods\":[\"bridge.manifest\"]}}}}"));

        var ex = Assert.Throws<BridgeRpcException>(() =>
            new BridgeRpcClient(duplex).Call("file.reload/v1", new JsonObject(), TimeSpan.FromSeconds(5)));

        Assert.True(ex.Error.IsMethodNotAvailable);
        Assert.Contains("bridge.manifest", ex.Message);   // the available methods surface in the message
    }

    [Fact]
    public void Call_AgainstASilentBridge_TimesOutCleanly_NamingTheMethod()
    {
        // The failure mode this exists for: a wedged Desktop holding the pipe open forever. Without the
        // deadline the MCP tool call would hang the whole server.
        var ex = Assert.Throws<TimeoutException>(() =>
            new BridgeRpcClient(new NeverAnswersStream()).Call("bridge.manifest", new JsonObject(), TimeSpan.FromMilliseconds(100)));
        Assert.Contains("bridge.manifest", ex.Message);
    }

    [Fact]
    public void Call_WhenThePipeClosesBeforeAnswering_Throws_NotNull()
        => Assert.Throws<IOException>(() =>
            new BridgeRpcClient(new ScriptedDuplex(Array.Empty<byte>())).Call("bridge.manifest", new JsonObject(), TimeSpan.FromSeconds(5)));

    [Theory]
    [InlineData("{\"id\":5,\"result\":{}}", 5, true)]
    [InlineData("{\"id\":\"5\",\"result\":{}}", 5, true)]    // a server may quote the id back as a string
    [InlineData("{\"id\":6,\"result\":{}}", 5, false)]
    [InlineData("{\"method\":\"progress\"}", 5, false)]       // a notification has no id at all
    public void MatchesId_HandlesNumericStringAndAbsentIds(string json, int id, bool expected)
        => Assert.Equal(expected, BridgeRpc.MatchesId((JsonObject)JsonNode.Parse(json)!, id));

    // ================================================================ pipe name parsing + discovery

    [Theory]
    [InlineData(@"\\.\pipe\pbi-desktop-bridge-4242", 4242)]
    [InlineData("pbi-desktop-bridge-1", 1)]
    [InlineData(@"\\.\pipe\pbi-desktop-bridge-", 0)]
    [InlineData(@"\\.\pipe\pbi-desktop-bridge-12x", 0)]
    [InlineData(@"\\.\pipe\pbi-desktop-bridge--5", 0)]
    [InlineData(@"\\.\pipe\some-other-pipe", 0)]
    [InlineData(@"\\.\pipe\prefix-pbi-desktop-bridge-7", 0)]   // the prefix must START the name, not appear in it
    public void TryParseBridgePipePid_AcceptsOnlyRealBridgePipes(string name, int expected)
        => Assert.Equal(expected, DesktopBridge.TryParseBridgePipePid(name));

    [Fact]
    public void DiscoverBridgePids_FiltersSortsAndDedupes()
    {
        var pids = DesktopBridge.DiscoverBridgePids(() => new[]
        {
            @"\\.\pipe\pbi-desktop-bridge-300",
            @"\\.\pipe\unrelated",
            @"\\.\pipe\pbi-desktop-bridge-100",
            @"\\.\pipe\pbi-desktop-bridge-300",   // duplicate listing entries collapse
            @"\\.\pipe\pbi-desktop-bridge-abc",
        });
        Assert.Equal(new[] { 100, 300 }, pids);
    }

    [Fact]
    public void DiscoverBridgePids_WhenTheListerThrows_DegradesToEmpty()
        // The \\.\pipe\ listing can refuse on hardened hosts; bridge_status must then still report the
        // engine-only instances instead of dying.
        => Assert.Empty(DesktopBridge.DiscoverBridgePids(() => throw new UnauthorizedAccessException()));

    [SkippableFact]
    public void DiscoverBridgePids_AgainstTheRealPipeDirectory_DoesNotThrow()
    {
        // The canary for the Directory.GetFiles(@"\\.\pipe\") trap (older runtimes threw on exotic pipe
        // names). Zero hits is a fine answer; a throw is not.
        Skip.If(!OperatingSystem.IsWindows(), "the pipe directory only exists on Windows.");
        var pids = DesktopBridge.DiscoverBridgePids();
        Assert.NotNull(pids);
    }

    // ================================================================ instance correlation

    [Fact]
    public void CorrelateInstances_UnionsAllThreeSources_ByPid()
    {
        var seeds = DesktopBridge.CorrelateInstances(
            bridgePids: new[] { 20, 30 },
            engineOwners: new[] { (DesktopPid: 10, Port: 55001), (DesktopPid: 20, Port: 55002), (DesktopPid: 0, Port: 55003) },
            desktopProcessPids: new[] { 10, 40 });

        Assert.Equal(new[] { 10, 20, 30, 40 }, seeds.Select(s => s.Pid));
        Assert.False(seeds[0].HasBridgePipe);                       // engine + process, no pipe: the degrade entry
        Assert.Equal(new[] { 55001 }, seeds[0].Ports);
        Assert.True(seeds[1].HasBridgePipe);                        // pipe AND engine: the fully-correlated case
        Assert.Equal(new[] { 55002 }, seeds[1].Ports);
        Assert.True(seeds[2].HasBridgePipe);                        // pipe only (model not loaded yet)
        Assert.Empty(seeds[2].Ports);
        Assert.False(seeds[3].HasBridgePipe);                       // a bare Desktop process
        // the orphan engine whose parent could not be resolved (pid 0) appears nowhere
        Assert.DoesNotContain(seeds, s => s.Ports.Contains(55003));
    }

    [Fact]
    public void CorrelateInstances_CollectsMultipleEnginePortsUnderOneDesktop()
    {
        var seeds = DesktopBridge.CorrelateInstances(
            new[] { 10 }, new[] { (10, 55001), (10, 55002) }, Array.Empty<int>());
        Assert.Equal(new[] { 55001, 55002 }, Assert.Single(seeds).Ports);
    }

    // ================================================================ PBIR page listing

    private string MakeReportDir(string name = "Sales.Report")
    {
        string dir = Path.Combine(_scratch, name);
        Directory.CreateDirectory(Path.Combine(dir, "definition", "pages"));
        return dir;
    }

    private static void WritePage(string reportDir, string id, string? displayName)
    {
        string pageDir = Path.Combine(reportDir, "definition", "pages", id);
        Directory.CreateDirectory(pageDir);
        if (displayName != null)
            File.WriteAllText(Path.Combine(pageDir, "page.json"),
                $"{{\"name\":\"{id}\",\"displayName\":\"{displayName}\"}}");
    }

    [Fact]
    public void ReadPagesFromDefinition_FollowsPageOrder_AndReadsDisplayNames()
    {
        string dir = MakeReportDir();
        // pageOrder deliberately reverses creation order: the tab order in Desktop IS pageOrder, and a
        // reader that lists the folder alphabetically would screenshot the report in the wrong order.
        File.WriteAllText(Path.Combine(dir, "definition", "pages", "pages.json"),
            "{\"pageOrder\":[\"pageB\",\"pageA\"],\"activePageName\":\"pageB\"}");
        WritePage(dir, "pageA", "Overview");
        WritePage(dir, "pageB", "Detail");

        var pages = DesktopBridge.ReadPagesFromDefinition(dir);

        Assert.Equal(new[] { "pageB", "pageA" }, pages.Select(p => p.Id));
        Assert.Equal(new[] { "Detail", "Overview" }, pages.Select(p => p.DisplayName));
    }

    [Fact]
    public void ReadPagesFromDefinition_FallsBackToThePagesArray_NameThenId()
    {
        // Older definitions carry pages:[{name|id}] instead of pageOrder - the same fallback Microsoft's
        // CLI implements.
        string dir = MakeReportDir();
        File.WriteAllText(Path.Combine(dir, "definition", "pages", "pages.json"),
            "{\"pages\":[{\"name\":\"p1\"},{\"id\":\"p2\"}]}");

        Assert.Equal(new[] { "p1", "p2" }, DesktopBridge.ReadPagesFromDefinition(dir).Select(p => p.Id));
    }

    [Fact]
    public void ReadPagesFromDefinition_MissingPageJson_DegradesToANullDisplayName()
    {
        string dir = MakeReportDir();
        File.WriteAllText(Path.Combine(dir, "definition", "pages", "pages.json"), "{\"pageOrder\":[\"ghost\"]}");
        // no ghost/page.json on disk - the GUID must still surface (it IS the bridge's pageId)

        var page = Assert.Single(DesktopBridge.ReadPagesFromDefinition(dir));
        Assert.Equal("ghost", page.Id);
        Assert.Null(page.DisplayName);
    }

    [Fact]
    public void ReadPagesFromDefinition_WithNoPagesJson_IsEmpty_NotAThrow()
        => Assert.Empty(DesktopBridge.ReadPagesFromDefinition(MakeReportDir()));

    // ================================================================ report dir resolution

    [Fact]
    public void ResolveReportDir_HonoursThePbipArtifactPath_BeforeGuessing()
    {
        // The pointer names "Custom.Report" while a conventional "model.Report" sibling ALSO exists; the
        // pointer is authoritative - guessing first would reload/screenshot the wrong definition tree.
        string pbip = Path.Combine(_scratch, "model.pbip");
        string custom = MakeReportDir("Custom.Report");
        MakeReportDir("model.Report");
        File.WriteAllText(pbip, "{\"version\":\"1.0\",\"artifacts\":[{\"report\":{\"path\":\"Custom.Report\"}}]}");

        Assert.Equal(custom, DesktopBridge.ResolveReportDir(pbip));
    }

    [Fact]
    public void ResolveReportDir_FallsBackToTheConventionalSibling_WhenThePointerIsMalformed()
    {
        string pbip = Path.Combine(_scratch, "model.pbip");
        File.WriteAllText(pbip, "not json at all");
        string sibling = MakeReportDir("model.Report");

        Assert.Equal(sibling, DesktopBridge.ResolveReportDir(pbip));
    }

    [Fact]
    public void ResolveReportDir_AcceptsAFolderThatHoldsTheDefinitionItself_OrThroughAReportChild()
    {
        string direct = MakeReportDir("Direct.Report");
        Assert.Equal(direct, DesktopBridge.ResolveReportDir(direct));

        string project = Path.Combine(_scratch, "project");
        Directory.CreateDirectory(project);
        string child = Path.Combine(project, "Inner.Report");
        Directory.CreateDirectory(Path.Combine(child, "definition", "pages"));
        Assert.Equal(child, DesktopBridge.ResolveReportDir(project));
    }

    [Fact]
    public void ResolveReportDir_ForAPbixOrNothing_IsNull()
    {
        // a .pbix keeps its definition INSIDE the zip - there is no on-disk tree to hot-reload or enumerate
        string pbix = Path.Combine(_scratch, "report.pbix");
        File.WriteAllText(pbix, "zip-bytes");
        Assert.Null(DesktopBridge.ResolveReportDir(pbix));
        Assert.Null(DesktopBridge.ResolveReportDir(null));
        Assert.Null(DesktopBridge.ResolveReportDir(""));
        Assert.Null(DesktopBridge.ResolveReportDir(Path.Combine(_scratch, "no-such-file.pbip")));
    }

    // ================================================================ screenshot target resolution

    private static readonly IReadOnlyList<DesktopBridge.PbirPage> KnownPages = new[]
    {
        new DesktopBridge.PbirPage("guid-1", "Overview"),
        new DesktopBridge.PbirPage("guid-2", "Detail"),
    };

    [Fact]
    public void ResolvePages_AllPages_ReturnsEveryPageInOrder()
        => Assert.Equal(new[] { "guid-1", "guid-2" },
            DesktopBridge.ResolvePages(KnownPages, null, allPages: true).Select(p => p.Id));

    [Fact]
    public void ResolvePages_AllPagesWithNoOnDiskList_RefusesWithTheDotPbixExplanation()
    {
        // allPages over a plain .pbix cannot enumerate anything; the error must teach the fallback
        // (pass the page GUID) rather than leaving "no pages" unexplained.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopBridge.ResolvePages(Array.Empty<DesktopBridge.PbirPage>(), null, allPages: true));
        Assert.Contains("pageName", ex.Message);
    }

    [Theory]
    [InlineData("guid-2")]
    [InlineData("Detail")]
    [InlineData("detail")]   // displayName matching is case-insensitive, like every other page lookup here
    public void ResolvePages_ResolvesByGuidOrDisplayName(string name)
        => Assert.Equal("guid-2", Assert.Single(DesktopBridge.ResolvePages(KnownPages, name, false)).Id);

    [Fact]
    public void ResolvePages_AnUnknownNameAgainstAKnownList_IsATypo_ThrownWithThePageList()
    {
        // With a real page list in hand, passing an unknown name through to the bridge would surface as an
        // opaque bridge error; catching it here names every page the report actually has.
        var ex = Assert.Throws<ArgumentException>(() => DesktopBridge.ResolvePages(KnownPages, "Overveiw", false));
        Assert.Contains("guid-1 (Overview)", ex.Message);
    }

    [Fact]
    public void ResolvePages_WithNoListAtAll_PassesTheRawPageIdThrough()
    {
        // A .pbix has no on-disk list, but the caller may know the GUID (from get_pbir_page etc.) - the
        // bridge is the authority, so the value passes through instead of being refused.
        var only = Assert.Single(DesktopBridge.ResolvePages(Array.Empty<DesktopBridge.PbirPage>(), "raw-guid", false));
        Assert.Equal("raw-guid", only.Id);
    }

    [Fact]
    public void ResolvePages_NeitherOrBoth_AreArgumentErrors()
    {
        Assert.Throws<ArgumentException>(() => DesktopBridge.ResolvePages(KnownPages, null, false));
        Assert.Throws<ArgumentException>(() => DesktopBridge.ResolvePages(KnownPages, "Overview", true));
    }

    // ================================================================ snapshot payload + file naming

    [Fact]
    public void DecodeSnapshotPayload_DefaultsToBase64_AndDecodes()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47 };
        var withEncoding = (JsonObject)JsonNode.Parse($"{{\"payload\":\"{Convert.ToBase64String(png)}\",\"encoding\":\"base64\"}}")!;
        var withoutEncoding = (JsonObject)JsonNode.Parse($"{{\"payload\":\"{Convert.ToBase64String(png)}\"}}")!;
        Assert.Equal(png, DesktopBridge.DecodeSnapshotPayload(withEncoding));
        Assert.Equal(png, DesktopBridge.DecodeSnapshotPayload(withoutEncoding));
    }

    [Fact]
    public void DecodeSnapshotPayload_RefusesMissingPayload_UnknownEncoding_AndBadBase64()
    {
        // Writing a mis-decoded "PNG" would pass the tool call and fail only when a human opens the file -
        // the worst kind of green. All three corruptions must be loud.
        Assert.Throws<InvalidOperationException>(() => DesktopBridge.DecodeSnapshotPayload(new JsonObject()));
        Assert.Throws<InvalidOperationException>(() => DesktopBridge.DecodeSnapshotPayload(
            (JsonObject)JsonNode.Parse("{\"payload\":\"AA==\",\"encoding\":\"hex\"}")!));
        Assert.Throws<InvalidOperationException>(() => DesktopBridge.DecodeSnapshotPayload(
            (JsonObject)JsonNode.Parse("{\"payload\":\"not base64!!\"}")!));
    }

    [Theory]
    [InlineData("Sales / Overview", "Sales _ Overview")]
    [InlineData("  padded  ", "padded")]
    [InlineData("trailing dots...", "trailing dots")]   // Windows silently drops these - trim them loudly
    [InlineData("<>:\"|?*", "page")]      // nothing but underscores survives: fall back to a real name
    public void SafeFileName_StripsWhatWindowsRefuses(string input, string expected)
        => Assert.Equal(expected, DesktopBridge.SafeFileName(input));

    [Fact]
    public void UniqueFileName_SuffixesDuplicates_SoTwoPagesWithOneDisplayNameBothSurvive()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Overview", DesktopBridge.UniqueFileName(used, "Overview"));
        Assert.Equal("Overview (2)", DesktopBridge.UniqueFileName(used, "Overview"));
        Assert.Equal("Overview (3)", DesktopBridge.UniqueFileName(used, "Overview"));
    }

    // ================================================================ the reload gate

    [Fact]
    public void EnsureReloadAllowed_CleanState_ProceedsSilently()
        => Assert.Null(DesktopBridge.EnsureReloadAllowed(hasUnsavedChanges: false, force: false));

    [Fact]
    public void EnsureReloadAllowed_UnsavedWithoutForce_Refuses_NamingTheEscapeHatch()
    {
        // THE reload safety property: a hot-reload replaces the in-memory definition, so unsaved operator
        // work would be silently destroyed. The refusal must name force=true so a deliberate discard stays
        // one call away.
        var ex = Assert.Throws<InvalidOperationException>(() => DesktopBridge.EnsureReloadAllowed(true, false));
        Assert.Contains("unsaved", ex.Message);
        Assert.Contains("force=true", ex.Message);
    }

    [Fact]
    public void EnsureReloadAllowed_UnknowableStateWithoutForce_AlsoRefuses()
    {
        // No application.state.get/v1 means the unsaved flag is UNKNOWABLE - and an unknowable risk to the
        // operator's work is treated as a real one, never assumed away.
        var ex = Assert.Throws<InvalidOperationException>(() => DesktopBridge.EnsureReloadAllowed(null, false));
        Assert.Contains("force=true", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void EnsureReloadAllowed_Forced_ProceedsWithANote(bool? hasUnsaved)
    {
        string? note = DesktopBridge.EnsureReloadAllowed(hasUnsaved, force: true);
        Assert.NotNull(note);
        Assert.Contains("forced", note);
    }

    [Fact]
    public void EnsureReloadAllowed_ForceOnACleanState_CostsNothing()
        // force=true with nothing at risk must not scare the caller with a note about discarded work.
        => Assert.Null(DesktopBridge.EnsureReloadAllowed(false, force: true));

    // ================================================================ exe resolution

    [Fact]
    public void DesktopExeCandidates_StartsWithTheDesktopInteropResolution_AndAddsStoreLayouts()
    {
        var candidates = DesktopBridge.DesktopExeCandidates(null,
            (root, pattern) => root.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase)
                ? new[] { Path.Combine(root, "Microsoft.MicrosoftPowerBIDesktop_2.140.0.0_x64__8wekyb3d8bbwe") }
                : Enumerable.Empty<string>());

        // the head of the list is exactly what DesktopInterop resolves (override > env var > stock MSI path)
        Assert.Equal(DesktopInterop.ResolvePbixExe(null), candidates[0]);
        // the Store package's bin\PBIDesktop.exe is probed
        Assert.Contains(candidates, c =>
            c.Contains("Microsoft.MicrosoftPowerBIDesktop_2.140.0.0_x64__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)
            && c.EndsWith(Path.Combine("bin", "PBIDesktop.exe"), StringComparison.OrdinalIgnoreCase));
        // and the Store execution alias closes the list of layouts
        Assert.Contains(candidates, c => c.EndsWith("PBIDesktopStore.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveDesktopExe_PicksTheFirstExistingCandidate()
    {
        string store = Path.Combine("C:", "fake-store", "bin", "PBIDesktop.exe");
        string resolved = DesktopBridge.ResolveDesktopExe(null,
            fileExists: p => p.Equals(store, StringComparison.OrdinalIgnoreCase),
            enumerateDirs: (root, pattern) => root.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase)
                ? new[] { Path.Combine("C:", "fake-store") }
                : Enumerable.Empty<string>());
        Assert.Equal(store, resolved);
    }

    [Fact]
    public void ResolveDesktopExe_AnExplicitMissingExePath_FailsItself_InsteadOfFallingThrough()
    {
        // Falling through to a different install would silently mask the operator's typo - they asked for
        // THAT exe, so its absence is the error.
        var ex = Assert.Throws<FileNotFoundException>(() => DesktopBridge.ResolveDesktopExe(
            @"C:\typo\PBIDesktop.exe", fileExists: _ => false, enumerateDirs: (_, _) => Enumerable.Empty<string>()));
        Assert.Contains("typo", ex.Message);
    }

    [Fact]
    public void ResolveDesktopExe_WithNothingInstalled_ListsEveryProbedPath()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => DesktopBridge.ResolveDesktopExe(
            null, fileExists: _ => false, enumerateDirs: (_, _) => Enumerable.Empty<string>()));
        Assert.Contains("PBIDesktop.exe not found", ex.Message);
        Assert.Contains("DAXOPS_PBIDESKTOP_EXE", ex.Message);   // the fix is named, not just the failure
    }

    // ================================================================ handoff adoption

    [Fact]
    public void PickHandoffPid_PrefersTheNewPidWhoseOpenFileIsTheRequestedPath()
    {
        var baseline = new HashSet<int> { 10 };
        int? adopted = DesktopBridge.PickHandoffPid(baseline, new[] { 10, 20, 30 },
            currentFileOf: p => p == 30 ? @"C:\reports\model.pbip" : @"C:\other\thing.pbip",
            requestedPath: @"C:\reports\model.pbip");
        Assert.Equal(30, adopted);
    }

    [Fact]
    public void PickHandoffPid_WithOneNewPidAndNoFileMatch_AdoptsItAsTheBestGuess()
        => Assert.Equal(20, DesktopBridge.PickHandoffPid(new HashSet<int> { 10 }, new[] { 10, 20 },
            _ => null, @"C:\reports\model.pbip"));

    [Fact]
    public void PickHandoffPid_WithNothingNew_OrAnAmbiguousField_AdoptsNothing()
    {
        // Two new bridges and no file match: adopting either could hand the screenshot/reload loop somebody
        // else's document - the exact wrong-Desktop hazard the rest of this repo is built to refuse.
        Assert.Null(DesktopBridge.PickHandoffPid(new HashSet<int> { 10 }, new[] { 10 }, _ => null, "x"));
        Assert.Null(DesktopBridge.PickHandoffPid(new HashSet<int>(), new[] { 20, 30 }, _ => null, "x"));
    }

    [Fact]
    public void PathsEqual_NormalisesCaseAndRelativeSegments()
    {
        Assert.True(DesktopBridge.PathsEqual(@"C:\reports\model.pbip", @"C:\Reports\MODEL.PBIP"));
        Assert.True(DesktopBridge.PathsEqual(@"C:\reports\..\reports\model.pbip", @"C:\reports\model.pbip"));
        Assert.False(DesktopBridge.PathsEqual(@"C:\reports\model.pbip", @"C:\reports\other.pbip"));
    }

    // ================================================================ live smoke (skips without a live bridge)

    [SkippableFact]
    public void BridgeStatus_LiveSmoke_ReportsEveryLocalInstance()
    {
        // Runs only when a real Desktop with the bridge preview is up on this box; the suite never needs it.
        Skip.If(!OperatingSystem.IsWindows(), "the Desktop Bridge only exists on Windows.");
        Skip.If(DesktopBridge.DiscoverBridgePids().Count == 0,
            "no live pbi-desktop-bridge pipe on this machine - open a .pbip in a Desktop build with the bridge preview on.");

        var bridge = new DesktopBridge(NullLogger<DesktopBridge>.Instance,
            new PortDiscovery(NullLogger<PortDiscovery>.Instance));
        string json = J.Of(bridge.Status());

        var parsed = (JsonObject)JsonNode.Parse(json)!;
        Assert.True((bool)parsed["ok"]!);
        Assert.True((int)parsed["count"]! >= 1);
    }
}
