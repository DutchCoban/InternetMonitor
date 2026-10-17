using System.Net;
using System.Text;

namespace InternetMonitor.Tests.Net;

/// <summary>An HTTP handler that answers every request through one function and keeps the requests it saw. Nothing leaves the process.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<Uri> _requests = [];

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string body, string mediaType = "text/plain") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request.RequestUri!);
        }

        return Task.FromResult(respond(request));
    }
}
