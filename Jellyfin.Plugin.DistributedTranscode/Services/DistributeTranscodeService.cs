using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.DistributedTranscode.Configuration;
using Jellyfin.Plugin.DistributedTranscode.Models;
using Jellyfin.Plugin.DistributedTranscode.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscode.Services;

public sealed class DistributeTranscodeService
{
    private readonly PluginConfiguration _configuration;
    private readonly JobDistributor _jobDistributor;
    private readonly ILogger<DistributeTranscodeService> _logger;
    private readonly MeshNodeService _meshNodeService;
    private readonly TranscodeJobManager _transcodeJobManager;

    public DistributeTranscodeService(
        PluginConfiguration configuration,
        MeshNodeService meshNodeService,
        JobDistributor jobDistributor,
        TranscodeJobManager transcodeJobManager,
        ILogger<DistributeTranscodeService> logger)
    {
        _configuration = configuration;
        _meshNodeService = meshNodeService;
        _jobDistributor = jobDistributor;
        _transcodeJobManager = transcodeJobManager;
        _logger = logger;
    }

    public void Initialize()
    {
        _logger.LogInformation("Distributed transcoding service initialized.");
    }

    public async Task<string> DistributeTranscodeAsync(TranscodeJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        _transcodeJobManager.AddOrUpdate(job);

        var duration = job.TotalDuration ?? TimeSpan.FromMinutes(10);
        var chunks = SplitIntoChunks(duration, _configuration.ChunkSizeSeconds);
        var availableNodes = _meshNodeService
            .GetConnectedNodes()
            .Where(node => node.Capabilities.SupportsHevcEncoding || node.Capabilities.SupportsHardwareAcceleration)
            .ToArray();

        if (availableNodes.Length == 0)
        {
            throw new InvalidOperationException("No worker nodes available for distributed transcoding.");
        }

        var assignments = _jobDistributor.AssignChunks(chunks, availableNodes);
        var results = new List<SegmentResult>(assignments.Count);
        var failures = new List<SegmentResult>();

        foreach (var assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SendChunkToNodeAsync(job, assignment.Node, assignment.Chunk, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                results.Add(result);
            }
            else
            {
                failures.Add(result);
            }
        }

        if (failures.Count > 0)
        {
            var firstFailure = failures[0];
            throw new InvalidOperationException($"Chunk {firstFailure.ChunkId} failed: {firstFailure.Error}");
        }

        return await CombineSegmentsAsync(job, results, cancellationToken).ConfigureAwait(false);
    }

    private static List<ChunkInfo> SplitIntoChunks(TimeSpan totalDuration, int chunkSizeSeconds)
    {
        var chunks = new List<ChunkInfo>();
        var totalSeconds = totalDuration.TotalSeconds;

        for (double start = 0; start < totalSeconds; start += chunkSizeSeconds)
        {
            chunks.Add(new ChunkInfo
            {
                StartTimeSeconds = start,
                DurationSeconds = Math.Min(chunkSizeSeconds, totalSeconds - start),
            });
        }

        return chunks;
    }

    private async Task<SegmentResult> SendChunkToNodeAsync(
        TranscodeJob job,
        NodeInfo node,
        ChunkInfo chunk,
        CancellationToken cancellationToken)
    {
        var segmentOutputPath = Path.Combine(Path.GetTempPath(), $"{chunk.ChunkId}.mp4");
        var request = new SegmentRequest
        {
            JobId = job.JobId,
            ChunkId = chunk.ChunkId,
            SourcePath = job.MediaPath,
            OutputPath = segmentOutputPath,
            StartTimeSeconds = chunk.StartTimeSeconds,
            DurationSeconds = chunk.DurationSeconds,
            VideoCodec = job.VideoCodec,
            AudioCodec = job.AudioCodec,
            Preset = job.Preset,
            Crf = job.Crf,
            Resolution = job.Resolution,
            VideoBitrateKbps = job.VideoBitrateKbps,
            PreferHardwareAcceleration = job.PreferHardwareAcceleration,
        };

        if (IsLocalNode(node))
        {
            return await _meshNodeService.ProcessTranscodeChunkAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await RetryPolicy.ExecuteAsync(
            async retryCancellationToken =>
            {
                using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(retryCancellationToken);
                timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(_configuration.RequestTimeoutSeconds));

                using var client = new HttpClient
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };

                var requestJson = JsonSerializer.Serialize(request);
                using var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"http://{node.Address}:{node.Port}/distributed-transcode/chunk");
                message.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                if (!string.IsNullOrWhiteSpace(_configuration.SharedSecret))
                {
                    var timestamp = DateTimeOffset.UtcNow.ToString("O");
                    var signature = WorkerRequestAuthenticator.CreateSignature(requestJson, timestamp, _configuration.SharedSecret);
                    message.Headers.TryAddWithoutValidation(WorkerRequestAuthenticator.TimestampHeaderName, timestamp);
                    message.Headers.TryAddWithoutValidation(WorkerRequestAuthenticator.SignatureHeaderName, signature);
                }

                using var response = await client.SendAsync(message, timeoutCancellation.Token).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(timeoutCancellation.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return new SegmentResult
                    {
                        JobId = request.JobId,
                        ChunkId = request.ChunkId,
                        NodeId = node.NodeId,
                        OutputPath = request.OutputPath,
                        Success = false,
                        Error = $"Worker returned {(int)response.StatusCode}: {responseBody}",
                    };
                }

                return JsonSerializer.Deserialize<SegmentResult>(responseBody)
                    ?? new SegmentResult
                    {
                        JobId = request.JobId,
                        ChunkId = request.ChunkId,
                        NodeId = node.NodeId,
                        OutputPath = request.OutputPath,
                        Success = false,
                        Error = "Worker returned an empty segment result.",
                    };
            },
            Math.Max(1, _configuration.MaxRetryAttempts),
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLocalNode(NodeInfo node)
    {
        return node.Address is "127.0.0.1" or "localhost" ||
               string.Equals(node.NodeName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> CombineSegmentsAsync(
        TranscodeJob job,
        IReadOnlyCollection<SegmentResult> results,
        CancellationToken cancellationToken)
    {
        var outputPath = string.IsNullOrWhiteSpace(job.OutputPath)
            ? Path.Combine(Path.GetTempPath(), $"{job.JobId}-combined.mp4")
            : job.OutputPath;

        var concatFile = Path.Combine(Path.GetTempPath(), $"{job.JobId}-concat.txt");
        await File.WriteAllLinesAsync(concatFile, results.OrderBy(result => result.ChunkId).Select(result => $"file '{result.OutputPath}'"), cancellationToken).ConfigureAwait(false);

        var arguments = $"-f concat -safe 0 -i \"{concatFile}\" -c copy -y \"{outputPath}\"";
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = arguments,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"FFmpeg failed to combine segments: {error}");
        }

        return outputPath;
    }
}
