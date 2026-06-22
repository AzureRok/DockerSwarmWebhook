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
    private const string UpdateModeConfigKey = "Docker:UpdateMode";
    private const string UpdateModeEnvVar = "DOCKER_SWARM_UPDATE_MODE";

    private readonly DockerApiClient _client;
    private readonly DockerCliService _dockerCli;
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
    private readonly bool _useDockerCli;

    public DockerSwarmService(ILogger<DockerSwarmService> logger, ILogger<DockerApiClient> dockerApiLogger, DockerCliService dockerCli, IConfiguration configuration)
    {
        _logger = logger;
        _dockerCli = dockerCli;

        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        DockerHost = string.IsNullOrEmpty(dockerHost) ? "unix:///var/run/docker.sock" : dockerHost;
        _registryServer = GetConfigOrEnvironmentValue(configuration, RegistryServerConfigKey, RegistryServerEnvVar);
        _registryUsername = GetConfigOrEnvironmentValue(configuration, RegistryUsernameConfigKey, RegistryUsernameEnvVar);
        _registryPassword = GetConfigOrEnvironmentValue(configuration, RegistryPasswordConfigKey, RegistryPasswordEnvVar);
        _registryIdentityToken = GetConfigOrEnvironmentValue(configuration, RegistryIdentityTokenConfigKey, RegistryIdentityTokenEnvVar);

        var registryAuth = GetConfigOrEnvironmentValue(configuration, RegistryAuthConfigKey, RegistryAuthEnvVar);
        _useDockerCli = string.Equals(GetConfigOrEnvironmentValue(configuration, UpdateModeConfigKey, UpdateModeEnvVar), "cli", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(registryAuth) && !HasExplicitRegistryCredentials())
        {
            registryAuth = TryLoadRegistryAuthFromDockerConfig();
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

        if (_useDockerCli)
        {
            _logger.LogInformation("Docker service updates are configured to use Docker CLI mode");
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

    private static string? NormalizeServiceImageForRegistryRefresh(DockerService service)
    {
        if (service.Spec.TaskTemplate?.ExtensionData == null
            || !service.Spec.TaskTemplate.ExtensionData.TryGetValue("ContainerSpec", out var containerSpec)
            || containerSpec.ValueKind != System.Text.Json.JsonValueKind.Object
            || !containerSpec.TryGetProperty("Image", out var imageElement))
        {
            return null;
        }

        var image = imageElement.GetString();
        if (string.IsNullOrWhiteSpace(image) || !image.Contains('@'))
            return image;

        var normalizedImage = image.Split('@', 2)[0];
        var lastSlashIndex = normalizedImage.LastIndexOf('/');
        var tagSeparatorIndex = normalizedImage.LastIndexOf(':');

        if (tagSeparatorIndex <= lastSlashIndex)
            return image;

        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var property in containerSpec.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);

                if (string.Equals(property.Name, "Image", StringComparison.Ordinal))
                {
                    writer.WriteStringValue(normalizedImage);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        stream.Position = 0;
        using var normalizedDocument = System.Text.Json.JsonDocument.Parse(stream);
        service.Spec.TaskTemplate.ExtensionData["ContainerSpec"] = normalizedDocument.RootElement.Clone();
        return normalizedImage;
    }

    private static string? TryLoadRegistryAuthFromDockerConfig()
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

            var auth = TryBuildRegistryAuthFromDockerConfig(candidate);
            if (!string.IsNullOrWhiteSpace(auth))
                return auth;
        }

        return null;
    }

    private static string? TryBuildRegistryAuthFromDockerConfig(string configPath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));

        if (!document.RootElement.TryGetProperty("auths", out var auths)
            || auths.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        foreach (var entry in auths.EnumerateObject())
        {
            if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            if (!entry.Value.TryGetProperty("auth", out var authProperty))
                continue;

            var encodedAuth = authProperty.GetString();
            if (string.IsNullOrWhiteSpace(encodedAuth))
                continue;

            var credentials = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedAuth));
            var separatorIndex = credentials.IndexOf(':');
            if (separatorIndex <= 0)
                continue;

            var payload = new RegistryAuthPayload(
                credentials[..separatorIndex],
                credentials[(separatorIndex + 1)..],
                entry.Name);

            var json = System.Text.Json.JsonSerializer.Serialize(payload, AppJsonContext.Default.RegistryAuthPayload);
            return ToBase64Url(json);
        }

        return null;
    }

    private static string ToBase64Url(string value)
    {
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
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

        if (_useDockerCli)
        {
            var image = NormalizeServiceImageForRegistryRefresh(service) ?? TryGetServiceImage(service);
            if (string.IsNullOrWhiteSpace(image))
                throw new InvalidOperationException($"Could not determine image for service '{service.Spec.Name}'.");

            await _dockerCli.RunServiceUpdateAsync(service.Spec.Name, image, force: false, ct);
            return WebhookResult.Success($"Service '{webhookName}' started via Docker CLI.");
        }

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

        if (_useDockerCli)
        {
            var image = NormalizeServiceImageForRegistryRefresh(service) ?? TryGetServiceImage(service);
            if (string.IsNullOrWhiteSpace(image))
                throw new InvalidOperationException($"Could not determine image for service '{service.Spec.Name}'.");

            await _dockerCli.RunServiceUpdateAsync(service.Spec.Name, image, force: false, ct);
            return WebhookResult.Success($"Service '{webhookName}' stopped via Docker CLI.");
        }

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

        if (_useDockerCli)
        {
            var image = NormalizeServiceImageForRegistryRefresh(service) ?? TryGetServiceImage(service);
            if (string.IsNullOrWhiteSpace(image))
                throw new InvalidOperationException($"Could not determine image for service '{service.Spec.Name}'.");

            await _dockerCli.RunServiceUpdateAsync(service.Spec.Name, image, force: true, ct);
            return WebhookResult.Success($"Service '{webhookName}' force-restarted via Docker CLI.");
        }

        var originalImage = TryGetServiceImage(service);
        var updateImage = NormalizeServiceImageForRegistryRefresh(service);

        // Ensure replicas are set to the desired count (in case the service was stopped).
        var desiredReplicas = GetDesiredReplicas(service);
        service.Spec.Mode ??= new DockerServiceMode();
        service.Spec.Mode.Replicated ??= new ReplicatedServiceMode();
        service.Spec.Mode.Replicated.Replicas = desiredReplicas;

        var registryAuth = GetRegistryAuthForService(service, out var authSource);
        _logger.LogInformation(
            "Restarting service {ServiceName} with image {OriginalImage} -> {UpdateImage} using registry auth source {AuthSource}",
            service.Spec.Name,
            originalImage ?? "<unknown>",
            updateImage ?? originalImage ?? "<unknown>",
            authSource);

        await _client.UpdateServiceAsync(service.ID, service.Version.Index, service.Spec, registryAuth, ct);

        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

        service = await FindServiceByWebhookNameAsync(webhookName, ct);
        if (service == null)
            return WebhookResult.NotFound(webhookName);

        service.Spec.TaskTemplate ??= new TaskSpec();
        service.Spec.TaskTemplate.ForceUpdate += 1;
        service.Spec.Mode ??= new DockerServiceMode();
        service.Spec.Mode.Replicated ??= new ReplicatedServiceMode();
        service.Spec.Mode.Replicated.Replicas = desiredReplicas;

        var refreshedImage = TryGetServiceImage(service);
        _logger.LogInformation(
            "Force-updating service {ServiceName} after image/auth refresh. Current image is {CurrentImage}",
            service.Spec.Name,
            refreshedImage ?? "<unknown>");

        registryAuth = GetRegistryAuthForService(service, out authSource);
        await _client.UpdateServiceAsync(service.ID, service.Version.Index, service.Spec, registryAuth, ct);

        _logger.LogInformation(
            "Force-restarted service {ServiceName} (webhook: {WebhookName}) with {Replicas} replica(s)",
            service.Spec.Name, webhookName, desiredReplicas);

        return WebhookResult.Success(
            $"Service '{webhookName}' force-restarted with {desiredReplicas} replica(s). Image will be re-pulled.");
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
