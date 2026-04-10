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

        _http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            // Pin to HTTP/1.1 — Docker Engine does not speak HTTP/2.
            DefaultRequestVersion = new Version(1, 1),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    public async Task<List<DockerService>> ListServicesAsync(CancellationToken ct = default)
    {
        // No version prefix — Docker accepts unversioned paths and uses its own current API version.
        using var response = await _http.GetAsync("services", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var services = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListDockerService, ct);
        return services ?? [];
    }

    public async Task UpdateServiceAsync(string serviceId, ulong version, ServiceSpec spec, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(spec, AppJsonContext.Default.ServiceSpec);
        using var response = await _http.PostAsync($"services/{serviceId}/update?version={version}", content, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Throws with the full response body included so Docker error messages are visible in logs.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Docker API {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} " +
            $"→ {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    public void Dispose() => _http.Dispose();
}
