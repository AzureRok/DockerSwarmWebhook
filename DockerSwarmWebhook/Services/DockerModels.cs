using System.Text.Json;
using System.Text.Json.Serialization;

namespace DockerSwarmWebhook.Services;

/// <summary>Minimal Docker API models. [JsonExtensionData] preserves unknown fields for round-trip updates.</summary>
public sealed class DockerService
{
    [JsonPropertyName("ID")]
    public string ID { get; set; } = "";

    [JsonPropertyName("Version")]
    public ServiceVersion Version { get; set; } = new();

    [JsonPropertyName("Spec")]
    public ServiceSpec Spec { get; set; } = new();
}

public sealed class ServiceVersion
{
    [JsonPropertyName("Index")]
    public ulong Index { get; set; }
}

public sealed class ServiceSpec
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Labels")]
    public Dictionary<string, string>? Labels { get; set; }

    [JsonPropertyName("Mode")]
    public DockerServiceMode? Mode { get; set; }

    [JsonPropertyName("TaskTemplate")]
    public TaskSpec? TaskTemplate { get; set; }

    /// <summary>Captures all other Spec fields (Networks, EndpointSpec, UpdateConfig, etc.) for round-trip fidelity.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class DockerServiceMode
{
    [JsonPropertyName("Replicated")]
    public ReplicatedServiceMode? Replicated { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ReplicatedServiceMode
{
    [JsonPropertyName("Replicas")]
    public ulong Replicas { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TaskSpec
{
    [JsonPropertyName("ForceUpdate")]
    public uint ForceUpdate { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

