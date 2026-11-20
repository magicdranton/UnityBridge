using System.Threading.Channels;
using UnityBridge.Configuration;

namespace UnityBridge.Jobs;

public enum EnumJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public sealed record CJobRequest
{
    public Guid Id { get; init; }
    public string LogPath { get; init; }

    public CJobRequest(Guid p_id, string p_logPath)
    {
        Id = p_id;
        LogPath = p_logPath;
    }

    public void Deconstruct(out Guid p_id, out string p_logPath)
    {
        p_id = Id;
        p_logPath = LogPath;
    }
}

public sealed record CJobResult
{
    public Guid Id { get; init; }
    public string Type { get; init; }
    public EnumJobStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int? ExitCode { get; init; }
    public bool? Success { get; init; }
    public int ErrorCount { get; init; }
    public string[] Errors { get; init; }
    public string LogPath { get; init; }

    public CJobResult(
        Guid p_id,
        string p_type,
        EnumJobStatus p_status,
        DateTimeOffset? p_startedAt,
        DateTimeOffset? p_finishedAt,
        int? p_exitCode,
        bool? p_success,
        int p_errorCount,
        string[] p_errors,
        string p_logPath)
    {
        Id = p_id;
        Type = p_type;
        Status = p_status;
        StartedAt = p_startedAt;
        FinishedAt = p_finishedAt;
        ExitCode = p_exitCode;
        Success = p_success;
        ErrorCount = p_errorCount;
        Errors = p_errors;
        LogPath = p_logPath;
    }

    public void Deconstruct(
        out Guid p_id,
        out string p_type,
        out EnumJobStatus p_status,
        out DateTimeOffset? p_startedAt,
        out DateTimeOffset? p_finishedAt,
        out int? p_exitCode,
        out bool? p_success,
        out int p_errorCount,
        out string[] p_errors,
        out string p_logPath)
    {
        p_id = Id;
        p_type = Type;
        p_status = Status;
        p_startedAt = StartedAt;
        p_finishedAt = FinishedAt;
        p_exitCode = ExitCode;
        p_success = Success;
        p_errorCount = ErrorCount;
        p_errors = Errors;
        p_logPath = LogPath;
    }
}

public sealed class CJobManager
{
    private readonly object m_gate = new();
    private readonly Dictionary<Guid, CJobResult> m_jobs = new();

    private readonly Channel<CJobRequest> m_queue;
    private readonly CBridgeSettings m_settings;

    public CJobManager(CBridgeSettings p_settings)
    {
        m_settings = p_settings;
        m_queue = Channel.CreateBounded<CJobRequest>(new BoundedChannelOptions(p_settings.Bridge.MaxQueuedJobs)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public ChannelReader<CJobRequest> Reader => m_queue.Reader;
    public CJobResult? Active
    {
        get
        {
            lock (m_gate)
            {
                return m_jobs.Values.FirstOrDefault(p_job => p_job.Status == EnumJobStatus.Running);
            }
        }
    }

    public CJobResult? Get(Guid p_id)
    {
        lock (m_gate)
        {
            return m_jobs.GetValueOrDefault(p_id);
        }
    }

    public CJobResult? Enqueue()
    {
        // Add the result and queue request together under the same lock.
        lock (m_gate)
        {
            var v_id = Guid.NewGuid();

            var v_path = Path.Combine(m_settings.Unity.LogDirectory, $"{v_id}.log");

            var v_job = new CJobResult(v_id, "compile", EnumJobStatus.Queued, null, null, null, null, 0, [], v_path);

            m_jobs.Add(v_id, v_job);

            if (m_queue.Writer.TryWrite(new(v_id, v_path)))
            {
                return v_job;
            }

            m_jobs.Remove(v_id);

            return null;
        }
    }

    public void Start(Guid p_id)
    {
        lock (m_gate)
        {
            m_jobs[p_id] = m_jobs[p_id] with
            {
                Status = EnumJobStatus.Running,
                StartedAt = DateTimeOffset.UtcNow
            };
        }
    }

    // Publish the terminal state and compilation diagnostics atomically.
    public void Finish(Guid p_id, EnumJobStatus p_status, int? p_exitCode, string[] p_errors)
    {
        lock (m_gate)
        {
            m_jobs[p_id] = m_jobs[p_id] with
            {
                Status = p_status,
                FinishedAt = DateTimeOffset.UtcNow,
                ExitCode = p_exitCode,
                Success = p_status == EnumJobStatus.Succeeded,
                Errors = p_errors,
                ErrorCount = p_errors.Length
            };
        }
    }
}
