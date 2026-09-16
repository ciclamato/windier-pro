# Palmier Pro for Windows

This is the Windows-first port of the published Palmier source baseline at
`last-gpl-source` (`9101d455991fac2cdf186e2073ce37a4c85505fe`). The macOS app remains
unchanged. Windows uses a native WinUI 3 shell, a UI-independent C# editor core, and
replaceable media/provider adapters.

The interface intentionally uses an off-white canvas, graphite surfaces, fine translucent
lines, and a restrained violet wash: an ethereal Venice/Hermes-like workspace rather than
a bright-white utility window.

## Projects

| Project | Purpose |
| --- | --- |
| `PalmierPro.Core` | Source-compatible models, serialization, commands, transactional undo/redo, package storage, media probing, export planning, AI contracts, Codex client, and MCP server |
| `PalmierPro.WinUI` | Windows 11 x64 native UI, drag/drop import, FFmpeg frame preview, project open/save, agent connections, MCP lifecycle |
| `PalmierPro.Windows` | Legacy WPF compatibility host for older Windows project files |
| `PalmierPro.Media` | CMake/C++ FFmpeg ABI and HLSL compositor baseline |
| `Packaging` | Reproducible self-contained portable x64 package and optional FFmpeg bundling |
| `PalmierPro.Core.Tests` | Persistence, commands, undo/redo, package recovery, and MCP boundary tests |

## Build and run

```powershell
dotnet build Windows/PalmierPro.WinUI/PalmierPro.WinUI.csproj -c Release
dotnet run --project Windows/PalmierPro.WinUI/PalmierPro.WinUI.csproj -c Release
dotnet test Windows/PalmierPro.Core.Tests/PalmierPro.Core.Tests.csproj -c Release
```

The managed media path uses `ffprobe` and `ffmpeg` from `PATH` for metadata, frame preview, and
multi-clip export. Install a Windows FFmpeg build, or pass an explicit executable path to the
adapter in a host integration. The C++ library is built with native FFmpeg headers and
libraries when `PALMIER_FFMPEG_ROOT` is supplied:

```powershell
cmake -S Windows/PalmierPro.Media -B Windows/PalmierPro.Media/build -DPALMIER_FFMPEG_ROOT=C:/ffmpeg
cmake --build Windows/PalmierPro.Media/build --config Release
```

## Current guarantees

- `.palmier` projects are directory packages with `project.json`, `media.json`, and `media/`.
- Source-style project JSON and legacy bare timelines are decoded without changing IDs or
  frame-based timing.
- Commands validate before commit, restore state after failed commands, and create one undo
  entry per intent. Timeline and media-manifest mutations share the same undo stack.
- The timeline supports clip selection, move, edge trim, split, ripple delete, click-to-seek,
  shared-clock playback, clip properties, SRT/VTT caption cues, and media/caption search.
- Saves stage a complete sibling package and replace the prior package only after the staged
  files are written. Interrupted backups are recovered on the next open.
- MCP listens on `http://127.0.0.1:19789/mcp` by default. It exposes timeline inspection,
  clip operations, split, undo, and redo through the same editor dispatcher. Requests with a
  browser `Origin` header are rejected.
- API keys are kept in Windows Credential Manager. Codex credentials remain owned by Codex;
  the app talks to its local `codex app-server` over documented stdio JSON-RPC.
- Audio output can be selected at runtime from WASAPI shared, WASAPI exclusive, or installed
  ASIO drivers. ASIO4ALL is detected when its own driver is already installed; it is never
  bundled by Palmier.
- H.264/ProRes exports compose layered clips and standalone audio through FFmpeg; the Export
  menu also writes XMEML and FCPXML interchange files.

## Compatibility boundary

The source baseline contains Apple-specific AVFoundation, Metal, Core ML, MLX, SwiftUI, and
AppKit implementations. Those are behind Windows adapters. HLSL and the C++ FFmpeg ABI are the
native rendering/media boundary; the managed FFmpeg path is the supported fallback when native
FFmpeg development libraries are not installed. Unsupported formats and unavailable provider
capabilities are reported before an operation starts.

See [`PortingChecklist.md`](PortingChecklist.md) for the feature-by-feature parity ledger and
the acceptance gates for the remaining rendering, captions, audio, AI, export, and packaging work.
The portable release script is documented in [`Packaging/README.md`](Packaging/README.md).
