using System.Net;

namespace InternetMonitor.Network;

/// <summary>
/// Shared HTTPS reachability probe: HEAD, falling back to GET when the server rejects HEAD
/// with 405. Used by the internet-wide connectivity cascade and by the individual API
/// reachability checks.
/// </summary>
internal static class HttpProbe
{
    public static async Task<bool> CheckAsync(
        HttpClient httpClient,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<HttpStatusCode, bool>? isReachable = null)
    {
        isReachable ??= static status => (int)status is >= 200 and < 400;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await httpClient
                .SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (headResponse.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
                using var getResponse = await httpClient
                    .SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                return isReachable(getResponse.StatusCode);
            }

            return isReachable(headResponse.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return false;
        }
    }
}
