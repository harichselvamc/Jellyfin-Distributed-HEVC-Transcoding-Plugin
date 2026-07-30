namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class FullTranscodeRequest
{
    public string? NodeId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public string WorkerOutputDirectory { get; set; } = string.Empty;

    public double? TotalDurationSeconds { get; set; }

    public int? ChunkSizeSeconds { get; set; }

    public string VideoCodec { get; set; } = "libx264";

    public string AudioCodec { get; set; } = "aac";

    public string Preset { get; set; } = "veryfast";

    public int Crf { get; set; } = 28;

    public string Resolution { get; set; } = "640x360";

    public int VideoBitrateKbps { get; set; } = 1200;

    public bool PreferHardwareAcceleration { get; set; }
}

public sealed class FullTranscodeResult
{
    public string JobId { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public bool Success { get; set; }

    public double DurationSeconds { get; set; }

    public int ChunkCount { get; set; }

    public string? Error { get; set; }
}
