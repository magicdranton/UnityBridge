using System.Threading.Channels;
using UnityBridge.Configuration;

namespace UnityBridge.Jobs;

public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled, TimedOut }
public sealed record JobRequest(Guid Id, string LogPath);
public sealed record JobResult(Guid Id, string Type, JobStatus Status, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, int? ExitCode, bool? Success, int ErrorCount, string[] Errors, string LogPath);

public sealed class JobManager
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, JobResult> jobs = new();
    private readonly Channel<JobRequest> queue;
    private readonly BridgeSettings settings;
    public JobManager(BridgeSettings settings)
    {
        this.settings = settings;
        queue = Channel.CreateBounded<JobRequest>(new BoundedChannelOptions(settings.Bridge.MaxQueuedJobs)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }
    public ChannelReader<JobRequest> Reader => queue.Reader;
    public JobResult? Active { get { lock (gate) return jobs.Values.FirstOrDefault(j => j.Status == JobStatus.Running); } }
    public JobResult? Get(Guid id) { lock (gate) return jobs.GetValueOrDefault(id); }
    public JobResult? Enqueue()
    {
        lock (gate)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(settings.Unity.LogDirectory, $"{id}.log");
            var job = new JobResult(id, "compile", JobStatus.Queued, null, null, null, null, 0, [], path);
            jobs.Add(id, job);
            if (queue.Writer.TryWrite(new(id, path))) return job;
            jobs.Remove(id);
            return null;
        }
    }
    public void Start(Guid id) { lock (gate) jobs[id] = jobs[id] with { Status = JobStatus.Running, StartedAt = DateTimeOffset.UtcNow }; }
    public void Finish(Guid id, JobStatus status, int? exitCode, string[] errors)
    {
        lock (gate) jobs[id] = jobs[id] with { Status = status, FinishedAt = DateTimeOffset.UtcNow,
            ExitCode = exitCode, Success = status == JobStatus.Succeeded, Errors = errors, ErrorCount = errors.Length };
    }
}
