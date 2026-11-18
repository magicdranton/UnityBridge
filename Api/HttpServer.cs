using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UnityBridge.Configuration;
using UnityBridge.Jobs;

namespace UnityBridge.Api;

public sealed class HttpServer(BridgeSettings settings, JobManager jobs) : IHostedService
{
    private WebApplication? app;
    private static IResult Error(int status, string code, string message) => Results.Json(new { error = new { code, message } }, statusCode: status);
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(o => 
        { 
            o.Listen(System.Net.IPAddress.Parse(settings.Bridge.Host), settings.Bridge.Port); 
            o.Limits.MaxRequestBodySize = 4096; 
        });

        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        app = builder.Build();

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Bridge.ApiToken));

        app.Use(async (context, next) =>
        {
            try
            {
                if (context.Request.Path != "/health")
                {
                    var header = context.Request.Headers.Authorization.ToString();

                    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                        !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
                    { 
                        await Error(401, "UNAUTHORIZED", "A valid bearer token is required.").ExecuteAsync(context); 
                        return; 
                    }
                }
                await next(context);
            }
            catch (BadHttpRequestException ex) 
            { 
                await Error(ex.StatusCode, "INVALID_REQUEST", "Invalid request or body too large.").ExecuteAsync(context); 
            }
            catch (Exception) when (!context.Response.HasStarted) 
            { 
                await Error(500, "INTERNAL_ERROR", "The request could not be completed.").ExecuteAsync(context); 
            }
        });
        
        app.MapGet("/health", () =>
        {
            Results.Ok(new
            {
                status = "ok",
                unityAvailable = settings.UnityAvailable,
                projectAvailable = settings.ProjectAvailable,
                activeJob = jobs.Active is { } j ? new { j.Id, j.Type } : null
            });
        });        

        app.MapPost("/api/v1/jobs/compile", async (HttpRequest request) =>
        {
            // Only an empty JSON object is accepted; no client paths or arguments.
            try
            {
                using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);

                if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Any())
                    return Error(400, "INVALID_REQUEST", "Expected an empty JSON object.");
            }
            catch (JsonException) 
            { 
                return Error(400, "INVALID_REQUEST", "Expected an empty JSON object."); 
            }

            if (!settings.UnityAvailable) return Error(503, "UNITY_NOT_FOUND", "Unity Editor executable was not found.");

            if (!settings.ProjectAvailable) return Error(503, "PROJECT_NOT_FOUND", "Configured Unity project was not found.");

            var job = jobs.Enqueue();

            return job is null ? Error(429, "QUEUE_FULL", "The job queue is full.") : Results.Accepted($"/api/v1/jobs/{job.Id}", 
                new 
                { 
                    jobId = job.Id, 
                    status = job.Status, 
                    statusUrl = $"/api/v1/jobs/{job.Id}", 
                    logsUrl = $"/api/v1/jobs/{job.Id}/logs" 
                });
        });

        app.MapGet("/api/v1/jobs/{id}", (string id) =>
            Guid.TryParse(id, out var guid) && jobs.Get(guid) is { } job ? Results.Ok(job) : Error(404, "JOB_NOT_FOUND", "Job was not found."));

        app.MapGet("/api/v1/jobs/{id}/logs", async (string id, HttpRequest request) =>
        {
            if (!Guid.TryParse(id, out var guid) || jobs.Get(guid) is not { } job) return Error(404, "JOB_NOT_FOUND", "Job was not found.");

            var count = Math.Min(100, settings.Bridge.MaxLogLines);

            if (request.Query.TryGetValue("lines", out var lines) 
                && (!int.TryParse(lines, out count) || count < 1 || count > settings.Bridge.MaxLogLines))
                    return Error(400, "INVALID_LINES", $"lines must be between 1 and {settings.Bridge.MaxLogLines}.");
            var tail = new Queue<string>();

            var truncated = false;

            if (File.Exists(job.LogPath))
            {
                using var stream = new FileStream(job.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                while (await reader.ReadLineAsync(request.HttpContext.RequestAborted) is { } line)
                { 
                    tail.Enqueue(line); 
                    
                    if (tail.Count > count) 
                    { 
                        tail.Dequeue(); truncated = true; 
                    } 
                }
            }

            return Results.Ok(new 
                { 
                    jobId = job.Id, 
                    lines = tail.ToArray(), 
                    truncated 
                });
        });

        app.MapFallback(() => Error(404, "NOT_FOUND", "Endpoint was not found."));

        await app.StartAsync(cancellationToken);
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    { 
        if (app is not null) 
        { 
            await app.StopAsync(cancellationToken); 
            await app.DisposeAsync(); 
        } 
    }
}
