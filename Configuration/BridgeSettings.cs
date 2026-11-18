using Microsoft.Extensions.Configuration;

namespace UnityBridge.Configuration;

public sealed class BridgeOptions
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8765;
    public string ApiToken { get; set; } = "";
    public int MaxQueuedJobs { get; set; } = 3;
    public int MaxLogLines { get; set; } = 300;
    public int JobTimeoutMinutes { get; set; } = 30;
}
public sealed class UnityOptions
{
    public string EditorPath { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string LogDirectory { get; set; } = "";
}
public sealed record BridgeSettings(BridgeOptions Bridge, UnityOptions Unity)
{
    public bool UnityAvailable => File.Exists(Unity.EditorPath);
    public bool ProjectAvailable => Directory.Exists(Path.Combine(Unity.ProjectPath, "Assets")) 
            && Directory.Exists(Path.Combine(Unity.ProjectPath, "ProjectSettings"));
    public static BridgeSettings Load(IConfiguration config)
    {
        var b = config.GetSection("Bridge").Get<BridgeOptions>() ?? new();

        var u = config.GetSection("Unity").Get<UnityOptions>() ?? new();

        if (!System.Net.IPAddress.TryParse(b.Host, out _) || b.Port is < 1 or > 65535 ||
            b.MaxQueuedJobs is < 1 or > 3 || b.MaxLogLines is < 1 or > 300 || b.JobTimeoutMinutes is < 1 or > 1440)
                throw new InvalidOperationException("Invalid Bridge limits or listen address.");
        if (string.IsNullOrWhiteSpace(b.ApiToken) || b.ApiToken == "CHANGE_ME")
                throw new InvalidOperationException("Set Bridge:ApiToken to a non-placeholder token (or use Bridge__ApiToken).");

        if (new[] { u.EditorPath, u.ProjectPath, u.LogDirectory }.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Unity paths must be configured.");

        Directory.CreateDirectory(u.LogDirectory);

        return new(b, u);
    }
}
