using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnityBridge.Configuration;
using UnityBridge.Jobs;
using UnityBridge.Unity;

namespace UnityBridge.Worker;

public sealed class BridgeWorker(JobManager jobs, UnityRunner runner, BridgeSettings settings, ILogger<BridgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Unity available: {Unity}; project available: {Project}", settings.UnityAvailable, settings.ProjectAvailable);
        try
        {
            await foreach (var job in jobs.Reader.ReadAllAsync(stoppingToken))
            {
                jobs.Start(job.Id);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(settings.Bridge.JobTimeoutMinutes));
                try
                {
                    var result = await runner.Compile(job, timeout.Token);
                    jobs.Finish(job.Id, result.Errors.Length == 0 ? JobStatus.Succeeded : JobStatus.Failed, result.ExitCode, result.Errors);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                { jobs.Finish(job.Id, stoppingToken.IsCancellationRequested ? JobStatus.Cancelled : JobStatus.TimedOut, null, ["Compilation cancelled or timed out."]); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Job {JobId} failed", job.Id);
                    jobs.Finish(job.Id, JobStatus.Failed, null, ["Unity could not complete compilation. Check Bridge logs."]);
                }
                var finished = jobs.Get(job.Id)!;
                logger.LogInformation("Job {JobId}: {Status}, exit {ExitCode}, duration {Duration}, errors {Errors}", job.Id, finished.Status, finished.ExitCode, finished.FinishedAt - finished.StartedAt, finished.ErrorCount);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
