using Jellyfin.Plugin.DistributedTranscode.Models;

namespace Jellyfin.Plugin.DistributedTranscode.Services;

public sealed class TranscodeJobManager
{
    private readonly Dictionary<string, TranscodeJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<JobStatus> _recentStatuses = [];
    private readonly object _sync = new();

    public void AddOrUpdate(TranscodeJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (_sync)
        {
            _jobs[job.JobId] = job;
        }
    }

    public bool TryGet(string jobId, out TranscodeJob? job)
    {
        lock (_sync)
        {
            return _jobs.TryGetValue(jobId, out job);
        }
    }

    public bool Remove(string jobId)
    {
        lock (_sync)
        {
            return _jobs.Remove(jobId);
        }
    }

    public void StartStatus(JobStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_sync)
        {
            _recentStatuses.RemoveAll(existing => string.Equals(existing.JobId, status.JobId, StringComparison.OrdinalIgnoreCase));
            _recentStatuses.Insert(0, status);

            if (_recentStatuses.Count > 25)
            {
                _recentStatuses.RemoveRange(25, _recentStatuses.Count - 25);
            }
        }
    }

    public void FinishStatus(string jobId, bool success, string? error = null)
    {
        lock (_sync)
        {
            var status = _recentStatuses.FirstOrDefault(existing => string.Equals(existing.JobId, jobId, StringComparison.OrdinalIgnoreCase));
            if (status is null)
            {
                return;
            }

            status.State = success ? "completed" : "failed";
            status.Progress = success ? 100 : status.Progress;
            status.FinishedUtc = DateTime.UtcNow;
            status.Error = error;
        }
    }

    public void UpdateStatus(string jobId, string state, double progress, string? nodeId = null, string? outputPath = null, string? error = null)
    {
        lock (_sync)
        {
            var status = _recentStatuses.FirstOrDefault(existing => string.Equals(existing.JobId, jobId, StringComparison.OrdinalIgnoreCase));
            if (status is null)
            {
                return;
            }

            status.State = state;
            status.Progress = Math.Max(0, Math.Min(100, progress));
            status.NodeId = string.IsNullOrWhiteSpace(nodeId) ? status.NodeId : nodeId;
            status.OutputPath = string.IsNullOrWhiteSpace(outputPath) ? status.OutputPath : outputPath;
            status.Error = error;
        }
    }

    public IReadOnlyCollection<JobStatus> GetRecentStatuses()
    {
        lock (_sync)
        {
            return _recentStatuses
                .Select(status => new JobStatus
                {
                    JobId = status.JobId,
                    Kind = status.Kind,
                    NodeId = status.NodeId,
                    SourcePath = status.SourcePath,
                    OutputPath = status.OutputPath,
                    State = status.State,
                    Progress = status.Progress,
                    StartedUtc = status.StartedUtc,
                    FinishedUtc = status.FinishedUtc,
                    Error = status.Error,
                })
                .ToArray();
        }
    }
}
