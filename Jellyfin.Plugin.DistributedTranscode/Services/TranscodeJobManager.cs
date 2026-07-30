using Jellyfin.Plugin.DistributedTranscode.Models;

namespace Jellyfin.Plugin.DistributedTranscode.Services;

public sealed class TranscodeJobManager
{
    private readonly Dictionary<string, TranscodeJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
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
}
