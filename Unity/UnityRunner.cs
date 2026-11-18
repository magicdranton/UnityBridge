using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UnityBridge.Configuration;
using UnityBridge.Jobs;

namespace UnityBridge.Unity;

public sealed class UnityRunner(BridgeSettings settings, ILogger<UnityRunner> logger)
{
    public async Task<(int ExitCode, string[] Errors)> Compile(JobRequest job, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(settings.Unity.EditorPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-batchmode", "-nographics", "-quit", "-projectPath", settings.Unity.ProjectPath, "-logFile", job.LogPath })
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Unity process did not start.");
        logger.LogInformation("Job {JobId}: compile started, PID {Pid}, log {LogPath}", job.Id, process.Id, job.LogPath);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var errors = new List<string>();
        if (!File.Exists(job.LogPath)) errors.Add("Unity did not produce a compilation log.");
        else
        {
            using var reader = new StreamReader(job.LogPath);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                if (Regex.IsMatch(line, @"error\s+CS\d+|Compilation failed|Scripts have compiler errors|Aborting batchmode due to failure|Failed to (activate|update) license|No valid Unity Editor license|another Unity instance", RegexOptions.IgnoreCase))
                    errors.Add(line);
        }
        if (process.ExitCode != 0) errors.Add($"Unity exited with code {process.ExitCode}.");
        return (process.ExitCode, errors.Distinct().ToArray());
    }
}
