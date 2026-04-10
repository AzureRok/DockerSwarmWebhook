using DockerSwarmWebhook.Services;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton<DockerSwarmService>();

// Register source-generated JSON context for Native AOT serialization.
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

// Allow SERVER_HOST / SERVER_PORT env vars for compatibility with the original swarm-webhook image.
var serverHost = Environment.GetEnvironmentVariable("SERVER_HOST") ?? "0.0.0.0";
var serverPort = Environment.GetEnvironmentVariable("SERVER_PORT") ?? "3000";

builder.WebHost.UseUrls($"http://{serverHost}:{serverPort}");

var app = builder.Build();

// ---------- Security key middleware ----------
// Supports Azure-style ?code=<key> query parameter or x-webhook-key header.
// When WEBHOOK_SECRET_KEY is not set, all requests are allowed.
var secretKey = app.Configuration["Webhook:SecretKey"];
if (string.IsNullOrEmpty(secretKey))
    secretKey = Environment.GetEnvironmentVariable("WEBHOOK_SECRET_KEY");

if (!string.IsNullOrEmpty(secretKey))
{
    app.Use(async (context, next) =>
    {
        var codeFromQuery = context.Request.Query["code"].FirstOrDefault();
        var codeFromHeader = context.Request.Headers["x-webhook-key"].FirstOrDefault();

        if (!string.Equals(codeFromQuery, secretKey, StringComparison.Ordinal)
            && !string.Equals(codeFromHeader, secretKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("Unauthorized. Provide a valid 'code' query parameter or 'x-webhook-key' header."),
                AppJsonContext.Default.ErrorResponse);
            return;
        }

        await next();
    });

    app.Logger.LogInformation("Webhook security key is configured. Requests require authentication.");
}
else
{
    app.Logger.LogWarning("No WEBHOOK_SECRET_KEY configured. All requests are allowed without authentication.");
}

// ---------- Endpoints ----------

// GET / — List all webhook-enabled services
app.MapGet("/", async (DockerSwarmService docker, CancellationToken ct) =>
{
    var services = await docker.ListEnabledServicesAsync(ct);
    return TypedResults.Ok(services);
});

// POST|GET /start/{name} — Scale service up to desired replicas
app.MapMethods("/start/{name}", ["GET", "POST"], async (string name, DockerSwarmService docker, CancellationToken ct) =>
{
    var result = await docker.StartServiceAsync(name, ct);
    return TypedResults.Json(new ApiResponse(result.Message), AppJsonContext.Default.ApiResponse, statusCode: result.StatusCode);
});

// POST|GET /stop/{name} — Scale service down to 0
app.MapMethods("/stop/{name}", ["GET", "POST"], async (string name, DockerSwarmService docker, CancellationToken ct) =>
{
    var result = await docker.StopServiceAsync(name, ct);
    return TypedResults.Json(new ApiResponse(result.Message), AppJsonContext.Default.ApiResponse, statusCode: result.StatusCode);
});

// POST|GET /restart/{name} — Force-restart with image re-pull (docker service update --force)
app.MapMethods("/restart/{name}", ["GET", "POST"], async (string name, DockerSwarmService docker, CancellationToken ct) =>
{
    var result = await docker.RestartServiceAsync(name, ct);
    return TypedResults.Json(new ApiResponse(result.Message), AppJsonContext.Default.ApiResponse, statusCode: result.StatusCode);
});

app.Run();
