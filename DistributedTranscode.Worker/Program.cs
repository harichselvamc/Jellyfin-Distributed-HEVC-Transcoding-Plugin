using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.DistributedTranscode.Models;
using Jellyfin.Plugin.DistributedTranscode.Security;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration.GetValue<int>("Worker:Port", 9090)}");

var app = builder.Build();

var nodeId = builder.Configuration["Worker:NodeId"] ?? Environment.MachineName;
var nodeName = builder.Configuration["Worker:NodeName"] ?? Environment.MachineName;
var sharedSecret = builder.Configuration["Worker:SharedSecret"] ?? string.Empty;
var hardwareEncoders = builder.Configuration.GetSection("Worker:SupportedHardwareEncoders").Get<string[]>() ?? [];
var workspaceRoot = Path.GetFullPath(builder.Configuration["Worker:WorkspaceRoot"] ?? @"C:\tmp");

app.MapGet("/distributed-transcode/health", () =>
{
    var node = new NodeInfo
    {
        NodeId = nodeId,
        NodeName = nodeName,
        Address = "manual",
        Port = builder.Configuration.GetValue<int>("Worker:Port", 9090),
        Capabilities = new NodeCapabilities
        {
            CpuCores = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            SupportsHevcDecoding = true,
            SupportsHevcEncoding = true,
            SupportsHardwareAcceleration = builder.Configuration.GetValue("Worker:SupportsHardwareAcceleration", true),
            SupportedHardwareEncoders = hardwareEncoders,
        },
        LastSeenUtc = DateTime.UtcNow,
    };

    return Results.Ok(new
    {
        status = "healthy",
        machine = Environment.MachineName,
        node,
    });
});

app.MapPost("/distributed-transcode/chunk", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    request.EnableBuffering();
    using var reader = new StreamReader(request.Body, leaveOpen: true);
    var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    request.Body.Position = 0;

    var timestamp = request.Headers[WorkerRequestAuthenticator.TimestampHeaderName].ToString();
    var signature = request.Headers[WorkerRequestAuthenticator.SignatureHeaderName].ToString();
    if (!WorkerRequestAuthenticator.IsAuthorized(body, timestamp, signature, sharedSecret, TimeSpan.FromMinutes(5)))
    {
        return Results.Unauthorized();
    }

    var segmentRequest = await JsonSerializer.DeserializeAsync<SegmentRequest>(
        request.Body,
        jsonOptions,
        cancellationToken).ConfigureAwait(false);
    if (segmentRequest is null)
    {
        return Results.BadRequest(new { error = "Invalid chunk payload." });
    }

    var outputDirectory = Path.GetDirectoryName(segmentRequest.OutputPath);
    if (string.IsNullOrWhiteSpace(outputDirectory))
    {
        return Results.BadRequest(new SegmentResult
        {
            JobId = segmentRequest.JobId,
            ChunkId = segmentRequest.ChunkId,
            NodeId = nodeId,
            OutputPath = segmentRequest.OutputPath,
            Success = false,
            Error = "Output path did not include a valid directory.",
        });
    }

    Directory.CreateDirectory(outputDirectory);

    var arguments = BuildFfmpegArguments(segmentRequest);
    app.Logger.LogInformation("Starting FFmpeg for chunk {ChunkId}: ffmpeg {Arguments}", segmentRequest.ChunkId, arguments);
    var errorBuilder = new StringBuilder();

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

    process.ErrorDataReceived += (_, args) =>
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            errorBuilder.AppendLine(args.Data);
            app.Logger.LogInformation("ffmpeg[{ChunkId}] {Line}", segmentRequest.ChunkId, args.Data);
        }
    };

    process.Start();
    process.BeginErrorReadLine();

    try
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        TryKillProcess(process);
        return Results.Json(
            new SegmentResult
            {
                JobId = segmentRequest.JobId,
                ChunkId = segmentRequest.ChunkId,
                NodeId = nodeId,
                OutputPath = segmentRequest.OutputPath,
                Success = false,
                Error = "Chunk processing was canceled or timed out.",
            },
            statusCode: StatusCodes.Status499ClientClosedRequest);
    }

    var errorOutput = errorBuilder.ToString();
    app.Logger.LogInformation("FFmpeg finished for chunk {ChunkId} with exit code {ExitCode}", segmentRequest.ChunkId, process.ExitCode);

    var result = new SegmentResult
    {
        JobId = segmentRequest.JobId,
        ChunkId = segmentRequest.ChunkId,
        NodeId = nodeId,
        OutputPath = segmentRequest.OutputPath,
        Success = process.ExitCode == 0,
        Error = process.ExitCode == 0 ? null : errorOutput,
    };

    return result.Success
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status500InternalServerError);
});

app.MapPost("/distributed-transcode/probe", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    request.EnableBuffering();
    using var reader = new StreamReader(request.Body, leaveOpen: true);
    var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    request.Body.Position = 0;

    var timestamp = request.Headers[WorkerRequestAuthenticator.TimestampHeaderName].ToString();
    var signature = request.Headers[WorkerRequestAuthenticator.SignatureHeaderName].ToString();
    if (!WorkerRequestAuthenticator.IsAuthorized(body, timestamp, signature, sharedSecret, TimeSpan.FromMinutes(5)))
    {
        return Results.Unauthorized();
    }

    var probeRequest = await JsonSerializer.DeserializeAsync<MediaProbeRequest>(
        request.Body,
        jsonOptions,
        cancellationToken).ConfigureAwait(false);
    if (probeRequest is null || string.IsNullOrWhiteSpace(probeRequest.SourcePath))
    {
        return Results.BadRequest(new { error = "SourcePath is required." });
    }

    if (!File.Exists(probeRequest.SourcePath))
    {
        return Results.NotFound(new MediaProbeResult
        {
            SourcePath = probeRequest.SourcePath,
            Success = false,
            Error = "Source file was not found on this worker.",
        });
    }

    var probe = await ProbeDurationAsync(probeRequest.SourcePath, cancellationToken).ConfigureAwait(false);
    return probe.Success ? Results.Ok(probe) : Results.Json(probe, statusCode: StatusCodes.Status500InternalServerError);
});

app.MapGet("/distributed-transcode/file", ([FromQuery] string path, HttpRequest request) =>
{
    var timestamp = request.Headers[WorkerRequestAuthenticator.TimestampHeaderName].ToString();
    var signature = request.Headers[WorkerRequestAuthenticator.SignatureHeaderName].ToString();
    if (!WorkerRequestAuthenticator.IsAuthorized(path, timestamp, signature, sharedSecret, TimeSpan.FromMinutes(5)))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { error = "Path is required." });
    }

    var fullPath = Path.GetFullPath(path);
    if (!IsInsideWorkspace(fullPath, workspaceRoot))
    {
        return Results.BadRequest(new { error = "Requested file is outside the worker workspace." });
    }

    if (!File.Exists(fullPath))
    {
        return Results.NotFound(new { error = "Generated segment was not found." });
    }

    return Results.File(fullPath, "video/mp4", Path.GetFileName(fullPath));
});

app.Run();

static async Task<MediaProbeResult> ProbeDurationAsync(string sourcePath, CancellationToken cancellationToken)
{
    var errorBuilder = new StringBuilder();
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{sourcePath}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        },
    };

    process.ErrorDataReceived += (_, args) =>
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            errorBuilder.AppendLine(args.Data);
        }
    };

    try
    {
        process.Start();
        process.BeginErrorReadLine();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode == 0 &&
            double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds) &&
            durationSeconds > 0)
        {
            return new MediaProbeResult
            {
                SourcePath = sourcePath,
                Success = true,
                DurationSeconds = durationSeconds,
            };
        }

        return new MediaProbeResult
        {
            SourcePath = sourcePath,
            Success = false,
            Error = string.IsNullOrWhiteSpace(errorBuilder.ToString())
                ? "ffprobe did not return a valid duration."
                : errorBuilder.ToString(),
        };
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        return new MediaProbeResult
        {
            SourcePath = sourcePath,
            Success = false,
            Error = ex.Message,
        };
    }
}

static bool IsInsideWorkspace(string fullPath, string workspaceRoot)
{
    var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
}

static string BuildFfmpegArguments(SegmentRequest request)
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

static void TryKillProcess(Process process)
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
