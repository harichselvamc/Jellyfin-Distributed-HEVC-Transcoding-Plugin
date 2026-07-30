namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class SegmentRequest
{
    public string JobId { get; set; } = string.Empty;

    public string ChunkId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public double StartTimeSeconds { get; set; }

    public double DurationSeconds { get; set; }

    public string VideoCodec { get; set; } = "libx264";

    public string AudioCodec { get; set; } = "aac";

    public string Preset { get; set; } = "medium";

    public int Crf { get; set; }

    public string Resolution { get; set; } = "1920x1080";

    public int VideoBitrateKbps { get; set; }

    public bool PreferHardwareAcceleration { get; set; }
}

public sealed class SegmentResult
{
    public string JobId { get; set; } = string.Empty;

    public string ChunkId { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Error { get; set; }
}

public sealed class ChunkInfo
{
    public string ChunkId { get; set; } = Guid.NewGuid().ToString("N");

    public double StartTimeSeconds { get; set; }

    public double DurationSeconds { get; set; }
}
