namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class NodeHeartbeat
{
    public string NodeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public int Port { get; set; }

    public NodeCapabilities Capabilities { get; set; } = new();

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
