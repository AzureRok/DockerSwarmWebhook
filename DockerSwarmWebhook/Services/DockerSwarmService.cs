using Docker.DotNet;
using Docker.DotNet.Models;

namespace DockerSwarmWebhook.Services;

public sealed class DockerSwarmService : IDisposable
{
    private const string LabelEnabled = "swarm.webhook.enabled";
    private const string LabelName = "swarm.webhook.name";
    private const string LabelReplicas = "swarm.webhook.replicas";
    private const ulong DefaultReplicas = 1;

    private readonly DockerClient _client;
    private readonly ILogger<DockerSwarmService> _logger;

    public DockerSwarmService(ILogger<DockerSwarmService> logger)
    {
        _logger = logger;

        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        Uri endpoint;

        if (!string.IsNullOrEmpty(dockerHost))
        {
            endpoint = new Uri(dockerHost);
        }
        else if (OperatingSystem.IsWindows())
        {
            endpoint = new Uri("npipe://./pipe/docker_engine");
        }
        else
        {
            endpoint = new Uri("unix:///var/run/docker.sock");
        }

        _client = new DockerClientConfiguration(endpoint).CreateClient();
        _logger.LogInformation("Docker client configured with endpoint: {Endpoint}", endpoint);
    }

    public async Task<IReadOnlyList<WebhookServiceInfo>> ListEnabledServicesAsync(CancellationToken ct = default)
    {
        var services = await _client.Swarm.ListServicesAsync(cancellationToken: ct);

        return services
            .Where(s => s.Spec.Labels != null
                && s.Spec.Labels.TryGetValue(LabelEnabled, out var enabled)
                && string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                var labels = s.Spec.Labels ?? new Dictionary<string, string>();
                labels.TryGetValue(LabelName, out var webhookName);
                labels.TryGetValue(LabelReplicas, out var replicasStr);
                _ = ulong.TryParse(replicasStr, out var desiredReplicas);
                if (desiredReplicas == 0) desiredReplicas = DefaultReplicas;

                return new WebhookServiceInfo
                {
                    Id = s.ID,
                    ServiceName = s.Spec.Name,
                    WebhookName = webhookName ?? s.Spec.Name,
                    CurrentReplicas = s.Spec.Mode?.Replicated?.Replicas ?? 0,
                    DesiredReplicas = desiredReplicas
                };
            })
            .ToList();
    }

    public async Task<WebhookResult> StartServiceAsync(string webhookName, CancellationToken ct = default)
    {
        var service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return WebhookResult.NotFound(webhookName);

        var desiredReplicas = GetDesiredReplicas(service);
        service.Spec.Mode ??= new ServiceMode();
        service.Spec.Mode.Replicated ??= new ReplicatedService();
        service.Spec.Mode.Replicated.Replicas = desiredReplicas;

        await _client.Swarm.UpdateServiceAsync(
            service.ID,
            new ServiceUpdateParameters
            {
                Service = service.Spec,
                Version = (long)service.Version.Index
            },
            ct);

        _logger.LogInformation("Started service {ServiceName} (webhook: {WebhookName}) with {Replicas} replica(s)",
            service.Spec.Name, webhookName, desiredReplicas);

        return WebhookResult.Success($"Service '{webhookName}' started with {desiredReplicas} replica(s).");
    }

    public async Task<WebhookResult> StopServiceAsync(string webhookName, CancellationToken ct = default)
    {
        var service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return WebhookResult.NotFound(webhookName);

        service.Spec.Mode ??= new ServiceMode();
        service.Spec.Mode.Replicated ??= new ReplicatedService();
        service.Spec.Mode.Replicated.Replicas = 0;

        await _client.Swarm.UpdateServiceAsync(
            service.ID,
            new ServiceUpdateParameters
            {
                Service = service.Spec,
                Version = (long)service.Version.Index
            },
            ct);

        _logger.LogInformation("Stopped service {ServiceName} (webhook: {WebhookName})",
            service.Spec.Name, webhookName);

        return WebhookResult.Success($"Service '{webhookName}' stopped.");
    }

    public async Task<WebhookResult> RestartServiceAsync(string webhookName, CancellationToken ct = default)
    {
        var service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return WebhookResult.NotFound(webhookName);

        // Increment ForceUpdate to force Docker to re-pull and recreate all tasks.
        // This is the equivalent of `docker service update --force`.
        service.Spec.TaskTemplate.ForceUpdate += 1;

        // Ensure replicas are set to the desired count (in case the service was stopped).
        var desiredReplicas = GetDesiredReplicas(service);
        service.Spec.Mode ??= new ServiceMode();
        service.Spec.Mode.Replicated ??= new ReplicatedService();
        service.Spec.Mode.Replicated.Replicas = desiredReplicas;

        await _client.Swarm.UpdateServiceAsync(
            service.ID,
            new ServiceUpdateParameters
            {
                Service = service.Spec,
                Version = (long)service.Version.Index
            },
            ct);

        _logger.LogInformation(
            "Force-restarted service {ServiceName} (webhook: {WebhookName}) with {Replicas} replica(s)",
            service.Spec.Name, webhookName, desiredReplicas);

        return WebhookResult.Success(
            $"Service '{webhookName}' force-restarted with {desiredReplicas} replica(s). Image will be re-pulled.");
    }

    private async Task<SwarmService?> FindServiceByWebhookNameAsync(string webhookName, CancellationToken ct)
    {
        var services = await _client.Swarm.ListServicesAsync(cancellationToken: ct);

        return services.FirstOrDefault(s =>
        {
            if (s.Spec.Labels == null) return false;
            if (!s.Spec.Labels.TryGetValue(LabelEnabled, out var enabled)
                || !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            if (s.Spec.Labels.TryGetValue(LabelName, out var name))
                return string.Equals(name, webhookName, StringComparison.OrdinalIgnoreCase);

            return string.Equals(s.Spec.Name, webhookName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static ulong GetDesiredReplicas(SwarmService service)
    {
        if (service.Spec.Labels != null
            && service.Spec.Labels.TryGetValue(LabelReplicas, out var replicasStr)
            && ulong.TryParse(replicasStr, out var replicas)
            && replicas > 0)
        {
            return replicas;
        }

        return DefaultReplicas;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
