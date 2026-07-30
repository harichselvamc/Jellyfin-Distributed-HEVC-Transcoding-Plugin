using Jellyfin.Plugin.DistributedTranscode.Models;

namespace Jellyfin.Plugin.DistributedTranscode.Services;

public sealed class JobDistributor
{
    public IReadOnlyList<(ChunkInfo Chunk, NodeInfo Node)> AssignChunks(
        IReadOnlyList<ChunkInfo> chunks,
        IReadOnlyList<NodeInfo> nodes)
    {
        if (chunks.Count == 0)
        {
            return [];
        }

        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("No worker nodes are currently available.");
        }

        var orderedNodes = nodes
            .OrderBy(node => node.ActiveJobs)
            .ThenByDescending(node => node.Capabilities.SupportsHardwareAcceleration)
            .ThenByDescending(node => node.Capabilities.CpuCores)
            .ToArray();

        var assignments = new List<(ChunkInfo, NodeInfo)>(chunks.Count);

        for (var index = 0; index < chunks.Count; index++)
        {
            assignments.Add((chunks[index], orderedNodes[index % orderedNodes.Length]));
        }

        return assignments;
    }
}
