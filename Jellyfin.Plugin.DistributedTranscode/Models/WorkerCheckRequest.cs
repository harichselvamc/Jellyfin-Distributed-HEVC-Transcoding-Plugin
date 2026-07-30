namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class WorkerCheckRequest
{
    public string Address { get; set; } = string.Empty;

    public int Port { get; set; } = 9090;
}
