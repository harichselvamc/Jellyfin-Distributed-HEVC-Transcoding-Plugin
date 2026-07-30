using Jellyfin.Plugin.DistributedTranscode.Models;
using Jellyfin.Plugin.DistributedTranscode.Security;
using Jellyfin.Plugin.DistributedTranscode.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Jellyfin.Plugin.DistributedTranscode.Web;

[ApiController]
[Route("distributed-transcode")]
public sealed class TranscodeController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DistributeTranscodeService _distributeTranscodeService;
    private readonly MeshNodeService _meshNodeService;
    private readonly TranscodeJobManager _transcodeJobManager;

    public TranscodeController(
        MeshNodeService meshNodeService,
        DistributeTranscodeService distributeTranscodeService,
        TranscodeJobManager transcodeJobManager)
    {
        _meshNodeService = meshNodeService;
        _distributeTranscodeService = distributeTranscodeService;
        _transcodeJobManager = transcodeJobManager;
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

    [HttpGet("nodes")]
    public ActionResult<IReadOnlyCollection<NodeInfo>> GetNodes()
    {
        return Ok(_meshNodeService.GetConnectedNodes());
    }

    [HttpGet("summary")]
    public ActionResult<DistributedTranscodeSummary> GetSummary()
    {
        return Ok(_meshNodeService.GetSummary(_transcodeJobManager));
    }

    [HttpGet("dashboard")]
    public ContentResult Dashboard()
    {
        return Content(DashboardHtml, "text/html; charset=utf-8");
    }

    [HttpPost("nodes")]
    public ActionResult<NodeInfo> RegisterNode([FromBody] NodeRegistrationRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Address))
        {
            return BadRequest(new { error = "Address is required." });
        }

        var node = _meshNodeService.RegisterOrUpdateNode(request);
        return Ok(node);
    }

    [HttpPost("check-worker")]
    public async Task<ActionResult<object>> CheckWorker([FromBody] WorkerCheckRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Address))
        {
            return BadRequest(new { error = "Address is required." });
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            var response = await client.GetAsync(
                $"http://{request.Address}:{request.Port}/distributed-transcode/health",
                cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = $"Worker returned HTTP {(int)response.StatusCode}.",
                    body,
                });
            }

            var payload = JsonSerializer.Deserialize<object>(body, JsonOptions);
            return Ok(payload);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    [HttpPost("test-job")]
    public async Task<ActionResult<SegmentResult>> RunTestJob([FromBody] TestJobRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SourcePath) || string.IsNullOrWhiteSpace(request.OutputPath))
        {
            return BadRequest(new { error = "SourcePath and OutputPath are required." });
        }

        try
        {
            var result = await _distributeTranscodeService.RunTestJobAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, result);
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("transcode-file")]
    public async Task<ActionResult<FullTranscodeResult>> RunFullTranscode([FromBody] FullTranscodeRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SourcePath) || string.IsNullOrWhiteSpace(request.OutputPath))
        {
            return BadRequest(new { error = "SourcePath and OutputPath are required." });
        }

        try
        {
            var result = await _distributeTranscodeService.RunFullTranscodeAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, result);
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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

        var request = await JsonSerializer.DeserializeAsync<SegmentRequest>(
            Request.Body,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
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

    private const string DashboardHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Distributed HEVC Transcoding</title>
    <style>
        :root {
            color-scheme: light;
            --bg: #f5f5f5;
            --panel: #ffffff;
            --panel-strong: #f0f0f0;
            --border: #d9d9d9;
            --text: #111111;
            --muted: #666666;
            --good: #111111;
            --warn: #555555;
            --bad: #9b1c1c;
            --accent: #111111;
            --accent-strong: #111111;
            --shadow: 0 12px 28px rgba(0, 0, 0, .08);
        }

        * {
            box-sizing: border-box;
        }

        body {
            margin: 0;
            min-height: 100vh;
            color: var(--text);
            font: 16px/1.5 "Segoe UI", "Trebuchet MS", sans-serif;
            background: var(--bg);
        }

        a {
            color: var(--accent-strong);
        }

        .shell {
            width: min(1220px, calc(100% - 32px));
            margin: 0 auto;
            padding: 32px 0 48px;
        }

        .hero {
            display: grid;
            grid-template-columns: minmax(0, 1fr) auto;
            gap: 20px;
            align-items: center;
            margin-bottom: 22px;
        }

        .eyebrow {
            color: var(--accent-strong);
            font-size: .82rem;
            font-weight: 800;
            letter-spacing: .16em;
            text-transform: uppercase;
        }

        h1, h2, h3, p {
            margin-top: 0;
        }

        h1 {
            margin-bottom: 8px;
            font-size: clamp(2rem, 5vw, 4.4rem);
            line-height: .95;
            letter-spacing: -.05em;
        }

        h2 {
            margin-bottom: 14px;
            font-size: 1.25rem;
        }

        .subtitle {
            max-width: 720px;
            margin-bottom: 0;
            color: var(--muted);
        }

        .button {
            display: inline-flex;
            justify-content: center;
            align-items: center;
            min-height: 44px;
            padding: 0 18px;
            border: 1px solid var(--accent);
            border-radius: 10px;
            color: #ffffff;
            background: var(--accent-strong);
            box-shadow: none;
            font: inherit;
            font-weight: 800;
            cursor: pointer;
        }

        .button.secondary {
            color: var(--text);
            background: #ffffff;
            box-shadow: none;
        }

        .button.good {
            color: #ffffff;
            background: #111111;
            box-shadow: none;
        }

        .button:disabled {
            cursor: not-allowed;
            opacity: .55;
        }

        .grid {
            display: grid;
            grid-template-columns: minmax(0, 1.45fr) minmax(330px, .75fr);
            gap: 18px;
        }

        .cards {
            display: grid;
            grid-template-columns: repeat(4, minmax(0, 1fr));
            gap: 12px;
            margin-bottom: 18px;
        }

        .card, .panel {
            border: 1px solid var(--border);
            border-radius: 14px;
            background: var(--panel);
            box-shadow: var(--shadow);
        }

        .card {
            padding: 18px;
        }

        .card .label {
            color: var(--muted);
            font-size: .82rem;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: .08em;
        }

        .card .value {
            margin-top: 8px;
            font-size: 1.75rem;
            font-weight: 900;
            letter-spacing: -.04em;
        }

        .panel {
            padding: 20px;
        }

        .stack {
            display: grid;
            gap: 14px;
        }

        .device, .job {
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 16px;
            background: #fafafa;
        }

        .device-head, .job-head {
            display: flex;
            align-items: flex-start;
            justify-content: space-between;
            gap: 14px;
        }

        .muted {
            color: var(--muted);
        }

        .pill {
            display: inline-flex;
            align-items: center;
            min-height: 28px;
            padding: 0 10px;
            border-radius: 999px;
            color: var(--good);
            background: #eeeeee;
            font-size: .82rem;
            font-weight: 800;
            white-space: nowrap;
        }

        .pill.warn {
            color: var(--warn);
            background: #eeeeee;
        }

        .pill.bad {
            color: var(--bad);
            background: #f5e9e9;
        }

        .facts {
            display: grid;
            gap: 7px;
            margin-top: 14px;
        }

        .fact {
            display: flex;
            justify-content: space-between;
            gap: 14px;
        }

        .fact span:first-child {
            color: var(--muted);
        }

        .fact strong {
            text-align: right;
            word-break: break-word;
        }

        .progress {
            height: 10px;
            overflow: hidden;
            border-radius: 999px;
            background: #e6e6e6;
            margin-top: 14px;
        }

        .progress span {
            display: block;
            height: 100%;
            width: 0;
            border-radius: inherit;
            background: #111111;
        }

        label {
            display: grid;
            gap: 6px;
            color: var(--muted);
            font-weight: 800;
        }

        input, select {
            width: 100%;
            min-height: 44px;
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 0 13px;
            color: var(--text);
            background: #ffffff;
            font: inherit;
        }

        input:focus, select:focus {
            outline: 2px solid #111111;
            border-color: transparent;
        }

        .form-grid {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 12px;
        }

        .form-grid .wide {
            grid-column: 1 / -1;
        }

        .message {
            min-height: 44px;
            border: 1px solid var(--border);
            border-radius: 10px;
            padding: 12px 14px;
            color: var(--muted);
            background: #fafafa;
        }

        .message.good {
            color: var(--good);
        }

        .message.bad {
            color: var(--bad);
        }

        .actions {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 10px;
        }

        .empty {
            color: var(--muted);
            border: 1px dashed var(--border);
            border-radius: 12px;
            padding: 18px;
            background: #fafafa;
        }

        @media (max-width: 920px) {
            .hero, .grid {
                grid-template-columns: 1fr;
            }

            .cards {
                grid-template-columns: repeat(2, minmax(0, 1fr));
            }
        }

        @media (max-width: 560px) {
            .cards, .form-grid, .actions {
                grid-template-columns: 1fr;
            }

            .shell {
                width: min(100% - 20px, 1220px);
                padding-top: 18px;
            }
        }
    </style>
</head>
<body>
    <main class="shell">
        <section class="hero">
            <div>
                <div class="eyebrow">Jellyfin worker mesh</div>
                <h1>Distributed HEVC Transcoding</h1>
                <p class="subtitle">Check Windows workers, save connected devices, run a tiny Pi to worker transcode test, and watch recent dispatch status from one reliable page.</p>
            </div>
            <button class="button secondary" id="refreshButton" type="button">Refresh</button>
        </section>

        <section class="cards" aria-label="Summary">
            <div class="card"><div class="label">Plugin status</div><div class="value" id="statusValue">Loading</div></div>
            <div class="card"><div class="label">Known devices</div><div class="value" id="nodeCountValue">0</div></div>
            <div class="card"><div class="label">Recent jobs</div><div class="value" id="jobCountValue">0</div></div>
            <div class="card"><div class="label">Last update</div><div class="value" id="updatedValue">Never</div></div>
        </section>

        <section class="grid">
            <div class="stack">
                <section class="panel">
                    <h2>Connected Devices</h2>
                    <div class="stack" id="nodesList"><div class="empty">No devices saved yet.</div></div>
                </section>

                <section class="panel">
                    <h2>Recent Jobs</h2>
                    <div class="stack" id="jobsList"><div class="empty">No jobs yet. Run the tiny test job when the worker is saved.</div></div>
                </section>
            </div>

            <aside class="stack">
                <section class="panel">
                    <h2>Add Worker</h2>
                    <div class="form-grid">
                        <label class="wide">Node name
                            <input id="nodeNameInput" value="My Laptop" autocomplete="off">
                        </label>
                        <label>IP address
                            <input id="addressInput" value="192.168.1.6" autocomplete="off">
                        </label>
                        <label>Port
                            <input id="portInput" type="number" min="1" max="65535" value="9090">
                        </label>
                        <label>CPU cores
                            <input id="cpuInput" type="number" min="0" value="8">
                        </label>
                        <label>Memory bytes
                            <input id="memoryInput" type="number" min="0" value="8000000000">
                        </label>
                        <div class="wide actions">
                            <button class="button secondary" id="checkButton" type="button">Check Worker</button>
                            <button class="button good" id="saveButton" type="button">Save Worker</button>
                        </div>
                    </div>
                    <div class="message" id="messageBox" style="margin-top: 14px;">Ready. Start with Check Worker.</div>
                </section>

                <section class="panel">
                    <h2>Pi to Worker Test</h2>
                    <div class="form-grid">
                        <label class="wide">Worker
                            <select id="testNodeSelect"></select>
                        </label>
                        <label class="wide">Source path on Windows worker
                            <input id="sourceInput" value="C:\Users\haric\Downloads\brittany-broski-broski-report.mp4">
                        </label>
                        <label class="wide">Output path on Windows worker
                            <input id="outputInput" value="C:\tmp\pi-test-chunk.mp4">
                        </label>
                        <label>Seconds
                            <input id="durationInput" type="number" min="1" max="30" value="3">
                        </label>
                        <label>Resolution
                            <input id="resolutionInput" value="640x360">
                        </label>
                        <button class="button wide" id="testButton" type="button">Run Tiny Test Job</button>
                    </div>
                    <p class="muted" style="margin: 14px 0 0;">Success means Jellyfin on the Pi can send a transcode chunk to the Windows worker and receive the result.</p>
                </section>

                <section class="panel">
                    <h2>Distributed File Transcode</h2>
                    <div class="form-grid">
                        <label class="wide">Final output path on Pi/Jellyfin
                            <input id="fullOutputInput" value="/cache/distributed-transcode/final-output.mp4">
                        </label>
                        <label>Worker temp folder
                            <input id="workerTempInput" value="C:\tmp">
                        </label>
                        <label>Chunk seconds
                            <input id="chunkSizeInput" type="number" min="3" max="300" value="10">
                        </label>
                        <button class="button wide" id="fullTranscodeButton" type="button">Run Full Distributed Transcode</button>
                    </div>
                    <p class="muted" style="margin: 14px 0 0;">This probes the source on the worker, splits it into chunks, downloads finished chunks to the Pi, then combines the final MP4.</p>
                </section>
            </aside>
        </section>
    </main>

    <script>
        (function () {
            var lastHealth = null;
            var selectedNodeId = '';

            function byId(id) {
                return document.getElementById(id);
            }

            function getValue(item, camelName, pascalName, fallback) {
                if (!item) {
                    return fallback;
                }

                if (item[camelName] !== undefined && item[camelName] !== null) {
                    return item[camelName];
                }

                if (item[pascalName] !== undefined && item[pascalName] !== null) {
                    return item[pascalName];
                }

                return fallback;
            }

            function setMessage(text, kind) {
                var box = byId('messageBox');
                box.textContent = text;
                box.className = 'message' + (kind ? ' ' + kind : '');
            }

            function setBusy(button, busyText) {
                var oldText = button.textContent;
                button.disabled = true;
                button.textContent = busyText;
                return function () {
                    button.disabled = false;
                    button.textContent = oldText;
                };
            }

            async function apiJson(path, options) {
                var response = await fetch('/distributed-transcode' + path, Object.assign({
                    credentials: 'same-origin'
                }, options || {}));
                var text = await response.text();
                var body = null;

                if (text) {
                    try {
                        body = JSON.parse(text);
                    } catch (error) {
                        body = { error: text };
                    }
                }

                if (!response.ok) {
                    var message = body && body.error ? body.error : 'HTTP ' + response.status;
                    throw new Error(message);
                }

                return body;
            }

            function escapeHtml(value) {
                return String(value === undefined || value === null ? '' : value)
                    .replace(/&/g, '&amp;')
                    .replace(/</g, '&lt;')
                    .replace(/>/g, '&gt;')
                    .replace(/"/g, '&quot;')
                    .replace(/'/g, '&#39;');
            }

            function fact(label, value) {
                return '<div class="fact"><span>' + escapeHtml(label) + '</span><strong>' + escapeHtml(value) + '</strong></div>';
            }

            function pill(text, kind) {
                return '<span class="pill ' + escapeHtml(kind || '') + '">' + escapeHtml(text) + '</span>';
            }

            function formatBytes(bytes) {
                var number = Number(bytes || 0);
                if (!number) {
                    return 'Unknown';
                }

                if (number >= 1073741824) {
                    return (number / 1073741824).toFixed(1) + ' GB';
                }

                if (number >= 1048576) {
                    return (number / 1048576).toFixed(1) + ' MB';
                }

                return number + ' bytes';
            }

            function makeNodeId(name, address, port) {
                var fromName = String(name || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
                return fromName || String(address || 'worker') + '-' + String(port || 9090);
            }

            function renderNodes(nodes) {
                var container = byId('nodesList');
                var select = byId('testNodeSelect');
                select.innerHTML = '';

                if (!nodes.length) {
                    container.innerHTML = '<div class="empty">No devices saved yet. Check the worker, then save it here.</div>';
                    select.innerHTML = '<option value="">No saved workers</option>';
                    return;
                }

                container.innerHTML = nodes.map(function (node) {
                    var caps = getValue(node, 'capabilities', 'Capabilities', {});
                    var nodeName = getValue(node, 'nodeName', 'NodeName', 'Unknown worker');
                    var nodeId = getValue(node, 'nodeId', 'NodeId', nodeName);
                    var address = getValue(node, 'address', 'Address', 'unknown');
                    var port = getValue(node, 'port', 'Port', 0);
                    var activeJobs = getValue(node, 'activeJobs', 'ActiveJobs', 0);
                    var lastSeen = getValue(node, 'lastSeenUtc', 'LastSeenUtc', null);
                    var hasGpu = getValue(caps, 'supportsHardwareAcceleration', 'SupportsHardwareAcceleration', false);
                    var cores = getValue(caps, 'cpuCores', 'CpuCores', 0);
                    var memory = getValue(caps, 'availableMemoryBytes', 'AvailableMemoryBytes', 0);
                    var encoders = getValue(caps, 'supportedHardwareEncoders', 'SupportedHardwareEncoders', []);

                    return '<article class="device">' +
                        '<div class="device-head"><div><h3 style="margin-bottom: 2px;">' + escapeHtml(nodeName) + '</h3><div class="muted">' + escapeHtml(nodeId) + '</div></div>' +
                        pill(hasGpu ? 'GPU ready' : 'CPU only', hasGpu ? '' : 'warn') + '</div>' +
                        '<div class="facts">' +
                        fact('Address', String(address) + ':' + String(port)) +
                        fact('CPU cores', cores || 'Unknown') +
                        fact('Memory', formatBytes(memory)) +
                        fact('Active jobs', activeJobs) +
                        fact('Encoders', encoders && encoders.length ? encoders.join(', ') : 'None listed') +
                        fact('Last seen', lastSeen ? new Date(lastSeen).toLocaleString() : 'Unknown') +
                        '</div>' +
                        '<button class="button secondary use-node" data-node-id="' + escapeHtml(nodeId) + '" style="margin-top: 14px;" type="button">Use for test</button>' +
                        '</article>';
                }).join('');

                nodes.forEach(function (node) {
                    var nodeName = getValue(node, 'nodeName', 'NodeName', 'Unknown worker');
                    var nodeId = getValue(node, 'nodeId', 'NodeId', nodeName);
                    var option = document.createElement('option');
                    option.value = nodeId;
                    option.textContent = nodeName + ' (' + nodeId + ')';
                    select.appendChild(option);
                });

                if (selectedNodeId) {
                    select.value = selectedNodeId;
                }

                Array.prototype.forEach.call(document.querySelectorAll('.use-node'), function (button) {
                    button.addEventListener('click', function () {
                        selectedNodeId = button.getAttribute('data-node-id') || '';
                        select.value = selectedNodeId;
                        setMessage('Selected ' + selectedNodeId + ' for the test job.', 'good');
                    });
                });
            }

            function renderJobs(jobs) {
                var container = byId('jobsList');

                if (!jobs.length) {
                    container.innerHTML = '<div class="empty">No jobs yet. Run the tiny test job when the worker is saved.</div>';
                    return;
                }

                container.innerHTML = jobs.map(function (job) {
                    var kind = getValue(job, 'kind', 'Kind', 'job');
                    var nodeId = getValue(job, 'nodeId', 'NodeId', 'unknown');
                    var state = getValue(job, 'state', 'State', 'unknown');
                    var progress = Number(getValue(job, 'progress', 'Progress', 0));
                    var outputPath = getValue(job, 'outputPath', 'OutputPath', '');
                    var error = getValue(job, 'error', 'Error', '');
                    var safeProgress = Math.max(0, Math.min(100, progress));
                    var stateKind = state === 'completed' ? '' : state === 'failed' ? 'bad' : 'warn';

                    return '<article class="job">' +
                        '<div class="job-head"><div><strong>' + escapeHtml(kind) + '</strong><div class="muted">Node ' + escapeHtml(nodeId) + '</div></div>' +
                        pill(state, stateKind) + '</div>' +
                        '<div class="progress"><span style="width: ' + safeProgress + '%;"></span></div>' +
                        '<div class="facts">' +
                        fact('Progress', safeProgress + '%') +
                        fact('Output', outputPath || 'Not set') +
                        (error ? fact('Error', error) : '') +
                        '</div>' +
                        '</article>';
                }).join('');
            }

            async function loadSummary() {
                var summary = await apiJson('/summary');
                var nodes = getValue(summary, 'nodes', 'Nodes', []);
                var jobs = getValue(summary, 'recentJobs', 'RecentJobs', []);

                byId('statusValue').textContent = getValue(summary, 'status', 'Status', 'healthy');
                byId('nodeCountValue').textContent = getValue(summary, 'knownNodes', 'KnownNodes', nodes.length);
                byId('jobCountValue').textContent = jobs.length;
                byId('updatedValue').textContent = new Date().toLocaleTimeString();
                renderNodes(nodes);
                renderJobs(jobs);
                return summary;
            }

            async function refresh() {
                var done = setBusy(byId('refreshButton'), 'Refreshing...');
                try {
                    await loadSummary();
                    setMessage('Dashboard refreshed.', 'good');
                } catch (error) {
                    setMessage('Could not refresh: ' + error.message, 'bad');
                } finally {
                    done();
                }
            }

            async function checkWorker() {
                var button = byId('checkButton');
                var done = setBusy(button, 'Checking...');
                var address = byId('addressInput').value.trim();
                var port = Number(byId('portInput').value || 9090);

                setMessage('Checking http://' + address + ':' + port + '/distributed-transcode/health ...');
                try {
                    var health = await apiJson('/check-worker', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ address: address, port: port })
                    });
                    var node = getValue(health, 'node', 'Node', {});
                    var caps = getValue(node, 'capabilities', 'Capabilities', {});
                    lastHealth = health;

                    byId('nodeNameInput').value = getValue(node, 'nodeName', 'NodeName', byId('nodeNameInput').value);
                    byId('cpuInput').value = getValue(caps, 'cpuCores', 'CpuCores', byId('cpuInput').value);
                    byId('memoryInput').value = getValue(caps, 'availableMemoryBytes', 'AvailableMemoryBytes', byId('memoryInput').value);
                    setMessage('Worker is available. Now click Save Worker.', 'good');
                } catch (error) {
                    lastHealth = null;
                    setMessage('Worker is not reachable: ' + error.message, 'bad');
                } finally {
                    done();
                }
            }

            async function saveWorker() {
                var button = byId('saveButton');
                var done = setBusy(button, 'Saving...');
                var healthNode = lastHealth ? getValue(lastHealth, 'node', 'Node', {}) : {};
                var caps = getValue(healthNode, 'capabilities', 'Capabilities', {});
                var nodeName = byId('nodeNameInput').value.trim() || 'Worker';
                var address = byId('addressInput').value.trim();
                var port = Number(byId('portInput').value || 9090);
                var nodeId = getValue(healthNode, 'nodeId', 'NodeId', makeNodeId(nodeName, address, port));

                try {
                    await apiJson('/nodes', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            nodeId: nodeId,
                            nodeName: nodeName,
                            address: address,
                            port: port,
                            cpuCores: Number(byId('cpuInput').value || getValue(caps, 'cpuCores', 'CpuCores', 0)),
                            availableMemoryBytes: Number(byId('memoryInput').value || getValue(caps, 'availableMemoryBytes', 'AvailableMemoryBytes', 0)),
                            supportsHevcDecoding: getValue(caps, 'supportsHevcDecoding', 'SupportsHevcDecoding', true),
                            supportsHevcEncoding: getValue(caps, 'supportsHevcEncoding', 'SupportsHevcEncoding', true),
                            supportsHardwareAcceleration: getValue(caps, 'supportsHardwareAcceleration', 'SupportsHardwareAcceleration', true),
                            supportedHardwareEncoders: getValue(caps, 'supportedHardwareEncoders', 'SupportedHardwareEncoders', ['qsv'])
                        })
                    });

                    selectedNodeId = nodeId;
                    await loadSummary();
                    byId('testNodeSelect').value = nodeId;
                    setMessage('Worker saved and ready for test jobs.', 'good');
                } catch (error) {
                    setMessage('Could not save worker: ' + error.message, 'bad');
                } finally {
                    done();
                }
            }

            async function runTestJob() {
                var button = byId('testButton');
                var done = setBusy(button, 'Running...');
                var nodeId = byId('testNodeSelect').value;

                if (!nodeId) {
                    setMessage('Save a worker first, then run the test job.', 'bad');
                    done();
                    return;
                }

                setMessage('Sending one small chunk from the Pi to worker ' + nodeId + ' ...');
                try {
                    var result = await apiJson('/test-job', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            nodeId: nodeId,
                            sourcePath: byId('sourceInput').value,
                            outputPath: byId('outputInput').value,
                            startTimeSeconds: 0,
                            durationSeconds: Number(byId('durationInput').value || 3),
                            videoCodec: 'libx264',
                            audioCodec: 'aac',
                            preset: 'veryfast',
                            crf: 28,
                            resolution: byId('resolutionInput').value || '640x360',
                            videoBitrateKbps: 1200,
                            preferHardwareAcceleration: false
                        })
                    });

                    await loadSummary();
                    setMessage('Test succeeded. Output: ' + getValue(result, 'outputPath', 'OutputPath', ''), 'good');
                } catch (error) {
                    await loadSummary().catch(function () {});
                    setMessage('Test failed: ' + error.message, 'bad');
                } finally {
                    done();
                }
            }

            async function runFullTranscode() {
                var button = byId('fullTranscodeButton');
                var done = setBusy(button, 'Running full job...');
                var nodeId = byId('testNodeSelect').value;

                if (!nodeId) {
                    setMessage('Save a worker first, then run the full transcode.', 'bad');
                    done();
                    return;
                }

                setMessage('Starting full distributed transcode. Watch Recent Jobs for progress.');
                try {
                    var result = await apiJson('/transcode-file', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            nodeId: nodeId,
                            sourcePath: byId('sourceInput').value,
                            outputPath: byId('fullOutputInput').value,
                            workerOutputDirectory: byId('workerTempInput').value,
                            chunkSizeSeconds: Number(byId('chunkSizeInput').value || 10),
                            videoCodec: 'libx264',
                            audioCodec: 'aac',
                            preset: 'veryfast',
                            crf: 28,
                            resolution: byId('resolutionInput').value || '640x360',
                            videoBitrateKbps: 1200,
                            preferHardwareAcceleration: false
                        })
                    });

                    await loadSummary();
                    setMessage('Full transcode succeeded. Final output on Pi: ' + getValue(result, 'outputPath', 'OutputPath', ''), 'good');
                } catch (error) {
                    await loadSummary().catch(function () {});
                    setMessage('Full transcode failed: ' + error.message, 'bad');
                } finally {
                    done();
                }
            }

            byId('refreshButton').addEventListener('click', refresh);
            byId('checkButton').addEventListener('click', checkWorker);
            byId('saveButton').addEventListener('click', saveWorker);
            byId('testButton').addEventListener('click', runTestJob);
            byId('fullTranscodeButton').addEventListener('click', runFullTranscode);
            byId('testNodeSelect').addEventListener('change', function () {
                selectedNodeId = byId('testNodeSelect').value;
            });

            window.setInterval(function () {
                loadSummary().catch(function () {});
            }, 5000);

            loadSummary()
                .then(function () {
                    setMessage('Ready. Check Worker, Save Worker, then Run Tiny Test Job.');
                })
                .catch(function (error) {
                    setMessage('Dashboard loaded, but summary failed: ' + error.message, 'bad');
                });
        })();
    </script>
</body>
</html>
""";
}
