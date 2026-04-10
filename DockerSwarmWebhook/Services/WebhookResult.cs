namespace DockerSwarmWebhook.Services;

public sealed class WebhookResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public int StatusCode { get; init; }

    public static WebhookResult Success(string message) => new()
    {
        IsSuccess = true,
        Message = message,
        StatusCode = 200
    };

    public static WebhookResult NotFound(string webhookName) => new()
    {
        IsSuccess = false,
        Message = $"No service found with webhook name '{webhookName}'.",
        StatusCode = 404
    };
}
