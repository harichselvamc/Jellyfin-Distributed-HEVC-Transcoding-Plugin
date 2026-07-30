using Jellyfin.Plugin.DistributedTranscode.Models;
using Jellyfin.Plugin.DistributedTranscode.Security;
using Jellyfin.Plugin.DistributedTranscode.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DistributedTranscode.Web;

[ApiController]
[Route("distributed-transcode")]
public sealed class TranscodeController : ControllerBase
{
    private readonly MeshNodeService _meshNodeService;

    public TranscodeController(MeshNodeService meshNodeService)
    {
        _meshNodeService = meshNodeService;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            machine = Environment.MachineName,
            knownNodes = _meshNodeService.GetConnectedNodes().Count,
        });
    }

    [HttpPost("chunk")]
    public async Task<ActionResult<SegmentResult>> ProcessChunk(CancellationToken cancellationToken)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        Request.Body.Position = 0;

        var timestamp = Request.Headers[WorkerRequestAuthenticator.TimestampHeaderName].ToString();
        var signature = Request.Headers[WorkerRequestAuthenticator.SignatureHeaderName].ToString();
        if (!_meshNodeService.AuthorizeWorkerRequest(body, timestamp, signature))
        {
            return Unauthorized(new { error = "Worker request authentication failed." });
        }

        var request = await System.Text.Json.JsonSerializer.DeserializeAsync<SegmentRequest>(Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return BadRequest(new { error = "Invalid chunk payload." });
        }

        var result = await _meshNodeService.ProcessTranscodeChunkAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, result);
        }

        return Ok(result);
    }
}
