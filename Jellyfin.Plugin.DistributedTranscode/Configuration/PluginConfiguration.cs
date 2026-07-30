using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DistributedTranscode.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string NodeName { get; set; } = Environment.MachineName;

    public int WorkerPort { get; set; } = 9090;

    public bool EnableDiscovery { get; set; } = true;

    public bool IsCoordinatorNode { get; set; } = true;

    public int MaxParallelTasks { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    public int ChunkSizeSeconds { get; set; } = 60;

    public int NodeTimeoutSeconds { get; set; } = 30;

    public int RequestTimeoutSeconds { get; set; } = 120;

    public int MaxRetryAttempts { get; set; } = 3;

    public string SharedSecret { get; set; } = string.Empty;

    public List<ConfiguredNode> KnownNodes { get; set; } = [];

    public TranscodeSettings TranscodeSettings { get; set; } = new();
}

public sealed class ConfiguredNode
{
    public string NodeId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public int Port { get; set; } = 9090;

    public bool Enabled { get; set; } = true;

    public int CpuCores { get; set; }

    public long AvailableMemoryBytes { get; set; }

    public bool SupportsHevcDecoding { get; set; } = true;

    public bool SupportsHevcEncoding { get; set; } = true;

    public bool SupportsHardwareAcceleration { get; set; }

    public string[] SupportedHardwareEncoders { get; set; } = [];
}

public sealed class TranscodeSettings
{
    public string VideoCodec { get; set; } = "libx264";

    public string AudioCodec { get; set; } = "aac";

    public string Preset { get; set; } = "medium";

    public int Crf { get; set; } = 23;

    public string PixelFormat { get; set; } = "yuv420p";

    public string Resolution { get; set; } = "1920x1080";

    public int VideoBitrateKbps { get; set; } = 5000;

    public bool PreferHardwareAcceleration { get; set; } = true;
}
