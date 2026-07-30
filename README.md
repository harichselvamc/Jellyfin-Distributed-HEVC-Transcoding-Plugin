# Jellyfin Distributed HEVC Transcoding Plugin

An experimental open source Jellyfin plugin for distributing HEVC transcoding jobs across multiple machines on the same local network.

The idea behind this project is to let a weaker Jellyfin host, such as a Raspberry Pi or low-power mini PC, coordinate transcoding while one or more stronger devices such as a laptop or desktop perform the expensive video encoding work.

## Project Status

This repository is currently in the design and scaffolding phase.

- The plugin is not implemented yet.
- The architecture and setup plan are defined here first.
- The goal of this repository is to become a real open source starting point for building distributed transcoding support around Jellyfin.

If you publish this repository today, it should be presented as an early-stage prototype project rather than a finished plugin.

## Why This Project Exists

Jellyfin transcoding can be heavy, especially for HEVC or 4K media. Many home servers are great for storage and streaming but not strong enough for repeated live transcoding.

This project explores a simple model:

- Jellyfin stays on the main server
- worker devices on the LAN advertise their capacity
- the transcode job is split into chunks
- chunks are sent to available workers
- completed segments are stitched back together for playback

This can make better use of hardware you already own instead of forcing everything through one machine.

## Planned Features

- Local network node discovery
- Manual node registration for stable setups
- Health checks for active worker nodes
- Capability reporting for CPU, memory, and hardware encoding
- Chunk-based distributed transcoding
- Segment recombination through FFmpeg
- Configurable worker priority and parallelism
- Future support for Intel Quick Sync, NVENC, and VAAPI
- Open source development with room for contributors

## Planned Repository Structure

```text
Jellyfin.Plugin.DistributedTranscode/
|- Jellyfin.Plugin.DistributedTranscode.csproj
|- Plugin.cs
|- Configuration/
|  |- PluginConfiguration.cs
|- Services/
|  |- MeshNodeService.cs
|  |- DistributeTranscodeService.cs
|  |- JobDistributor.cs
|  |- NetworkDiscovery.cs
|- Models/
|  |- NodeInfo.cs
|  |- SegmentRequest.cs
|  |- TranscodeJob.cs
|- Web/
|  |- TranscodeController.cs
```

## Planned Core Components

### `Plugin.cs`

The main Jellyfin plugin entry point. This is expected to:

- initialize services
- register background jobs or timers
- hook into the Jellyfin transcoding flow
- start health monitoring for connected worker nodes

### `PluginConfiguration.cs`

Holds the plugin configuration values, such as:

- `NodeName`
- `Port`
- `EnableDiscovery`
- `KnownNodes`
- `MaxParallelTasks`
- `IsMasterNode`
- `MasterNodeAddress`
- `ChunkSizeSeconds`
- `TranscodeSettings`

### `MeshNodeService.cs`

Responsible for network coordination between nodes, including:

- broadcasting node presence
- receiving discovery messages
- exposing worker endpoints
- checking worker health
- maintaining the active node list

### `DistributeTranscodeService.cs`

Responsible for the actual job flow:

- receiving a transcode request
- probing source media information
- splitting the media into chunks
- choosing workers
- sending each chunk to a worker
- collecting finished segments
- combining the final output

### Models

The models folder is expected to define shared contracts such as:

- node identity and capabilities
- transcode job details
- chunk request payloads
- segment results and errors

## Expected Workflow

1. Jellyfin receives a playback request that requires transcoding.
2. The plugin checks available worker nodes.
3. The source media is divided into time-based chunks.
4. Chunks are distributed across available nodes.
5. Each worker transcodes its chunk.
6. The coordinator collects completed segments.
7. FFmpeg concatenates the final media output.
8. Jellyfin serves the result to the client.

## Example Home Lab Setup

### Main Jellyfin Server

- Raspberry Pi
- Mini PC
- NAS-hosted Jellyfin instance

This machine is responsible for:

- library management
- playback requests
- session coordination
- dispatching chunk work to workers

### Worker Node

- Windows laptop
- Linux desktop
- spare mini PC

This machine is responsible for:

- receiving chunk jobs
- transcoding with CPU or GPU acceleration
- returning completed output segments

### Playback Client

- Android TV
- web browser
- tablet
- phone

The client does not need this plugin. It only streams from Jellyfin as usual.

## Development Setup

## Requirements

- .NET 8 SDK
- Jellyfin server for local testing
- FFmpeg installed and available in `PATH`
- Git
- at least two machines on the same network for realistic distributed testing

## Suggested Package Dependencies

The original design draft expects dependencies similar to:

- `Jellyfin.Controller`
- `Jellyfin.Model`
- `FFMpegCore`
- `Grpc.Net.Client`
- `Grpc.AspNetCore.Server`
- `Microsoft.Extensions.Hosting`
- `System.Threading.Tasks.Dataflow`

Version numbers should be aligned with the Jellyfin version you target when implementation begins.

## Local Setup Steps

1. Clone the repository:

```powershell
git clone https://github.com/<your-username>/jellyfin-distributed-hevc-transcoding-plugin.git
cd jellyfin-distributed-hevc-transcoding-plugin
```

2. Create the project scaffold:

```powershell
dotnet new classlib -n Jellyfin.Plugin.DistributedTranscode
```

3. Add the initial package references:

```powershell
dotnet add Jellyfin.Plugin.DistributedTranscode package Jellyfin.Controller
dotnet add Jellyfin.Plugin.DistributedTranscode package Jellyfin.Model
dotnet add Jellyfin.Plugin.DistributedTranscode package FFMpegCore
dotnet add Jellyfin.Plugin.DistributedTranscode package Grpc.Net.Client
dotnet add Jellyfin.Plugin.DistributedTranscode package Grpc.AspNetCore.Server
dotnet add Jellyfin.Plugin.DistributedTranscode package Microsoft.Extensions.Hosting
dotnet add Jellyfin.Plugin.DistributedTranscode package System.Threading.Tasks.Dataflow
```

4. Build the scaffold:

```powershell
dotnet build Jellyfin.Plugin.DistributedTranscode
```

5. Once implementation exists, copy the built plugin output into your Jellyfin plugins directory for testing.

Note: the exact plugin deployment path depends on your operating system and how Jellyfin is installed.

## Planned Configuration Example

The current draft design assumes settings similar to the following:

```json
{
  "NodeName": "Laptop-Node",
  "Port": 9090,
  "EnableDiscovery": true,
  "MaxParallelTasks": 4,
  "IsMasterNode": false,
  "MasterNodeAddress": "192.168.1.100:9090",
  "ChunkSizeSeconds": 60
}
```

This is only an example of the intended shape. The actual config schema may change during implementation.

## Deployment Plan

### Worker Node Deployment

On the stronger worker machine:

- install FFmpeg with HEVC support
- install the plugin or future worker companion component
- expose the worker endpoint on the chosen port
- allow the local firewall rule if required
- verify the machine can transcode independently

### Coordinator Deployment

On the main Jellyfin server:

- install the plugin
- enable discovery or manually list worker nodes
- set chunk size and parallel limits
- route eligible jobs through the distributed workflow

### Clients

Clients such as Android TV or browsers do not need special setup. They continue to consume media through Jellyfin normally.

## Hardware Acceleration Direction

Hardware acceleration is planned but not implemented yet.

Possible future targets include:

- Intel Quick Sync
- NVIDIA NVENC
- VAAPI on Linux
- software fallback when hardware encoding is unavailable

An example future FFmpeg direction for Intel Quick Sync could look like:

```csharp
var ffmpegArgs = $"-hwaccel qsv " +
                 $"-i \"{input}\" " +
                 $"-c:v h264_qsv " +
                 $"-preset fast " +
                 $"-global_quality 23 " +
                 $"\"{output}\"";
```

The exact command line will depend on the host OS, installed drivers, and target codec pipeline.

## Security Considerations

Distributed transcoding should not be exposed casually without safeguards.

Before production use, this project should include:

- trusted node authentication
- signed or token-based worker requests
- allowlisted worker registration
- validation of media paths and job payloads
- timeout and retry controls
- encrypted transport where practical
- logging and auditability for node actions

## Risks And Engineering Challenges

This idea is promising, but there are real technical challenges:

- live transcoding has tighter timing constraints than offline batch work
- segment boundaries can introduce playback issues if not handled carefully
- some formats do not stitch cleanly without extra processing
- Jellyfin integration may require deeper hooks than a basic plugin alone
- worker availability changes can interrupt active playback sessions

Because of that, this project should be treated as experimental until there is a stable end-to-end prototype.

## Open Source Roadmap

- create the plugin project scaffold
- implement configuration classes
- implement node discovery and health checks
- add worker registration and capability reporting
- build chunk dispatch and result collection
- add segment recombination
- integrate with Jellyfin transcoding flow
- add tests and sample configuration
- document installation and troubleshooting
- publish tagged releases

## Contributing

Contributions are welcome, especially once the initial scaffold is committed.

Useful contribution areas include:

- Jellyfin plugin architecture
- FFmpeg job handling
- networking and service discovery
- fault tolerance and retries
- configuration UX
- packaging and release automation
- documentation and testing

If you open this repository to the public, add clear issues for early tasks so contributors know where to start.

## Publishing This Repository

Before pushing this to GitHub as open source, it is a good idea to add:

- a `LICENSE` file
- a `.gitignore`
- an initial project scaffold
- a short `CONTRIBUTING.md`
- sample screenshots or diagrams later, if the implementation grows

For a permissive open source release, `MIT` is a simple default license choice.

## License

No license file is included yet.

If you want others to use, fork, and contribute to the project safely, add a license before public release.

## Disclaimer

This project is an experimental concept and is not ready for production use. It is intended as an open source starting point for exploring distributed transcoding around Jellyfin, not as a finished plugin today.
