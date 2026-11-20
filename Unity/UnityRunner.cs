using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UnityBridge.Configuration;
using UnityBridge.Jobs;

namespace UnityBridge.Unity;

public sealed class CUnityRunner
{
    private readonly CBridgeSettings m_settings;
    private readonly ILogger<CUnityRunner> m_logger;

    public CUnityRunner(CBridgeSettings p_settings, ILogger<CUnityRunner> p_logger)
    {
        m_settings = p_settings;
        m_logger = p_logger;
    }

    public async Task<(int ExitCode, string[] Errors)> Compile(CJobRequest p_job, CancellationToken p_cancellationToken)
    {
        // Start Unity in batch mode with the configured project and job log.
        var v_info = new ProcessStartInfo(m_settings.Unity.EditorPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var v_argument in new[]
        {
            "-batchmode",
            "-nographics",
            "-quit",
            "-projectPath",
            m_settings.Unity.ProjectPath,
            "-logFile",
            p_job.LogPath
        })
        {
            v_info.ArgumentList.Add(v_argument);
        }

        using var v_process = new Process
        {
            StartInfo = v_info
        };

        p_cancellationToken.ThrowIfCancellationRequested();

        if (!v_process.Start())
        {
            throw new InvalidOperationException("Unity process did not start.");
        }

        m_logger.LogInformation("Job {JobId}: compile started, PID {Pid}, log {LogPath}", p_job.Id, v_process.Id, p_job.LogPath);

        try
        {
            await v_process.WaitForExitAsync(p_cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stop the entire process tree before propagating cancellation.
            if (!v_process.HasExited)
            {
                v_process.Kill(entireProcessTree: true);
            }

            await v_process.WaitForExitAsync(CancellationToken.None);

            throw;
        }

        // Collect compilation and licensing failures from the Unity log.
        var v_errors = new List<string>();

        if (!File.Exists(p_job.LogPath))
        {
            v_errors.Add("Unity did not produce a compilation log.");
        }
        else
        {
            using var v_reader = new StreamReader(p_job.LogPath);

            while (await v_reader.ReadLineAsync(p_cancellationToken) is { } v_line)
            {
                if (Regex.IsMatch(
                    v_line,
                    @"error\s+CS\d+|Compilation failed|Scripts have compiler errors|Aborting batchmode due to failure|Failed to (activate|update) license|No valid Unity Editor license|another Unity instance",
                    RegexOptions.IgnoreCase))
                {
                    v_errors.Add(v_line);
                }
            }
        }
        if (v_process.ExitCode != 0)
        {
            v_errors.Add($"Unity exited with code {v_process.ExitCode}.");
        }

        return (v_process.ExitCode, v_errors.Distinct().ToArray());
    }
}
