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
| `PalmierPro.Windows` | Earlier WPF compatibility prototype; retained as disposable scaffolding |
| `PalmierPro.Media` | CMake/C++ FFmpeg ABI and HLSL compositor baseline |
| `PalmierPro.Core.Tests` | Persistence, commands, undo/redo, package recovery, and MCP boundary tests |

## Build and run

```powershell
dotnet build Windows/PalmierPro.WinUI/PalmierPro.WinUI.csproj -c Release
dotnet run --project Windows/PalmierPro.WinUI/PalmierPro.WinUI.csproj -c Release
dotnet test Windows/PalmierPro.Core.Tests/PalmierPro.Core.Tests.csproj -c Release
```

The managed baseline uses `ffprobe` and `ffmpeg` from `PATH` for metadata and still-frame
preview. Install a Windows FFmpeg build, or pass an explicit executable path to the media
adapter in a host integration. The C++ library is built when FFmpeg development headers and
libraries are supplied through `PALMIER_FFMPEG_ROOT`:

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
- Saves stage a complete sibling package and replace the prior package only after the staged
  files are written. Interrupted backups are recovered on the next open.
- MCP listens on `http://127.0.0.1:19789/mcp` by default. It exposes timeline inspection,
  clip operations, split, undo, and redo through the same editor dispatcher. Requests with a
  browser `Origin` header are rejected.
- API keys are kept in Windows Credential Manager. Codex credentials remain owned by Codex;
  the app talks to its local `codex app-server` over documented stdio JSON-RPC.

## Compatibility boundary

The source baseline contains Apple-specific AVFoundation, Metal, Core ML, MLX, SwiftUI, and
AppKit implementations. Those are intentionally behind Windows adapters. HLSL and the C++
FFmpeg ABI are the native rendering/media boundary; FFmpeg CLI probing and still capture are
the working bootstrap alternative until the native decoder/render graph is complete. Unsupported
formats and unavailable provider capabilities must be reported before an operation starts.

See [`PortingChecklist.md`](PortingChecklist.md) for the feature-by-feature parity ledger and
the acceptance gates for the remaining rendering, captions, audio, AI, export, and packaging work.
