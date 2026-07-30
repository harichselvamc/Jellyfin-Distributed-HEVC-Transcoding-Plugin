namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class NodeRegistrationRequest
{
    public string NodeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public int Port { get; set; } = 9090;

    public int CpuCores { get; set; }

    public long AvailableMemoryBytes { get; set; }

    public bool SupportsHevcDecoding { get; set; } = true;

    public bool SupportsHevcEncoding { get; set; } = true;

    public bool SupportsHardwareAcceleration { get; set; }

    public string[] SupportedHardwareEncoders { get; set; } = [];
}
