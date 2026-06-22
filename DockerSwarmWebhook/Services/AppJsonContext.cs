using System.Text.Json.Serialization;

namespace DockerSwarmWebhook.Services;

// ── Response types (replace anonymous types, which are not AOT-safe) ────────

public sealed record ApiResponse(string Message);
public sealed record ErrorResponse(string Error);
public sealed record DiagnosticsResponse(string DockerHost, bool RegistryAuthConfigured, bool RegistryAuthValid, bool RegistryAuthLoadedFromDockerConfig);
public sealed record RegistryAuthPayload(string Username, string Password, string ServerAddress);
public sealed record RegistryIdentityTokenPayload(string IdentityToken, string? ServerAddress);
public sealed record ServiceImageDiagnosticsResponse(string ServiceName, string WebhookName, string? Image, string RegistryAuthSource);
public sealed record ServiceTaskDiagnosticsResponse(string ServiceName, string WebhookName, string TaskId, string DesiredState, string CurrentState, string? Message, string? Error);

// ── Source-generated JSON context ────────────────────────────────────────────

[JsonSerializable(typeof(List<DockerService>))]
[JsonSerializable(typeof(List<DockerTask>))]
[JsonSerializable(typeof(ServiceSpec))]
[JsonSerializable(typeof(IReadOnlyList<WebhookServiceInfo>))]
[JsonSerializable(typeof(List<WebhookServiceInfo>))]
[JsonSerializable(typeof(ApiResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(DiagnosticsResponse))]
[JsonSerializable(typeof(RegistryAuthPayload))]
[JsonSerializable(typeof(RegistryIdentityTokenPayload))]
[JsonSerializable(typeof(ServiceImageDiagnosticsResponse))]
[JsonSerializable(typeof(List<ServiceTaskDiagnosticsResponse>))]
[JsonSerializable(typeof(ServiceTaskDiagnosticsResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AppJsonContext : JsonSerializerContext { }

