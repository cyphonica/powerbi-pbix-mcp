using System.Net;

namespace SuperBiMcp.Tests;

/// <summary>
/// A function-driven <see cref="HttpMessageHandler"/> for the offline connector tests. Each request is mapped
/// to a canned response by a caller-supplied delegate, so a connector's REAL orchestration (paging walk, cap
/// branches, JSON flattening, value-matrix shaping) runs end to end with no network and no credentials. Every
/// request URL is recorded so a test can assert the connector built the paths / paging query it should have.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode status, string? json)> _respond;

    public StubHttpHandler(Func<HttpRequestMessage, (HttpStatusCode status, string? json)> respond)
        => _respond = respond;

    /// <summary>Every absolute request URL the connector issued, in order (for path / paging assertions).</summary>
    public List<string> Requests { get; } = new();

    private HttpResponseMessage Build(HttpRequestMessage request)
    {
        Requests.Add(request.RequestUri!.ToString());
        var (status, json) = _respond(request);
        var resp = new HttpResponseMessage(status);
        if (json != null)
            resp.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return resp;
    }

    // the Graph connectors call SendAsync; the Files REST / URL sub-modes call the synchronous Send. Override
    // BOTH so either real code path is driven offline against the same canned responses.
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(Build(request));

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        => Build(request);
}
