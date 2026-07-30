namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class NodeInfo
{
    public string NodeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public int Port { get; set; }

    public NodeCapabilities Capabilities { get; set; } = new();

    public int ActiveJobs { get; set; }

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class NodeCapabilities
{
    public int CpuCores { get; set; }

    public long AvailableMemoryBytes { get; set; }

    public bool SupportsHevcDecoding { get; set; }

    public bool SupportsHevcEncoding { get; set; }

    public bool SupportsHardwareAcceleration { get; set; }

    public string[] SupportedHardwareEncoders { get; set; } = [];
}
