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

public sealed class DockerTask
{
    [JsonPropertyName("ID")]
    public string ID { get; set; } = "";

    [JsonPropertyName("DesiredState")]
    public string DesiredState { get; set; } = "";

    [JsonPropertyName("Status")]
    public DockerTaskStatus Status { get; set; } = new();
}

public sealed class DockerTaskStatus
{
    [JsonPropertyName("State")]
    public string State { get; set; } = "";

    [JsonPropertyName("Message")]
    public string? Message { get; set; }

    [JsonPropertyName("Err")]
    public string? Err { get; set; }
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

/// <summary>
/// Response body of POST /services/{id}/update. Docker returns HTTP 200 even when it could not
/// resolve a tag's digest from the registry; the reason is reported here as a non-fatal warning
/// (e.g. "image x:latest could not be accessed on a registry to record its digest"). Ignoring this
/// makes a failed ":latest" refresh look successful.
/// </summary>
public sealed class ServiceUpdateResponse
{
    [JsonPropertyName("Warnings")]
    public List<string>? Warnings { get; set; }
}

/// <summary>
/// Response body of GET /distribution/{name}/json. Returns the current registry digest of an image
/// reference without pulling it, so we can pin "repo:tag@sha256:..." into a service spec and force
/// Swarm to roll out the newest image for a moving tag like ":latest" or ":main".
/// </summary>
public sealed class DistributionInspect
{
    [JsonPropertyName("Descriptor")]
    public DistributionDescriptor? Descriptor { get; set; }
}

public sealed class DistributionDescriptor
{
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

