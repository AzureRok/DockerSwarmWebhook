using System.Text.Json.Serialization;

namespace DockerSwarmWebhook.Services;

// ── Response types (replace anonymous types, which are not AOT-safe) ────────

public sealed record ApiResponse(string Message);
public sealed record ErrorResponse(string Error);

// ── Source-generated JSON context ────────────────────────────────────────────

[JsonSerializable(typeof(List<DockerService>))]
[JsonSerializable(typeof(ServiceSpec))]
[JsonSerializable(typeof(IReadOnlyList<WebhookServiceInfo>))]
[JsonSerializable(typeof(List<WebhookServiceInfo>))]
[JsonSerializable(typeof(ApiResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AppJsonContext : JsonSerializerContext { }

