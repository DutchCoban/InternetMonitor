using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InternetMonitor.Tests.Net;

/// <summary>
/// A one-connection-at-a-time TCP listener on 127.0.0.1 that plays a web server badly on purpose:
/// it reads one request (unless told not to), keeps what it read, and answers with whatever raw
/// bytes the test gave it. Enough to drive the staged HTTP request through its TCP, TLS and HTTP
/// stages without the internet.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _reply;
    private readonly bool _readRequest;

    public LoopbackServer(string reply, bool readRequest = true)
    {
        _reply = reply;
        _readRequest = readRequest;
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The raw bytes this server answers with, so a test can tell its own answer from a stranger's.</summary>
    public string Reply => _reply;

    public Uri Url(string path = "/probe", string scheme = "http") => new($"{scheme}://127.0.0.1:{Port}{path}");

    /// <summary>The request text of the last connection, headers included.</summary>
    public string LastRequest { get; private set; } = string.Empty;

    /// <summary>A port nothing listens on right now: bound, then released.</summary>
    public static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        if (_readRequest)
                        {
                            var request = new StringBuilder();
                            byte[] buffer = new byte[4096];
                            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                            {
                                int read = await stream.ReadAsync(buffer, _stop.Token);
                                if (read == 0)
                                {
                                    break;
                                }

                                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            }

                            LastRequest = request.ToString();
                        }

                        byte[] reply = Encoding.ASCII.GetBytes(_reply);
                        await stream.WriteAsync(reply, _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                    }
                    catch (Exception)
                    {
                        // The client went away or the server is stopping; either ends this connection.
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
