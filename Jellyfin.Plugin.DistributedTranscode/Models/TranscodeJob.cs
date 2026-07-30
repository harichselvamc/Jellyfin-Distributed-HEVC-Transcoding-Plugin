namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class TranscodeJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    public string MediaPath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public TimeSpan? TotalDuration { get; set; }

    public string VideoCodec { get; set; } = "libx264";

    public string AudioCodec { get; set; } = "aac";

    public string Preset { get; set; } = "medium";

    public int Crf { get; set; } = 23;

    public string Resolution { get; set; } = "1920x1080";

    public int VideoBitrateKbps { get; set; } = 5000;

    public bool PreferHardwareAcceleration { get; set; } = true;
}
