namespace DockerSwarmWebhook.Services;

public sealed class DockerSwarmService : IDisposable
{
    private const string LabelEnabled = "swarm.webhook.enabled";
    private const string LabelName = "swarm.webhook.name";
    private const string LabelReplicas = "swarm.webhook.replicas";
    private const string LabelRegistryServer = "swarm.webhook.registry.server";
    private const string LabelRegistryUsername = "swarm.webhook.registry.username";
    private const string LabelRegistryPassword = "swarm.webhook.registry.password";
    private const string LabelRegistryIdentityToken = "swarm.webhook.registry.identitytoken";
    private const string LabelRegistryAuth = "swarm.webhook.registry.auth";
    private const ulong DefaultReplicas = 1;
    private const string RegistryAuthConfigKey = "Docker:RegistryAuth";
    private const string RegistryAuthEnvVar = "DOCKER_REGISTRY_AUTH";
    private const string RegistryServerConfigKey = "Docker:RegistryServer";
    private const string RegistryUsernameConfigKey = "Docker:RegistryUsername";
    private const string RegistryPasswordConfigKey = "Docker:RegistryPassword";
    private const string RegistryIdentityTokenConfigKey = "Docker:RegistryIdentityToken";
    private const string RegistryServerEnvVar = "DOCKER_REGISTRY_SERVER";
    private const string RegistryUsernameEnvVar = "DOCKER_REGISTRY_USERNAME";
    private const string RegistryPasswordEnvVar = "DOCKER_REGISTRY_PASSWORD";
    private const string RegistryIdentityTokenEnvVar = "DOCKER_REGISTRY_IDENTITY_TOKEN";

    private readonly DockerApiClient _client;
    private readonly ILogger<DockerSwarmService> _logger;

    public string DockerHost { get; }
    public bool RegistryAuthConfigured { get; }
    public bool RegistryAuthValid { get; }
    public bool RegistryAuthLoadedFromDockerConfig { get; }

    private readonly string? _configuredRegistryAuth;
    private readonly string? _registryServer;
    private readonly string? _registryUsername;
    private readonly string? _registryPassword;
    private readonly string? _registryIdentityToken;
    private readonly Dictionary<string, string>? _dockerConfigRegistryAuths;

    public DockerSwarmService(ILogger<DockerSwarmService> logger, ILogger<DockerApiClient> dockerApiLogger, IConfiguration configuration)
    {
        _logger = logger;

        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        DockerHost = string.IsNullOrEmpty(dockerHost) ? "unix:///var/run/docker.sock" : dockerHost;
        _registryServer = GetConfigOrEnvironmentValue(configuration, RegistryServerConfigKey, RegistryServerEnvVar);
        _registryUsername = GetConfigOrEnvironmentValue(configuration, RegistryUsernameConfigKey, RegistryUsernameEnvVar);
        _registryPassword = GetConfigOrEnvironmentValue(configuration, RegistryPasswordConfigKey, RegistryPasswordEnvVar);
        _registryIdentityToken = GetConfigOrEnvironmentValue(configuration, RegistryIdentityTokenConfigKey, RegistryIdentityTokenEnvVar);

        var registryAuth = GetConfigOrEnvironmentValue(configuration, RegistryAuthConfigKey, RegistryAuthEnvVar);

        if (string.IsNullOrWhiteSpace(registryAuth) && !HasExplicitRegistryCredentials())
        {
            _dockerConfigRegistryAuths = TryLoadRegistryAuthsFromDockerConfig();
            registryAuth = _dockerConfigRegistryAuths?.Values.FirstOrDefault();
            RegistryAuthLoadedFromDockerConfig = !string.IsNullOrWhiteSpace(registryAuth);
        }

        _configuredRegistryAuth = registryAuth;
        RegistryAuthConfigured = !string.IsNullOrWhiteSpace(_configuredRegistryAuth) || HasExplicitRegistryCredentials();

        if (!string.IsNullOrWhiteSpace(_configuredRegistryAuth))
        {
            ValidateRegistryAuth(_configuredRegistryAuth);
            RegistryAuthValid = true;
        }
        else if (HasExplicitRegistryCredentials())
        {
            ValidateExplicitRegistryCredentials();
            RegistryAuthValid = true;
        }

        _client = new DockerApiClient(dockerApiLogger, dockerHost, _configuredRegistryAuth);

        _logger.LogInformation("Docker client configured (host: {Host})",
            DockerHost);

        if (RegistryAuthConfigured)
        {
            _logger.LogInformation(
                "Docker registry auth forwarding is enabled for service updates (source: {Source})",
                RegistryAuthLoadedFromDockerConfig ? "docker-config" : HasExplicitRegistryCredentials() ? "explicit-credentials" : "explicit-config");
        }
    }

    private static string? GetConfigOrEnvironmentValue(IConfiguration configuration, string configKey, string environmentVariableName)
    {
        var configValue = configuration[configKey];
        if (!string.IsNullOrWhiteSpace(configValue))
            return configValue;

        var environmentValue = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(environmentValue) ? null : environmentValue;
    }

    private bool HasExplicitRegistryCredentials()
    {
        return (!string.IsNullOrWhiteSpace(_registryUsername) && !string.IsNullOrWhiteSpace(_registryPassword))
               || !string.IsNullOrWhiteSpace(_registryIdentityToken);
    }

    private static bool HasServiceRegistryCredentials(Dictionary<string, string>? labels)
    {
        if (labels == null)
            return false;

        return (!string.IsNullOrWhiteSpace(GetLabelValue(labels, LabelRegistryUsername))
                && !string.IsNullOrWhiteSpace(GetLabelValue(labels, LabelRegistryPassword)))
               || !string.IsNullOrWhiteSpace(GetLabelValue(labels, LabelRegistryIdentityToken));
    }

    private void ValidateExplicitRegistryCredentials()
    {
        if (!string.IsNullOrWhiteSpace(_registryIdentityToken))
            return;

        if (string.IsNullOrWhiteSpace(_registryUsername) || string.IsNullOrWhiteSpace(_registryPassword))
        {
            throw new InvalidOperationException(
                $"Registry credentials must include both '{RegistryUsernameConfigKey}'/'{RegistryUsernameEnvVar}' and '{RegistryPasswordConfigKey}'/'{RegistryPasswordEnvVar}'.");
        }
    }

    private string? GetRegistryAuthForService(DockerService service, out string authSource)
    {
        var labels = service.Spec.Labels;
        var serviceRegistryAuth = GetLabelValue(labels, LabelRegistryAuth);
        if (!string.IsNullOrWhiteSpace(serviceRegistryAuth))
        {
            ValidateRegistryAuth(serviceRegistryAuth);
            authSource = "service-label-auth";
            return serviceRegistryAuth;
        }

        if (HasServiceRegistryCredentials(labels))
        {
            authSource = "service-label-credentials";
            return BuildRegistryAuth(
                GetLabelValue(labels, LabelRegistryServer),
                GetLabelValue(labels, LabelRegistryUsername),
                GetLabelValue(labels, LabelRegistryPassword),
                GetLabelValue(labels, LabelRegistryIdentityToken),
                service);
        }

        if (HasExplicitRegistryCredentials())
        {
            authSource = "global-explicit-credentials";
            return BuildRegistryAuth(_registryServer, _registryUsername, _registryPassword, _registryIdentityToken, service);
        }

        if (_dockerConfigRegistryAuths is { Count: > 0 })
        {
            var imageServerAddress = TryGetRegistryServerAddress(service);
            if (!string.IsNullOrWhiteSpace(imageServerAddress)
                && TryGetDockerConfigAuthForServer(imageServerAddress, out var matchedAuth))
            {
                authSource = "docker-config";
                return matchedAuth;
            }
        }

        authSource = RegistryAuthLoadedFromDockerConfig ? "docker-config" : !string.IsNullOrWhiteSpace(_configuredRegistryAuth) ? "global-auth" : "none";
        return _configuredRegistryAuth;
    }

    private static string BuildRegistryAuth(
        string? registryServer,
        string? registryUsername,
        string? registryPassword,
        string? registryIdentityToken,
        DockerService service)
    {
        var serverAddress = registryServer ?? TryGetRegistryServerAddress(service);

        if (string.IsNullOrWhiteSpace(serverAddress) && string.IsNullOrWhiteSpace(registryIdentityToken))
        {
            throw new InvalidOperationException(
                $"Registry server address is required. Set '{LabelRegistryServer}' or '{RegistryServerConfigKey}'/'{RegistryServerEnvVar}', or use an image with an explicit registry host.");
        }

        if (!string.IsNullOrWhiteSpace(registryIdentityToken))
        {
            var tokenPayload = new RegistryIdentityTokenPayload(registryIdentityToken, serverAddress);
            var tokenJson = System.Text.Json.JsonSerializer.Serialize(tokenPayload, AppJsonContext.Default.RegistryIdentityTokenPayload);
            return ToBase64Url(tokenJson);
        }

        if (string.IsNullOrWhiteSpace(registryUsername) || string.IsNullOrWhiteSpace(registryPassword))
        {
            throw new InvalidOperationException(
                $"Registry credentials must include both '{LabelRegistryUsername}'/'{LabelRegistryPassword}' or '{RegistryUsernameConfigKey}'/'{RegistryPasswordConfigKey}'.");
        }

        var credentialPayload = new RegistryAuthPayload(registryUsername, registryPassword, serverAddress!);
        var credentialJson = System.Text.Json.JsonSerializer.Serialize(credentialPayload, AppJsonContext.Default.RegistryAuthPayload);
        return ToBase64Url(credentialJson);
    }

    private static string? GetLabelValue(Dictionary<string, string>? labels, string key)
    {
        if (labels == null || !labels.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        return value;
    }

    private static string? TryGetRegistryServerAddress(DockerService service)
    {
        var image = TryGetServiceImage(service);
        if (string.IsNullOrWhiteSpace(image))
            return null;

        var imageWithoutDigest = image.Split('@', 2)[0];
        var firstSegment = imageWithoutDigest.Split('/', 2)[0];

        if (firstSegment.Contains('.') || firstSegment.Contains(':') || string.Equals(firstSegment, "localhost", StringComparison.OrdinalIgnoreCase))
            return firstSegment;

        return "https://index.docker.io/v1/";
    }

    private static string? TryGetServiceImage(DockerService service)
    {
        if (service.Spec.TaskTemplate?.ExtensionData == null
            || !service.Spec.TaskTemplate.ExtensionData.TryGetValue("ContainerSpec", out var containerSpec)
            || containerSpec.ValueKind != System.Text.Json.JsonValueKind.Object
            || !containerSpec.TryGetProperty("Image", out var imageElement))
        {
            return null;
        }

        return imageElement.GetString();
    }

    /// <summary>Extracts the "sha256:..." digest pinned to an image reference of the form "name:tag@sha256:...".</summary>
    private static string? TryGetImageDigest(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;

        var atIndex = image.IndexOf('@');
        return atIndex >= 0 && atIndex < image.Length - 1
            ? image[(atIndex + 1)..]
            : null;
    }

    /// <summary>Returns the "name:tag" portion of an image reference, dropping any "@sha256:..." digest suffix.</summary>
    private static string StripDigest(string image)
    {
        var atIndex = image.IndexOf('@');
        return atIndex >= 0 ? image[..atIndex] : image;
    }

    /// <summary>
    /// Builds a digest-pinned reference "name:tag@sha256:..." from a (possibly already-pinned) image and a
    /// freshly resolved digest. Pinning a NEW digest into the spec is the only change Swarm reliably rolls
    /// out for moving tags such as ":latest" or ":main".
    /// </summary>
    private static string BuildDigestPinnedReference(string image, string digest)
    {
        return $"{StripDigest(image)}@{digest}";
    }

    /// <summary>Writes a new Image value into the service's ContainerSpec, preserving all other fields.</summary>
    private static void SetServiceImage(DockerService service, string newImage)
    {
        if (service.Spec.TaskTemplate?.ExtensionData == null
            || !service.Spec.TaskTemplate.ExtensionData.TryGetValue("ContainerSpec", out var containerSpec)
            || containerSpec.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            var wroteImage = false;
            foreach (var property in containerSpec.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);

                if (string.Equals(property.Name, "Image", StringComparison.Ordinal))
                {
                    writer.WriteStringValue(newImage);
                    wroteImage = true;
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            if (!wroteImage)
            {
                writer.WriteString("Image", newImage);
            }

            writer.WriteEndObject();
        }

        stream.Position = 0;
        using var updatedDocument = System.Text.Json.JsonDocument.Parse(stream);
        service.Spec.TaskTemplate.ExtensionData["ContainerSpec"] = updatedDocument.RootElement.Clone();
    }

    private static Dictionary<string, string>? TryLoadRegistryAuthsFromDockerConfig()
    {
        var dockerConfigPath = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(dockerConfigPath))
            candidates.Add(Path.Combine(dockerConfigPath, "config.json"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            candidates.Add(Path.Combine(userProfile, ".docker", "config.json"));

        candidates.Add("/root/.docker/config.json");

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
                continue;

            var auths = TryBuildRegistryAuthsFromDockerConfig(candidate);
            if (auths is { Count: > 0 })
                return auths;
        }

        return null;
    }

    private static Dictionary<string, string>? TryBuildRegistryAuthsFromDockerConfig(string configPath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));

        if (!document.RootElement.TryGetProperty("auths", out var auths)
            || auths.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in auths.EnumerateObject())
        {
            if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            // `az acr login` / `docker login` with a token store the real credential in "identitytoken"
            // and leave the "auth" password blank (username becomes the all-zeros GUID). Prefer the
            // identity token when present; otherwise fall back to the username/password in "auth".
            string? identityToken = null;
            if (entry.Value.TryGetProperty("identitytoken", out var identityTokenProperty)
                && identityTokenProperty.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                identityToken = identityTokenProperty.GetString();
            }

            if (!string.IsNullOrWhiteSpace(identityToken))
            {
                var tokenPayload = new RegistryIdentityTokenPayload(identityToken, entry.Name);
                var tokenJson = System.Text.Json.JsonSerializer.Serialize(tokenPayload, AppJsonContext.Default.RegistryIdentityTokenPayload);
                result[entry.Name] = ToBase64Url(tokenJson);
                continue;
            }

            if (!entry.Value.TryGetProperty("auth", out var authProperty))
                continue;

            var encodedAuth = authProperty.GetString();
            if (string.IsNullOrWhiteSpace(encodedAuth))
                continue;

            string credentials;
            try
            {
                credentials = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedAuth));
            }
            catch (FormatException)
            {
                continue;
            }

            var separatorIndex = credentials.IndexOf(':');
            if (separatorIndex <= 0)
                continue;

            var username = credentials[..separatorIndex];
            var password = credentials[(separatorIndex + 1)..];

            // An empty password with no identity token is a token-based login whose token was stripped
            // (e.g. an expired `az acr login`); it can only produce 401s, so skip it instead of sending it.
            if (string.IsNullOrEmpty(password))
                continue;

            var payload = new RegistryAuthPayload(username, password, entry.Name);

            var json = System.Text.Json.JsonSerializer.Serialize(payload, AppJsonContext.Default.RegistryAuthPayload);
            result[entry.Name] = ToBase64Url(json);
        }

        return result.Count > 0 ? result : null;
    }

    private bool TryGetDockerConfigAuthForServer(string serverAddress, out string auth)
    {
        auth = string.Empty;

        if (_dockerConfigRegistryAuths is null)
            return false;

        if (_dockerConfigRegistryAuths.TryGetValue(serverAddress, out var exactMatch))
        {
            auth = exactMatch;
            return true;
        }

        var normalizedTarget = NormalizeRegistryHost(serverAddress);
        foreach (var entry in _dockerConfigRegistryAuths)
        {
            if (string.Equals(NormalizeRegistryHost(entry.Key), normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                auth = entry.Value;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeRegistryHost(string value)
    {
        var host = value;

        var schemeIndex = host.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
            host = host[(schemeIndex + 3)..];

        host = host.TrimEnd('/');

        return host is "index.docker.io/v1" or "index.docker.io" or "registry-1.docker.io" or "docker.io"
            ? "docker.io"
            : host;
    }

    private static string ToBase64Url(string value)
    {
        // Docker decodes X-Registry-Auth with Go's padded base64.URLEncoding (the Docker CLI encodes the
        // same way), so keep the '=' padding. Stripping it makes the daemon fail to decode the header and
        // fall back to anonymous access, which surfaces as a 401 from the registry even with valid creds.
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void ValidateRegistryAuth(string registryAuth)
    {
        try
        {
            var normalized = registryAuth.Replace('-', '+').Replace('_', '/');
            var padding = normalized.Length % 4;
            if (padding > 0)
                normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidOperationException("Registry auth payload must decode to a JSON object.");

            var hasUsername = root.TryGetProperty("username", out _);
            var hasPassword = root.TryGetProperty("password", out _);
            var hasServerAddress = root.TryGetProperty("serveraddress", out _);
            var hasIdentityToken = root.TryGetProperty("identitytoken", out _);

            if ((!hasUsername || !hasPassword || !hasServerAddress) && !hasIdentityToken)
            {
                throw new InvalidOperationException(
                    "Registry auth payload must contain either username/password/serveraddress or identitytoken.");
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException(
                $"Invalid Docker registry auth configuration in '{RegistryAuthConfigKey}'/'{RegistryAuthEnvVar}'. " +
                "Expected a base64url-encoded JSON object compatible with Docker X-Registry-Auth.", ex);
        }
    }

    public async Task<IReadOnlyList<WebhookServiceInfo>> ListEnabledServicesAsync(CancellationToken ct = default)
    {
        var services = await _client.ListServicesAsync(ct);

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

    public async Task<ServiceImageDiagnosticsResponse?> GetServiceImageDiagnosticsAsync(string webhookName, CancellationToken ct = default)
    {
        var service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return null;

        _ = GetRegistryAuthForService(service, out var authSource);

        return new ServiceImageDiagnosticsResponse(
            service.Spec.Name,
            webhookName,
            TryGetServiceImage(service),
            authSource);
    }

    public async Task<IReadOnlyList<ServiceTaskDiagnosticsResponse>?> GetServiceTaskDiagnosticsAsync(string webhookName, CancellationToken ct = default)
    {
        var service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return null;

        var tasks = await _client.ListTasksForServiceAsync(service.ID, ct);

        return tasks
            .Select(task => new ServiceTaskDiagnosticsResponse(
                service.Spec.Name,
                webhookName,
                task.ID,
                task.DesiredState,
                task.Status.State,
                task.Status.Message,
                task.Status.Err))
            .ToList();
    }

    public async Task<WebhookResult> StartServiceAsync(string webhookName, CancellationToken ct = default)
    {
        var service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return WebhookResult.NotFound(webhookName);

        var desiredReplicas = GetDesiredReplicas(service);
        service.Spec.Mode ??= new DockerServiceMode();
        service.Spec.Mode.Replicated ??= new ReplicatedServiceMode();
        service.Spec.Mode.Replicated.Replicas = desiredReplicas;

        var registryAuth = GetRegistryAuthForService(service, out var authSource);
        _logger.LogInformation("Starting service {ServiceName} using registry auth source {AuthSource}", service.Spec.Name, authSource);
        await _client.UpdateServiceAsync(service.ID, service.Version.Index, service.Spec, registryAuth, ct);

        _logger.LogInformation("Started service {ServiceName} (webhook: {WebhookName}) with {Replicas} replica(s)",
            service.Spec.Name, webhookName, desiredReplicas);

        return WebhookResult.Success($"Service '{webhookName}' started with {desiredReplicas} replica(s).");
    }

    public async Task<WebhookResult> StopServiceAsync(string webhookName, CancellationToken ct = default)
    {
        var service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return WebhookResult.NotFound(webhookName);

        service.Spec.Mode ??= new DockerServiceMode();
        service.Spec.Mode.Replicated ??= new ReplicatedServiceMode();
        service.Spec.Mode.Replicated.Replicas = 0;

        var registryAuth = GetRegistryAuthForService(service, out var authSource);
        _logger.LogInformation("Stopping service {ServiceName} using registry auth source {AuthSource}", service.Spec.Name, authSource);
        await _client.UpdateServiceAsync(service.ID, service.Version.Index, service.Spec, registryAuth, ct);

        _logger.LogInformation("Stopped service {ServiceName} (webhook: {WebhookName})",
            service.Spec.Name, webhookName);

        return WebhookResult.Success($"Service '{webhookName}' stopped.");
    }

    public async Task<WebhookResult> RestartServiceAsync(string webhookName, CancellationToken ct = default)
    {
        var service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return WebhookResult.NotFound(webhookName);

        var originalImage = TryGetServiceImage(service);

        // Ensure replicas are set to the desired count (in case the service was stopped).
        var desiredReplicas = GetDesiredReplicas(service);
        service.Spec.Mode ??= new DockerServiceMode();
        service.Spec.Mode.Replicated ??= new ReplicatedServiceMode();
        service.Spec.Mode.Replicated.Replicas = desiredReplicas;

        service.Spec.TaskTemplate ??= new TaskSpec();

        var registryAuth = GetRegistryAuthForService(service, out var authSource);

        // The real reason a moving tag (":latest"/":main") does not roll out: Swarm only redeploys when the
        // image DIGEST in the spec changes. Re-submitting the same tag — even with ForceUpdate — just recreates
        // tasks from each worker's locally cached image. So resolve the tag's CURRENT registry digest ourselves
        // (like "docker service update --image" does) and pin "repo:tag@sha256:NEW" into the spec.
        var originalDigest = TryGetImageDigest(originalImage);
        string? resolvedDigest = null;
        string? updateImage = originalImage;

        if (!string.IsNullOrWhiteSpace(originalImage))
        {
            var tagReference = StripDigest(originalImage);
            resolvedDigest = await _client.InspectDistributionDigestAsync(tagReference, registryAuth, ct);

            if (!string.IsNullOrWhiteSpace(resolvedDigest))
            {
                updateImage = BuildDigestPinnedReference(originalImage, resolvedDigest);
                SetServiceImage(service, updateImage);
            }
        }

        // Bump ForceUpdate so tasks are recreated even when the resolved digest is unchanged (e.g. a forced
        // restart of the same image). When the digest changed, this also guarantees a clean rollout.
        service.Spec.TaskTemplate.ForceUpdate += 1;

        _logger.LogInformation(
            "Restarting service {ServiceName} with image {OriginalImage} -> {UpdateImage} (resolved digest: {ResolvedDigest}, ForceUpdate={ForceUpdate}) using registry auth source {AuthSource}",
            service.Spec.Name,
            originalImage ?? "<unknown>",
            updateImage ?? originalImage ?? "<unknown>",
            resolvedDigest ?? "<unresolved>",
            service.Spec.TaskTemplate.ForceUpdate,
            authSource);

        if (string.IsNullOrWhiteSpace(resolvedDigest))
        {
            _logger.LogWarning(
                "Could not resolve a registry digest for service {ServiceName}; the update may reuse the locally cached image. " +
                "If the registry returned 401, the mounted Docker credentials (source: {AuthSource}) are missing or expired. " +
                "For Azure Container Registry, prefer a service principal or repository-scoped token with a username/password over " +
                "'az acr login', whose identity token expires after a few hours.",
                service.Spec.Name, authSource);
        }

        var warnings = await _client.UpdateServiceAsync(service.ID, service.Version.Index, service.Spec, registryAuth, ct);

        if (warnings.Count > 0)
        {
            var warningText = string.Join(" | ", warnings);
            _logger.LogWarning(
                "Service {ServiceName} update returned warnings: {Warnings}",
                service.Spec.Name, warningText);
        }

        // Confirm what actually changed by comparing the pinned digest before vs. after.
        var refreshedService = await FindServiceByWebhookNameAsync(webhookName, ct);
        var refreshedImage = refreshedService != null ? TryGetServiceImage(refreshedService) : null;
        var refreshedDigest = TryGetImageDigest(refreshedImage);

        if (originalDigest != null && refreshedDigest != null
            && !string.Equals(originalDigest, refreshedDigest, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Service {ServiceName} digest changed {OldDigest} -> {NewDigest}; the latest image is being rolled out.",
                service.Spec.Name, originalDigest, refreshedDigest);

            return WebhookResult.Success(
                $"Service '{webhookName}' updated to a newer image ({refreshedDigest}) with {desiredReplicas} replica(s).");
        }

        if (!string.IsNullOrWhiteSpace(resolvedDigest)
            && string.Equals(originalDigest, refreshedDigest, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Service {ServiceName} already runs the newest image for this tag ({Digest}); tasks were restarted.",
                service.Spec.Name, refreshedDigest ?? resolvedDigest);

            return WebhookResult.Success(
                $"Service '{webhookName}' is already on the newest image; restarted {desiredReplicas} replica(s).");
        }

        _logger.LogInformation(
            "Force-restarted service {ServiceName} (webhook: {WebhookName}) with {Replicas} replica(s).",
            service.Spec.Name, webhookName, desiredReplicas);

        return WebhookResult.Success(
            $"Service '{webhookName}' restarted with {desiredReplicas} replica(s). " +
            (string.IsNullOrWhiteSpace(resolvedDigest)
                ? "Could not confirm a registry digest — verify the manager node's registry access and credentials."
                : "Latest image will be re-pulled."));
    }

    private async Task<DockerService?> FindServiceByWebhookNameAsync(string webhookName, CancellationToken ct)
    {
        var services = await _client.ListServicesAsync(ct);

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

    private static ulong GetDesiredReplicas(DockerService service)
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

    public void Dispose() => _client.Dispose();
}
