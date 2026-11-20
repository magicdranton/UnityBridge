using Microsoft.Extensions.Configuration;

namespace UnityBridge.Configuration;

public sealed class CBridgeOptions
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8765;
    public string ApiToken { get; set; } = "";
    public int MaxQueuedJobs { get; set; } = 3;
    public int MaxLogLines { get; set; } = 300;
    public int JobTimeoutMinutes { get; set; } = 30;
}

public sealed class CUnityOptions
{
    public string EditorPath { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string LogDirectory { get; set; } = "";
}

public sealed record CBridgeSettings
{
    public CBridgeOptions Bridge { get; init; }
    public CUnityOptions Unity { get; init; }

    public CBridgeSettings(CBridgeOptions p_bridge, CUnityOptions p_unity)
    {
        Bridge = p_bridge;
        Unity = p_unity;
    }

    public void Deconstruct(out CBridgeOptions p_bridge, out CUnityOptions p_unity)
    {
        p_bridge = Bridge;
        p_unity = Unity;
    }

    public bool UnityAvailable => File.Exists(Unity.EditorPath);
    public bool ProjectAvailable => Directory.Exists(Path.Combine(Unity.ProjectPath, "Assets"))
            && Directory.Exists(Path.Combine(Unity.ProjectPath, "ProjectSettings"));
    public static CBridgeSettings Load(IConfiguration p_config)
    {
        var v_bridge = p_config.GetSection("Bridge").Get<CBridgeOptions>() ?? new();

        var v_unity = p_config.GetSection("Unity").Get<CUnityOptions>() ?? new();

        // Validate the listen address, queue limits, credentials, and Unity paths.
        if (!System.Net.IPAddress.TryParse(v_bridge.Host, out _)
            || v_bridge.Port is < 1 or > 65535
            || v_bridge.MaxQueuedJobs is < 1 or > 3
            || v_bridge.MaxLogLines is < 1 or > 300
            || v_bridge.JobTimeoutMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException("Invalid Bridge limits or listen address.");
        }
        if (string.IsNullOrWhiteSpace(v_bridge.ApiToken)
            || v_bridge.ApiToken == "CHANGE_ME")
        {
            throw new InvalidOperationException("Set Bridge:ApiToken to a non-placeholder token (or use Bridge__ApiToken).");
        }

        if (new[] { v_unity.EditorPath, v_unity.ProjectPath, v_unity.LogDirectory }.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Unity paths must be configured.");
        }

        Directory.CreateDirectory(v_unity.LogDirectory);

        return new(v_bridge, v_unity);
    }
}
