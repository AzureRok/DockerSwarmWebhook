using System.Net.Sockets;

namespace DockerSwarmWebhook.Services;

/// <summary>
/// AOT-compatible Docker Engine API client using Unix domain sockets or TCP.
/// Replaces Docker.DotNet (which uses Newtonsoft.Json / Reflection.Emit, incompatible with Native AOT).
/// </summary>
public sealed class DockerApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _registryAuth;
    private readonly ILogger<DockerApiClient> _logger;

    public DockerApiClient(ILogger<DockerApiClient> logger, string? dockerHost = null, string? registryAuth = null)
    {
        _logger = logger;
        _registryAuth = string.IsNullOrWhiteSpace(registryAuth) ? null : registryAuth;

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

    public async Task<List<DockerTask>> ListTasksForServiceAsync(string serviceId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"tasks?filters=%7B%22service%22%3A%7B%22{Uri.EscapeDataString(serviceId)}%22%3Atrue%7D%7D", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var tasks = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListDockerTask, ct);
        return tasks ?? [];
    }

    /// <summary>
    /// Resolves the current registry digest of an image reference (e.g. "repo/name:tag") without pulling it,
    /// via GET /distribution/{ref}/json. Returns null if the registry cannot be queried (e.g. missing auth).
    /// </summary>
    public async Task<string?> InspectDistributionDigestAsync(string imageReference, string? registryAuth = null, CancellationToken ct = default)
    {
        var requestUri = $"distribution/{Uri.EscapeDataString(imageReference)}/json";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        var effectiveRegistryAuth = string.IsNullOrWhiteSpace(registryAuth) ? _registryAuth : registryAuth;
        if (effectiveRegistryAuth != null)
        {
            request.Headers.TryAddWithoutValidation("X-Registry-Auth", effectiveRegistryAuth);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Could not resolve registry digest for {ImageReference} via {RequestUri}: {StatusCode} {Body}",
                imageReference, requestUri, (int)response.StatusCode, body);
            return null;
        }

        var inspect = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.DistributionInspect, ct);
        return inspect?.Descriptor?.Digest;
    }

    public async Task<IReadOnlyList<string>> UpdateServiceAsync(string serviceId, ulong version, ServiceSpec spec, string? registryAuth = null, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(spec, AppJsonContext.Default.ServiceSpec);
        var requestUri = $"services/{serviceId}/update?version={version}&queryRegistry=true&registryAuthFrom=spec";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            requestUri)
        {
            Content = content
        };

        var effectiveRegistryAuth = string.IsNullOrWhiteSpace(registryAuth) ? _registryAuth : registryAuth;

        if (effectiveRegistryAuth != null)
        {
            request.Headers.TryAddWithoutValidation("X-Registry-Auth", effectiveRegistryAuth);
        }

        _logger.LogInformation(
            "Sending Docker service update for {ServiceId} via {RequestUri} (registry auth attached: {HasRegistryAuth})",
            serviceId,
            requestUri,
            effectiveRegistryAuth != null);

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        // Docker returns HTTP 200 even when it could not contact the registry to record a new digest.
        // The reason is reported as a non-fatal warning, so surface it instead of treating 200 as success.
        var updateResult = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ServiceUpdateResponse, ct);
        var warnings = updateResult?.Warnings ?? [];

        foreach (var warning in warnings)
        {
            _logger.LogWarning("Docker service update warning for {ServiceId}: {Warning}", serviceId, warning);
        }

        return warnings;
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
