using System.Net.Sockets;

namespace TaskTimer.Services;

/// <summary>
/// Retries a request whose host name could not be looked up. Some DNS servers (for example the one a
/// Hyper-V virtual switch provides) fail the first lookup of a name and answer the next one, which
/// otherwise shows up as "No such host is known". Nothing was sent to the server in that case, so
/// sending the request again is safe.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private const int Attempts = 4;

    public RetryHandler() : base(new HttpClientHandler()) { }

    public static HttpClient CreateClient(TimeSpan timeout) => new(new RetryHandler()) { Timeout = timeout };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.SendAsync(request, ct);
            }
            catch (HttpRequestException ex) when (attempt < Attempts && IsNameLookupFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }
    }

    private static bool IsNameLookupFailure(HttpRequestException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData };
}
