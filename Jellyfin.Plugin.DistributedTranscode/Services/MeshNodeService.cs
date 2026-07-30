using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.DistributedTranscode.Configuration;
using Jellyfin.Plugin.DistributedTranscode.Models;
using Jellyfin.Plugin.DistributedTranscode.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscode.Services;

public sealed class MeshNodeService : IDisposable
{
    private const int DiscoveryPort = 9091;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PluginConfiguration _configuration;
    private readonly ILogger<MeshNodeService> _logger;
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _lifetime;
    private Task? _listenerTask;
    private Task? _broadcastTask;

    public MeshNodeService(PluginConfiguration configuration, ILogger<MeshNodeService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
        {
            return Task.CompletedTask;
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenerTask = Task.Run(() => ListenForHeartbeatsAsync(_lifetime.Token), _lifetime.Token);
        _broadcastTask = Task.Run(() => BroadcastHeartbeatsAsync(_lifetime.Token), _lifetime.Token);
        SeedConfiguredNodes();
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<NodeInfo> GetConnectedNodes() => _nodes.Values.ToArray();

    public async Task PerformHealthCheckAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        foreach (var pair in _nodes.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var uri = $"http://{pair.Value.Address}:{pair.Value.Port}/distributed-transcode/health";
                using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _nodes.TryRemove(pair.Key, out _);
                }
            }
            catch
            {
                _nodes.TryRemove(pair.Key, out _);
            }
        }
    }

    public async Task<SegmentResult> ProcessTranscodeChunkAsync(SegmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var directory = Path.GetDirectoryName(request.OutputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new SegmentResult
            {
                JobId = request.JobId,
                ChunkId = request.ChunkId,
                NodeId = _configuration.NodeName,
                OutputPath = request.OutputPath,
                Success = false,
                Error = "Output path did not include a valid directory.",
            };
        }

        Directory.CreateDirectory(directory);

        var arguments = BuildFfmpegArguments(request);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = arguments,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        string errorOutput;

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return new SegmentResult
            {
                JobId = request.JobId,
                ChunkId = request.ChunkId,
                NodeId = _configuration.NodeName,
                OutputPath = request.OutputPath,
                Success = false,
                Error = "Chunk processing was canceled or timed out.",
            };
        }

        return new SegmentResult
        {
            JobId = request.JobId,
            ChunkId = request.ChunkId,
            NodeId = _configuration.NodeName,
            OutputPath = request.OutputPath,
            Success = process.ExitCode == 0,
            Error = process.ExitCode == 0 ? null : errorOutput,
        };
    }

    public bool AuthorizeWorkerRequest(string body, string? timestamp, string? signature)
    {
        return WorkerRequestAuthenticator.IsAuthorized(
            body,
            timestamp,
            signature,
            _configuration.SharedSecret,
            TimeSpan.FromMinutes(5));
    }

    private void SeedConfiguredNodes()
    {
        foreach (var configuredNode in _configuration.KnownNodes.Where(node => node.Enabled))
        {
            _nodes.TryAdd(
                $"{configuredNode.Address}:{configuredNode.Port}",
                new NodeInfo
                {
                    NodeId = configuredNode.Name,
                    NodeName = configuredNode.Name,
                    Address = configuredNode.Address,
                    Port = configuredNode.Port,
                    LastSeenUtc = DateTime.UtcNow,
                });
        }
    }

    private async Task ListenForHeartbeatsAsync(CancellationToken cancellationToken)
    {
        using var udpClient = new UdpClient(DiscoveryPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetString(result.Buffer);
            var heartbeat = JsonSerializer.Deserialize<NodeHeartbeat>(payload, JsonOptions);
            if (heartbeat is null || string.Equals(heartbeat.NodeName, _configuration.NodeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _nodes.AddOrUpdate(
                heartbeat.NodeId,
                _ => new NodeInfo
                {
                    NodeId = heartbeat.NodeId,
                    NodeName = heartbeat.NodeName,
                    Address = heartbeat.Address,
                    Port = heartbeat.Port,
                    Capabilities = heartbeat.Capabilities,
                    LastSeenUtc = heartbeat.TimestampUtc,
                },
                (_, existing) =>
                {
                    existing.Address = heartbeat.Address;
                    existing.Port = heartbeat.Port;
                    existing.Capabilities = heartbeat.Capabilities;
                    existing.LastSeenUtc = heartbeat.TimestampUtc;
                    return existing;
                });
        }
    }

    private async Task BroadcastHeartbeatsAsync(CancellationToken cancellationToken)
    {
        using var udpClient = new UdpClient { EnableBroadcast = true };

        while (!cancellationToken.IsCancellationRequested)
        {
            var heartbeat = new NodeHeartbeat
            {
                NodeId = _configuration.NodeName,
                NodeName = _configuration.NodeName,
                Address = NetworkDiscovery.GetLocalIpv4Addresses().FirstOrDefault()?.ToString() ?? IPAddress.Loopback.ToString(),
                Port = _configuration.WorkerPort,
                Capabilities = GetCapabilities(),
                TimestampUtc = DateTime.UtcNow,
            };

            var payload = JsonSerializer.SerializeToUtf8Bytes(heartbeat, JsonOptions);
            await udpClient.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildFfmpegArguments(SegmentRequest request)
    {
        var hardwareArgs = request.PreferHardwareAcceleration ? "-hwaccel auto " : string.Empty;
        return string.Join(
            ' ',
            hardwareArgs,
            $"-ss {request.StartTimeSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"-t {request.DurationSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"-i \"{request.SourcePath}\"",
            $"-c:v {request.VideoCodec}",
            $"-c:a {request.AudioCodec}",
            $"-preset {request.Preset}",
            $"-crf {request.Crf}",
            $"-vf scale={request.Resolution}",
            $"-b:v {request.VideoBitrateKbps}k",
            "-pix_fmt yuv420p",
            $"-y \"{request.OutputPath}\"");
    }

    private static NodeCapabilities GetCapabilities()
    {
        return new NodeCapabilities
        {
            CpuCores = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            SupportsHevcDecoding = true,
            SupportsHevcEncoding = true,
            SupportsHardwareAcceleration = true,
            SupportedHardwareEncoders = ["qsv", "nvenc", "vaapi"],
        };
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
    }
}
