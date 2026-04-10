using System.Net.Sockets;

namespace DockerSwarmWebhook.Services;

/// <summary>
/// AOT-compatible Docker Engine API client using Unix domain sockets or TCP.
/// Replaces Docker.DotNet (which uses Newtonsoft.Json / Reflection.Emit, incompatible with Native AOT).
/// </summary>
public sealed class DockerApiClient : IDisposable
{
    private readonly HttpClient _http;

    public DockerApiClient(string? dockerHost = null)
    {
        Uri baseAddress;
        SocketsHttpHandler handler;

        if (!string.IsNullOrEmpty(dockerHost) &&
            (dockerHost.StartsWith("tcp://", StringComparison.Ordinal) ||
             dockerHost.StartsWith("http://", StringComparison.Ordinal)))
        {
            var uri = new Uri(dockerHost.Replace("tcp://", "http://", StringComparison.Ordinal));
            baseAddress = new Uri($"http://{uri.Host}:{uri.Port}/");
            handler = new SocketsHttpHandler();
        }
        else
        {
            var socketPath = string.IsNullOrEmpty(dockerHost)
                ? "/var/run/docker.sock"
                : dockerHost.Replace("unix://", "", StringComparison.Ordinal);

            handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };
            baseAddress = new Uri("http://localhost/");
        }

        _http = new HttpClient(handler) { BaseAddress = baseAddress };
    }

    public async Task<List<DockerService>> ListServicesAsync(CancellationToken ct = default)
    {
        var services = await _http.GetFromJsonAsync(
            "v1.41/services",
            AppJsonContext.Default.ListDockerService,
            ct);
        return services ?? [];
    }

    public async Task UpdateServiceAsync(string serviceId, ulong version, ServiceSpec spec, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(spec, AppJsonContext.Default.ServiceSpec);
        var response = await _http.PostAsync(
            $"v1.41/services/{serviceId}/update?version={version}",
            content,
            ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}


