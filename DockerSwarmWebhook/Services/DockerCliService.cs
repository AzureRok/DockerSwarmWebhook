namespace DockerSwarmWebhook.Services;

public sealed class DockerCliService
{
    private readonly ILogger<DockerCliService> _logger;

    public DockerCliService(ILogger<DockerCliService> logger)
    {
        _logger = logger;
    }

    public async Task RunServiceUpdateAsync(string serviceName, string image, bool force, CancellationToken ct)
    {
        var arguments = $"service update --with-registry-auth --image \"{image}\"{(force ? " --force" : string.Empty)} \"{serviceName}\"";

        _logger.LogInformation("Executing Docker CLI: docker {Arguments}", arguments);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout))
            _logger.LogInformation("Docker CLI output: {Output}", stdout.Trim());

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker CLI failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }
    }
}
