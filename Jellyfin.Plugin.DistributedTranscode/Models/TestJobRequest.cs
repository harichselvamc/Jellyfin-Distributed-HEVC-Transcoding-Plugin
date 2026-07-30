namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class TestJobRequest
{
    public string? NodeId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public double StartTimeSeconds { get; set; } = 0;

    public double DurationSeconds { get; set; } = 3;

    public string VideoCodec { get; set; } = "libx264";

    public string AudioCodec { get; set; } = "aac";

    public string Preset { get; set; } = "veryfast";

    public int Crf { get; set; } = 28;

    public string Resolution { get; set; } = "640x360";

    public int VideoBitrateKbps { get; set; } = 1200;

    public bool PreferHardwareAcceleration { get; set; }
}
