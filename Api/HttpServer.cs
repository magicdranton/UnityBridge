using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnityBridge.Configuration;
using UnityBridge.Jobs;

namespace UnityBridge.Api;

public sealed class CHttpServer : IHostedService
{
    private readonly CBridgeSettings m_settings;
    private readonly CJobManager m_jobs;
    private WebApplication? m_app;
    private ILogger<CHttpServer> m_Logger;

    public CHttpServer(CBridgeSettings p_settings, CJobManager p_jobs, ILogger<CHttpServer> p_Logger)
    {
        m_settings = p_settings;
        m_jobs = p_jobs;
        m_Logger = p_Logger;
    }

    private static IResult Error(int p_status, string p_code, string p_message) => Results.Json(new
    {
        error = new
        {
            code = p_code,
            message = p_message
        }
    }, statusCode: p_status);
    public async Task StartAsync(CancellationToken p_cancellationToken)
    {
        var v_builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = []
        });
        v_builder.WebHost.ConfigureKestrel(p_options =>
        {
            p_options.Listen(System.Net.IPAddress.Parse(m_settings.Bridge.Host), m_settings.Bridge.Port);
            p_options.Limits.MaxRequestBodySize = 4096;

            m_Logger.LogInformation("HttpServer: listening on {Host}:{Port}", 
                m_settings.Bridge.Host, m_settings.Bridge.Port);
        });

        v_builder.Services.ConfigureHttpJsonOptions(p_options => p_options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        m_app = v_builder.Build();

        var v_expected = SHA256.HashData(Encoding.UTF8.GetBytes(m_settings.Bridge.ApiToken));

        // Authenticate all endpoints except the public health check.
        m_app.Use(async (p_context, p_next) =>
        {
            try
            {
                await LogRequestAsync(p_context.Request);

                if (p_context.Request.Path != "/health")
                {
                    var v_header = p_context.Request.Headers.Authorization.ToString();

                    if (!v_header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        || !CryptographicOperations.FixedTimeEquals(v_expected, SHA256.HashData(Encoding.UTF8.GetBytes(v_header[7..]))))
                    {
                        await Error(401, "UNAUTHORIZED", "A valid bearer token is required.").ExecuteAsync(p_context);
                        return;
                    }
                }
                await p_next(p_context);
            }
            catch (BadHttpRequestException v_ex)
            {
                await Error(v_ex.StatusCode, "INVALID_REQUEST", "Invalid request or body too large.").ExecuteAsync(p_context);
            }
            catch (Exception) when (!p_context.Response.HasStarted)
            {
                await Error(500, "INTERNAL_ERROR", "The request could not be completed.").ExecuteAsync(p_context);
            }
        });

        m_app.MapGet("/health", () =>
        {
            return Results.Ok(new
            {
                status = "ok",
                unityAvailable = m_settings.UnityAvailable,
                projectAvailable = m_settings.ProjectAvailable,
                activeJob = m_jobs.Active is { } v_activeJob ? new
                {
                    v_activeJob.Id,
                    v_activeJob.Type
                } : null
            });
        });

        // Return a consistent job snapshot; authentication is enforced by the middleware.
        m_app.MapGet("/api/v1/status", () =>
        {
            var v_snapshot = m_jobs.GetStatus();

            return Results.Ok(new
            {
                bridgeVersion = typeof(CHttpServer).Assembly.GetName().Version?.ToString(3),
                unityVersion = GetUnityVersion(),
                unityAvailable = m_settings.UnityAvailable,
                projectAvailable = m_settings.ProjectAvailable,
                activeJob = v_snapshot.ActiveJob is { } v_activeJob ? new
                {
                    v_activeJob.Id,
                    v_activeJob.Type
                } : null,
                queuedJobs = v_snapshot.QueuedJobs,
                completedJobs = v_snapshot.CompletedJobs,
                failedJobs = v_snapshot.FailedJobs
            });
        });

        m_app.MapPost("/api/v1/jobs/compile", async (HttpRequest p_request) =>
        {
            // Only an empty JSON object is accepted; no client paths or arguments.
            try
            {
                using var v_body = await JsonDocument.ParseAsync(p_request.Body, cancellationToken: p_request.HttpContext.RequestAborted);

                if (v_body.RootElement.ValueKind != JsonValueKind.Object
                    || v_body.RootElement.EnumerateObject().Any())
                {
                    return Error(400, "INVALID_REQUEST", "Expected an empty JSON object.");
                }
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_REQUEST", "Expected an empty JSON object.");
            }

            if (!m_settings.UnityAvailable)
            {
                return Error(503, "UNITY_NOT_FOUND", "Unity Editor executable was not found.");
            }

            if (!m_settings.ProjectAvailable)
            {
                return Error(503, "PROJECT_NOT_FOUND", "Configured Unity project was not found.");
            }

            var v_job = m_jobs.Enqueue();

            return v_job is null ? Error(429, "QUEUE_FULL", "The job queue is full.") : Results.Accepted($"/api/v1/jobs/{v_job.Id}",
                new
                {
                    jobId = v_job.Id,
                    status = v_job.Status,
                    statusUrl = $"/api/v1/jobs/{v_job.Id}",
                    logsUrl = $"/api/v1/jobs/{v_job.Id}/logs"
                });
        });

        m_app.MapGet("/api/v1/jobs/{id}", ([Microsoft.AspNetCore.Mvc.FromRoute(Name = "id")] string p_id) =>
        {
            return Guid.TryParse(p_id, out var v_guid)
                && m_jobs.Get(v_guid) is { } v_job
                    ? Results.Ok(v_job)
                    : Error(404, "JOB_NOT_FOUND", "Job was not found.");
        });

        m_app.MapGet("/api/v1/jobs/{id}/logs", async ([Microsoft.AspNetCore.Mvc.FromRoute(Name = "id")] string p_id, HttpRequest p_request) =>
        {
            if (!Guid.TryParse(p_id, out var v_guid)
                || m_jobs.Get(v_guid) is not { } v_job)
            {
                return Error(404, "JOB_NOT_FOUND", "Job was not found.");
            }

            var v_count = Math.Min(100, m_settings.Bridge.MaxLogLines);

            if (p_request.Query.TryGetValue("lines", out var v_lines)
                && (!int.TryParse(v_lines, out v_count)
                    || v_count < 1
                    || v_count > m_settings.Bridge.MaxLogLines))
            {
                return Error(400, "INVALID_LINES", $"lines must be between 1 and {m_settings.Bridge.MaxLogLines}.");
            }
            // Retain only the requested tail while reading the log.
            var v_tail = new Queue<string>();

            var v_truncated = false;

            if (File.Exists(v_job.LogPath))
            {
                using var v_stream = new FileStream(v_job.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var v_reader = new StreamReader(v_stream);

                while (await v_reader.ReadLineAsync(p_request.HttpContext.RequestAborted) is { } v_line)
                {
                    v_tail.Enqueue(v_line);

                    if (v_tail.Count > v_count)
                    {
                        v_tail.Dequeue();
                        v_truncated = true;
                    }
                }
            }

            return Results.Ok(new
            {
                jobId = v_job.Id,
                lines = v_tail.ToArray(),
                truncated = v_truncated
            });
        });

        m_app.MapFallback(() => Error(404, "NOT_FOUND", "Endpoint was not found."));

        await m_app.StartAsync(p_cancellationToken);
    }

    private async Task LogRequestAsync(HttpRequest p_request)
    {
        var v_bodyPreview = "";

        // Buffer and rewind the body so endpoint handlers can still read the full request.
        p_request.EnableBuffering();
        var v_position = p_request.Body.Position;

        try
        {
            using var v_reader = new StreamReader(p_request.Body, Encoding.UTF8, leaveOpen: true);
            var v_buffer = new char[50];
            var v_length = await v_reader.ReadBlockAsync(v_buffer.AsMemory(), p_request.HttpContext.RequestAborted);
            v_bodyPreview = new string(v_buffer, 0, v_length);
        }
        finally
        {
            p_request.Body.Position = v_position;

            if (v_bodyPreview.Length > 0)
            {
                m_Logger.LogInformation("HttpServer: {Method} {Url}; Body: {Body}",
                    p_request.Method, p_request.GetEncodedPathAndQuery(), v_bodyPreview);
            }
            else
            {
                m_Logger.LogInformation("HttpServer: {Method} {Url}",
                    p_request.Method, p_request.GetEncodedPathAndQuery());
            }
        }
    }

    private string? GetUnityVersion()
    {
        // Read the configured Editor version without starting a Unity process.
        try
        {
            return m_settings.UnityAvailable
                ? FileVersionInfo.GetVersionInfo(m_settings.Unity.EditorPath).ProductVersion
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task StopAsync(CancellationToken p_cancellationToken)
    {
        if (m_app is not null)
        {
            await m_app.StopAsync(p_cancellationToken);
            await m_app.DisposeAsync();
        }
    }
}
