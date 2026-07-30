namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class MediaProbeRequest
{
    public string SourcePath { get; set; } = string.Empty;
}

public sealed class MediaProbeResult
{
    public string SourcePath { get; set; } = string.Empty;

    public bool Success { get; set; }

    public double DurationSeconds { get; set; }

    public string? Error { get; set; }
}
