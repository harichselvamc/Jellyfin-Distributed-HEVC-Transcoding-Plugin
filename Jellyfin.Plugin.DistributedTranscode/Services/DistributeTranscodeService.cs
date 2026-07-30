using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.DistributedTranscode.Configuration;
using Jellyfin.Plugin.DistributedTranscode.Models;
using Jellyfin.Plugin.DistributedTranscode.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscode.Services;

public sealed class DistributeTranscodeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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

    public async Task<SegmentResult> RunTestJobAsync(TestJobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw new InvalidOperationException("SourcePath is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new InvalidOperationException("OutputPath is required.");
        }

        var node = ResolveTargetNode(request.NodeId);
        var job = new TranscodeJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            MediaPath = request.SourcePath,
            OutputPath = request.OutputPath,
            VideoCodec = request.VideoCodec,
            AudioCodec = request.AudioCodec,
            Preset = request.Preset,
            Crf = request.Crf,
            Resolution = request.Resolution,
            VideoBitrateKbps = request.VideoBitrateKbps,
            PreferHardwareAcceleration = request.PreferHardwareAcceleration,
        };
        _transcodeJobManager.StartStatus(new JobStatus
        {
            JobId = job.JobId,
            Kind = "test",
            NodeId = node.NodeId,
            SourcePath = request.SourcePath,
            OutputPath = request.OutputPath,
            State = "running",
            Progress = 10,
        });

        var chunk = new ChunkInfo
        {
            ChunkId = Guid.NewGuid().ToString("N"),
            StartTimeSeconds = request.StartTimeSeconds,
            DurationSeconds = request.DurationSeconds,
        };

        var result = await SendChunkToNodeAsync(job, node, chunk, request.OutputPath, cancellationToken).ConfigureAwait(false);
        _transcodeJobManager.FinishStatus(job.JobId, result.Success, result.Error);
        return result;
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

    public async Task<FullTranscodeResult> RunFullTranscodeAsync(FullTranscodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw new InvalidOperationException("SourcePath is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new InvalidOperationException("OutputPath is required.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkerOutputDirectory))
        {
            throw new InvalidOperationException("WorkerOutputDirectory is required.");
        }

        var nodes = ResolveTargetNodes(request.NodeId);
        var durationSeconds = await ResolveDurationAsync(nodes[0], request, cancellationToken).ConfigureAwait(false);
        var chunkSizeSeconds = Math.Max(1, request.ChunkSizeSeconds ?? _configuration.ChunkSizeSeconds);
        var chunks = CreateIndexedChunks(durationSeconds, chunkSizeSeconds);
        var assignments = _jobDistributor.AssignChunks(chunks, nodes);
        var job = new TranscodeJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            MediaPath = request.SourcePath,
            OutputPath = request.OutputPath,
            VideoCodec = request.VideoCodec,
            AudioCodec = request.AudioCodec,
            Preset = request.Preset,
            Crf = request.Crf,
            Resolution = request.Resolution,
            VideoBitrateKbps = request.VideoBitrateKbps,
            PreferHardwareAcceleration = request.PreferHardwareAcceleration,
            TotalDuration = TimeSpan.FromSeconds(durationSeconds),
        };

        _transcodeJobManager.AddOrUpdate(job);
        _transcodeJobManager.StartStatus(new JobStatus
        {
            JobId = job.JobId,
            Kind = "full-file",
            NodeId = nodes.Count == 1 ? nodes[0].NodeId : "mesh",
            SourcePath = request.SourcePath,
            OutputPath = request.OutputPath,
            State = "running",
            Progress = 1,
        });

        try
        {
            var localJobDirectory = Path.Combine(Path.GetTempPath(), "distributed-transcode", job.JobId);
            Directory.CreateDirectory(localJobDirectory);

            var localResults = new List<SegmentResult>(assignments.Count);
            for (var index = 0; index < assignments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var assignment = assignments[index];
                var remoteOutputPath = CombineRemotePath(request.WorkerOutputDirectory, job.JobId, $"{assignment.Chunk.ChunkId}.mp4");
                _transcodeJobManager.UpdateStatus(
                    job.JobId,
                    "transcoding",
                    5 + (index * 70.0 / assignments.Count),
                    assignment.Node.NodeId);

                var segmentResult = await SendChunkToNodeAsync(job, assignment.Node, assignment.Chunk, remoteOutputPath, cancellationToken).ConfigureAwait(false);
                if (!segmentResult.Success)
                {
                    throw new InvalidOperationException($"Chunk {assignment.Chunk.ChunkId} failed on {assignment.Node.NodeId}: {segmentResult.Error}");
                }

                var localSegmentPath = Path.Combine(localJobDirectory, $"{assignment.Chunk.ChunkId}.mp4");
                if (IsLocalNode(assignment.Node))
                {
                    File.Copy(segmentResult.OutputPath, localSegmentPath, overwrite: true);
                }
                else
                {
                    await DownloadSegmentFromNodeAsync(assignment.Node, segmentResult.OutputPath, localSegmentPath, cancellationToken).ConfigureAwait(false);
                }

                localResults.Add(new SegmentResult
                {
                    JobId = segmentResult.JobId,
                    ChunkId = segmentResult.ChunkId,
                    NodeId = segmentResult.NodeId,
                    OutputPath = localSegmentPath,
                    Success = true,
                });

                _transcodeJobManager.UpdateStatus(
                    job.JobId,
                    "downloading",
                    10 + ((index + 1) * 75.0 / assignments.Count),
                    assignment.Node.NodeId);
            }

            _transcodeJobManager.UpdateStatus(job.JobId, "combining", 90, outputPath: request.OutputPath);
            var outputPath = await CombineSegmentsAsync(job, localResults, cancellationToken).ConfigureAwait(false);
            _transcodeJobManager.FinishStatus(job.JobId, success: true);

            return new FullTranscodeResult
            {
                JobId = job.JobId,
                OutputPath = outputPath,
                Success = true,
                DurationSeconds = durationSeconds,
                ChunkCount = chunks.Count,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
        {
            _transcodeJobManager.FinishStatus(job.JobId, success: false, ex.Message);
            return new FullTranscodeResult
            {
                JobId = job.JobId,
                OutputPath = request.OutputPath,
                Success = false,
                DurationSeconds = durationSeconds,
                ChunkCount = chunks.Count,
                Error = ex.Message,
            };
        }
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

    private static List<ChunkInfo> CreateIndexedChunks(double totalSeconds, int chunkSizeSeconds)
    {
        var chunks = new List<ChunkInfo>();
        var index = 0;

        for (double start = 0; start < totalSeconds; start += chunkSizeSeconds)
        {
            chunks.Add(new ChunkInfo
            {
                ChunkId = index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                StartTimeSeconds = start,
                DurationSeconds = Math.Min(chunkSizeSeconds, totalSeconds - start),
            });
            index++;
        }

        return chunks;
    }

    private async Task<SegmentResult> SendChunkToNodeAsync(
        TranscodeJob job,
        NodeInfo node,
        ChunkInfo chunk,
        CancellationToken cancellationToken)
    {
        return await SendChunkToNodeAsync(job, node, chunk, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SegmentResult> SendChunkToNodeAsync(
        TranscodeJob job,
        NodeInfo node,
        ChunkInfo chunk,
        string? outputPathOverride,
        CancellationToken cancellationToken)
    {
        var segmentOutputPath = string.IsNullOrWhiteSpace(outputPathOverride)
            ? Path.Combine(Path.GetTempPath(), $"{chunk.ChunkId}.mp4")
            : outputPathOverride;
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

                var requestJson = JsonSerializer.Serialize(request, JsonOptions);
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

                return JsonSerializer.Deserialize<SegmentResult>(responseBody, JsonOptions)
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

    private async Task<double> ResolveDurationAsync(NodeInfo node, FullTranscodeRequest request, CancellationToken cancellationToken)
    {
        if (request.TotalDurationSeconds is > 0)
        {
            return request.TotalDurationSeconds.Value;
        }

        if (IsLocalNode(node))
        {
            throw new InvalidOperationException("TotalDurationSeconds is required when probing a local coordinator node is not available.");
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(_configuration.RequestTimeoutSeconds));

        using var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var body = JsonSerializer.Serialize(new MediaProbeRequest { SourcePath = request.SourcePath }, JsonOptions);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"http://{node.Address}:{node.Port}/distributed-transcode/probe")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_configuration.SharedSecret))
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            var signature = WorkerRequestAuthenticator.CreateSignature(body, timestamp, _configuration.SharedSecret);
            message.Headers.TryAddWithoutValidation(WorkerRequestAuthenticator.TimestampHeaderName, timestamp);
            message.Headers.TryAddWithoutValidation(WorkerRequestAuthenticator.SignatureHeaderName, signature);
        }

        using var response = await client.SendAsync(message, timeoutCancellation.Token).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(timeoutCancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Worker probe returned {(int)response.StatusCode}: {responseBody}");
        }

        var probe = JsonSerializer.Deserialize<MediaProbeResult>(responseBody, JsonOptions);
        if (probe is null || !probe.Success || probe.DurationSeconds <= 0)
        {
            throw new InvalidOperationException(probe?.Error ?? "Worker probe did not return a valid duration.");
        }

        return probe.DurationSeconds;
    }

    private async Task DownloadSegmentFromNodeAsync(NodeInfo node, string remotePath, string localPath, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(_configuration.RequestTimeoutSeconds));

        using var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var uri = $"http://{node.Address}:{node.Port}/distributed-transcode/file?path={Uri.EscapeDataString(remotePath)}";
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);

        if (!string.IsNullOrWhiteSpace(_configuration.SharedSecret))
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            var signature = WorkerRequestAuthenticator.CreateSignature(remotePath, timestamp, _configuration.SharedSecret);
            message.Headers.TryAddWithoutValidation(WorkerRequestAuthenticator.TimestampHeaderName, timestamp);
            message.Headers.TryAddWithoutValidation(WorkerRequestAuthenticator.SignatureHeaderName, signature);
        }

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(timeoutCancellation.Token).ConfigureAwait(false);
            throw new InvalidOperationException($"Worker file download returned {(int)response.StatusCode}: {body}");
        }

        await using var remoteStream = await response.Content.ReadAsStreamAsync(timeoutCancellation.Token).ConfigureAwait(false);
        await using var localStream = File.Create(localPath);
        await remoteStream.CopyToAsync(localStream, timeoutCancellation.Token).ConfigureAwait(false);
    }

    private IReadOnlyList<NodeInfo> ResolveTargetNodes(string? nodeId)
    {
        var nodes = _meshNodeService.GetConnectedNodes();

        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            var matchedNode = nodes.FirstOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
            if (matchedNode is not null)
            {
                return [matchedNode];
            }

            throw new InvalidOperationException($"Worker node '{nodeId}' was not found.");
        }

        var availableNodes = nodes
            .Where(node => node.Capabilities.SupportsHevcEncoding || node.Capabilities.SupportsHardwareAcceleration)
            .ToArray();
        if (availableNodes.Length == 0)
        {
            throw new InvalidOperationException("No worker nodes are registered.");
        }

        return availableNodes;
    }

    private static string CombineRemotePath(string root, string jobId, string fileName)
    {
        var separator = root.Contains('\\', StringComparison.Ordinal) ? "\\" : "/";
        return root.TrimEnd('\\', '/') + separator + jobId + separator + fileName;
    }

    private NodeInfo ResolveTargetNode(string? nodeId)
    {
        var nodes = _meshNodeService.GetConnectedNodes();

        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            var matchedNode = nodes.FirstOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
            if (matchedNode is not null)
            {
                return matchedNode;
            }

            throw new InvalidOperationException($"Worker node '{nodeId}' was not found.");
        }

        var firstNode = nodes.FirstOrDefault();
        if (firstNode is null)
        {
            throw new InvalidOperationException("No worker nodes are registered.");
        }

        return firstNode;
    }

    private static async Task<string> CombineSegmentsAsync(
        TranscodeJob job,
        IReadOnlyCollection<SegmentResult> results,
        CancellationToken cancellationToken)
    {
        var outputPath = string.IsNullOrWhiteSpace(job.OutputPath)
            ? Path.Combine(Path.GetTempPath(), $"{job.JobId}-combined.mp4")
            : job.OutputPath;
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

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
