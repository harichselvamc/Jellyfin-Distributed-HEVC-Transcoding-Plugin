namespace Jellyfin.Plugin.DistributedTranscode.Models;

public sealed class JobStatus
{
    public string JobId { get; set; } = string.Empty;

    public string Kind { get; set; } = "test";

    public string NodeId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public string State { get; set; } = "queued";

    public double Progress { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; set; }

    public string? Error { get; set; }
}

public sealed class DistributedTranscodeSummary
{
    public string Status { get; set; } = "healthy";

    public int KnownNodes { get; set; }

    public IReadOnlyCollection<NodeInfo> Nodes { get; set; } = [];

    public IReadOnlyCollection<JobStatus> RecentJobs { get; set; } = [];
}
