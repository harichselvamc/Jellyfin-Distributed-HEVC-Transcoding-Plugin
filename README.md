<div align="center">

# Jellyfin Distributed HEVC Transcoding

Experimental Jellyfin plugin for sending heavy transcode work from a small Jellyfin server to stronger worker machines on the same local network.

<strong>Status:</strong> early prototype, under active development

</div>

<br>

<table>
  <tr>
    <td><strong>Target Jellyfin</strong></td>
    <td>10.11.11</td>
  </tr>
  <tr>
    <td><strong>Target .NET</strong></td>
    <td>.NET 9</td>
  </tr>
  <tr>
    <td><strong>Main test setup</strong></td>
    <td>Jellyfin on Raspberry Pi, Windows laptop as worker</td>
  </tr>
  <tr>
    <td><strong>Project type</strong></td>
    <td>Open source prototype</td>
  </tr>
</table>

## Goal

The goal is simple:

Use a stronger computer on the local network to do transcoding work, so a small Jellyfin server like a Raspberry Pi does not carry the full CPU load.

Example:

- Raspberry Pi runs Jellyfin.
- Windows laptop runs the worker app.
- Jellyfin plugin sends transcode chunks to the laptop.
- The laptop runs FFmpeg.
- The plugin receives the output back.

This project is still under development. It needs more time before it can be used like a normal finished Jellyfin plugin.

## Current Status

This is not production ready.

It is a working prototype for testing the distributed transcode idea.

### Working Now

- Jellyfin plugin loads in Jellyfin `10.11.11`.
- Windows worker app runs on port `9090`.
- Dashboard is available at `/distributed-transcode/dashboard`.
- Plugin can check if a worker is online.
- Plugin can save worker devices.
- Saved workers can survive Jellyfin restart.
- Pi can send a small test transcode chunk to the Windows worker.
- Worker runs FFmpeg and creates output.
- Dashboard shows connected devices and recent jobs.
- Basic retries, timeout handling, and worker request signing support exist.
- Full-file distributed transcode endpoint has been started.

### Tested So Far

The project has been tested with:

- Jellyfin running in Docker on Raspberry Pi.
- Windows laptop as worker.
- FFmpeg installed on Windows.
- Manual worker registration.
- Tiny chunk transcode test from Pi to Windows worker.

The small test job successfully created output on the Windows worker.

## Not Working Yet

Normal Jellyfin movie playback is not automatically distributed yet.

If you play a movie in Jellyfin today, Jellyfin still uses its normal FFmpeg flow on the Jellyfin server.

The dashboard test is real worker dispatch, but it is not yet connected into Jellyfin's automatic playback transcoding path.

## Important Limitation

This plugin currently proves that worker dispatch is possible.

It does not yet replace Jellyfin's built-in live playback transcoder.

To make automatic playback work, the next major step is an FFmpeg wrapper or deeper Jellyfin playback integration.

## Planned Automatic Playback Design

The planned approach is:

1. Jellyfin starts a transcode job.
2. Instead of running normal FFmpeg directly, Jellyfin calls a wrapper.
3. The wrapper checks if distributed transcoding is possible.
4. If yes, it sends work to worker devices.
5. If no, it falls back to normal Jellyfin FFmpeg.
6. The viewer should still play media normally in Jellyfin.

This is the hardest part of the project and needs careful development.

## Dashboard

The dashboard uses a simple professional black and white style.

Open it from the Jellyfin server:

```text
http://<jellyfin-server-ip>:8096/distributed-transcode/dashboard
```

Example:

```text
http://192.168.1.5:8096/distributed-transcode/dashboard
```

Dashboard features:

- Worker health check
- Save worker
- Connected device list
- Recent job list
- Tiny test job
- Full-file distributed transcode test area

## Repository Structure

```text
Jellyfin.Plugin.DistributedTranscode/
  Plugin.cs
  PluginServiceRegistrator.cs
  Configuration/
  Models/
  Services/
  Security/
  Web/

DistributedTranscode.Worker/
  Program.cs
  appsettings.json
```

## Requirements

### Jellyfin Server

- Jellyfin `10.11.11`
- .NET 9 compatible plugin build
- FFmpeg available in the Jellyfin container or host

### Worker Machine

- .NET 9 SDK or runtime
- FFmpeg in `PATH`
- FFprobe in `PATH`
- Network access from Jellyfin server to worker
- Port `9090` open on the worker firewall

## Build

From the repository root:

```powershell
dotnet restore
dotnet build Jellyfin.Plugin.DistributedTranscode/Jellyfin.Plugin.DistributedTranscode.csproj -c Release
dotnet build DistributedTranscode.Worker/DistributedTranscode.Worker.csproj -c Release
```

On Windows CMD:

```cmd
dotnet restore
dotnet build "Jellyfin.Plugin.DistributedTranscode\Jellyfin.Plugin.DistributedTranscode.csproj" -c Release
dotnet build "DistributedTranscode.Worker\DistributedTranscode.Worker.csproj" -c Release
```

## Run Worker

On the worker machine:

```cmd
dotnet run --project "DistributedTranscode.Worker\DistributedTranscode.Worker.csproj" -c Release
```

Check worker health:

```cmd
curl http://127.0.0.1:9090/distributed-transcode/health
```

From the Jellyfin server, check the worker:

```bash
curl http://<worker-ip>:9090/distributed-transcode/health
```

## Manual Plugin Install

Build output is here:

```text
Jellyfin.Plugin.DistributedTranscode/bin/Release/net9.0
```

Copy those files into the Jellyfin plugin folder.

Example Docker install path:

```text
/home/admin/media-server/jellyfin/config/plugins/DistributedTranscode
```

Example copy from Windows to Pi:

```cmd
scp -r "D:\Jellyfin Distributed HEVC Transcoding Plugin\Jellyfin.Plugin.DistributedTranscode\bin\Release\net9.0\*" admin@192.168.1.5:/tmp/distributed-transcode
```

On the Pi:

```bash
sudo mkdir -p /home/admin/media-server/jellyfin/config/plugins/DistributedTranscode
sudo cp -r /tmp/distributed-transcode/* /home/admin/media-server/jellyfin/config/plugins/DistributedTranscode/
docker restart jellyfin
```

## Test Order

Use this order when testing:

1. Start the Windows worker.
2. Restart Jellyfin after copying the plugin.
3. Open the dashboard.
4. Click `Check Worker`.
5. Click `Save Worker`.
6. Click `Run Tiny Test Job`.
7. Watch the worker terminal for `/chunk`.
8. Check the output file on the worker.

If full-file test is used, also watch for:

```text
/probe
/chunk
/file
```

## Current API Endpoints

Plugin endpoints:

```text
GET  /distributed-transcode/health
GET  /distributed-transcode/nodes
GET  /distributed-transcode/summary
GET  /distributed-transcode/dashboard
POST /distributed-transcode/nodes
POST /distributed-transcode/check-worker
POST /distributed-transcode/test-job
POST /distributed-transcode/transcode-file
POST /distributed-transcode/chunk
```

Worker endpoints:

```text
GET  /distributed-transcode/health
POST /distributed-transcode/chunk
POST /distributed-transcode/probe
GET  /distributed-transcode/file
```

## What Is Pending

Main pending work:

- Automatic Jellyfin playback integration.
- FFmpeg wrapper mode.
- Better path mapping between Jellyfin paths and worker paths.
- Better worker delete and edit buttons.
- Better duplicate worker handling.
- Stronger shared secret setup in the dashboard.
- More progress details for long jobs.
- Better full-file transcode testing.
- Safer cleanup of temporary chunk files.
- Better packaging for GitHub releases.
- GitHub Actions build workflow.
- More documentation for Linux workers.

## Development Roadmap

### Phase 1: Prototype

Status: mostly done.

- Plugin loads in Jellyfin.
- Worker app runs.
- Health checks work.
- Manual worker save works.
- Tiny chunk dispatch works.
- Dashboard exists.

### Phase 2: Full File Pipeline

Status: in progress.

- Probe media duration.
- Split media into chunks.
- Send chunks to workers.
- Download finished chunks.
- Combine output on Jellyfin server.
- Show progress in dashboard.

### Phase 3: Automatic Playback

Status: pending.

- Add FFmpeg wrapper or deeper Jellyfin integration.
- Detect when Jellyfin wants transcoding.
- Route eligible jobs to distributed workers.
- Fall back to normal FFmpeg when needed.
- Keep playback stable for real users.

### Phase 4: Public Release

Status: pending.

- Add release package.
- Add GitHub Actions.
- Add install guide.
- Add troubleshooting guide.
- Add screenshots.
- Add contributor tasks.

## Security Notes

Do not expose the worker port to the public internet.

Keep this on a trusted local network only.

Security work still needs improvement before production use:

- Better shared secret setup.
- Worker allowlist.
- Safer file path validation.
- Better logs.
- Better cleanup.
- Optional HTTPS support.

## Project Impact

This project can be useful if it becomes stable.

It could help people who run Jellyfin on small home servers but also have a stronger laptop, desktop, or mini PC available on the same network.

The idea has real value, but the hardest part is automatic live playback integration. That part is still pending and needs time.

## Contributing

Contributions are welcome.

Good areas to help:

- Jellyfin plugin internals
- FFmpeg command handling
- Windows and Linux worker support
- Transcode progress tracking
- Dashboard UI
- Testing on real home servers
- Documentation

Please keep issues and pull requests simple and clear.

## License

This project is released under the MIT License.

See `LICENSE`.

## Disclaimer

This is experimental software.

It is not ready for production Jellyfin servers.

Use it for development, testing, and learning only.
