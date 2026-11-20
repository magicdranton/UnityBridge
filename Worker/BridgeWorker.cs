using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnityBridge.Configuration;
using UnityBridge.Jobs;
using UnityBridge.Unity;

namespace UnityBridge.Worker;

public sealed class CBridgeWorker : BackgroundService
{
    private readonly CJobManager m_jobs;
    private readonly CUnityRunner m_runner;
    private readonly CBridgeSettings m_settings;
    private readonly ILogger<CBridgeWorker> m_logger;

    public CBridgeWorker(CJobManager p_jobs, CUnityRunner p_runner, CBridgeSettings p_settings, ILogger<CBridgeWorker> p_logger)
    {
        m_jobs = p_jobs;
        m_runner = p_runner;
        m_settings = p_settings;
        m_logger = p_logger;
    }

    protected override async Task ExecuteAsync(CancellationToken p_stoppingToken)
    {
        m_logger.LogInformation("Unity available: {Unity}; project available: {Project}", m_settings.UnityAvailable, m_settings.ProjectAvailable);

        try
        {
            // Process queued jobs sequentially with an individual timeout.
            await foreach (var v_job in m_jobs.Reader.ReadAllAsync(p_stoppingToken))
            {
                m_jobs.Start(v_job.Id);

                using var v_timeout = CancellationTokenSource.CreateLinkedTokenSource(p_stoppingToken);

                v_timeout.CancelAfter(TimeSpan.FromMinutes(m_settings.Bridge.JobTimeoutMinutes));
                try
                {
                    var v_result = await m_runner.Compile(v_job, v_timeout.Token);
                    m_jobs.Finish(v_job.Id, v_result.Errors.Length == 0 ? EnumJobStatus.Succeeded : EnumJobStatus.Failed, v_result.ExitCode, v_result.Errors);
                }
                catch (OperationCanceledException) when (v_timeout.IsCancellationRequested)
                {
                    m_jobs.Finish(v_job.Id, p_stoppingToken.IsCancellationRequested ? EnumJobStatus.Cancelled : EnumJobStatus.TimedOut, null,
                        ["Compilation cancelled or timed out."]);
                }
                catch (Exception v_ex)
                {
                    m_logger.LogError(v_ex, "Job {JobId} failed", v_job.Id);
                    m_jobs.Finish(v_job.Id, EnumJobStatus.Failed, null, ["Unity could not complete compilation. Check Bridge logs."]);
                }
                var v_finished = m_jobs.Get(v_job.Id)!;

                m_logger.LogInformation("Job {JobId}: {Status}, exit {ExitCode}, duration {Duration}, errors {Errors}", v_job.Id,
                    v_finished.Status, v_finished.ExitCode, v_finished.FinishedAt - v_finished.StartedAt, v_finished.ErrorCount);
            }
        }
        catch (OperationCanceledException) when (p_stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the pending queue read.
        }
    }
}
