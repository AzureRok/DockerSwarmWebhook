namespace DockerSwarmWebhook.Services;

public sealed class WebhookServiceInfo
{
    public required string Id { get; init; }
    public required string ServiceName { get; init; }
    public required string WebhookName { get; init; }
    public ulong CurrentReplicas { get; init; }
    public ulong DesiredReplicas { get; init; }
}
