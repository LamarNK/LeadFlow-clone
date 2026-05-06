using System.Net;
using System.Net.Http;
using System.Text;

namespace LeadFlow.Tests.Support;

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public List<(string Path, string Body)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        Calls.Add((path, body));
        return await handler(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Ok(string json) => Json(HttpStatusCode.OK, json);
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
