# Windows port parity checklist

Reference: published GPL source revision `9101d455991fac2cdf186e2073ce37a4c85505fe`.
Later proprietary releases are not part of this compatibility target.

Status values: **working** means implemented and covered by an automated check; **bootstrap**
means a Windows alternative is present but not yet feature-complete; **planned** means the
source behavior is inventoried and has an explicit implementation boundary.

## Foundation

| Area | Windows implementation | Status | Acceptance gate |
| --- | --- | --- | --- |
| Native shell | WinUI 3, Windows App SDK, Windows 11 x64 | working | Release build and clean launch |
| Editor state | `PalmierPro.Core`, UI-independent | working | Core tests pass without UI |
| Media ABI | CMake C++ library with optional FFmpeg linkage | bootstrap | Probe/decode fixture through the ABI |
| Preview | FFmpeg frame capture; D3D/HLSL boundary defined | bootstrap | Frame comparison against reference |
| Audio clock | Shared clock contract; WASAPI adapter boundary | bootstrap | A/V drift stays within one frame |
| Local ML | On-demand checksum store and ONNX model boundary | bootstrap | Beat and SigLIP parity thresholds |
| Packaging | Self-contained WinUI x64 output and installer boundary | planned | Clean-machine install/uninstall |

## Project and editing behavior

| Feature | Status | Test/next proof |
| --- | --- | --- |
| Source `project.json` decode | working | Source-shaped fixture |
| Legacy bare timeline decode | working | Legacy fixture |
| Media manifest and external/project sources | working | Round-trip fixture |
| Unicode and relative Windows paths | planned | Re-link and case-collision fixture |
| Staged package saves and recovery | working | Atomic replacement test |
| One undo entry per intent | working | Import/remove/failure/MCP tests |
| Trim, move, split, ripple/roll/slip | bootstrap | Add boundary and ripple tests |
| Tracks, linked clips, nesting, multicam | bootstrap | Add command fixtures |
| Markers and captions | bootstrap | Add caption timing fixtures |
| Keyframes and interpolation | working | Add golden sampling fixtures |
| Search | bootstrap | Text/media-name search fixture |
| Effects and grading data | bootstrap | HLSL image comparisons |

## Media, export, and intelligence

| Feature | Status | Windows replacement |
| --- | --- | --- |
| Probe, thumbnails, filmstrips, waveforms | bootstrap | FFmpeg/native media adapters |
| Preview/export render graph | bootstrap | D3D11 + HLSL shared graph |
| Video/audio mixing | planned | Native FFmpeg + WASAPI |
| H.264 and ProRes export profiles | bootstrap | FFmpeg export preflight/queue |
| XML/FCPXML | planned | C# exporter using immutable snapshot |
| Beat This and SigLIP 2 | bootstrap | ONNX Runtime conversion and parity checks |
| Speech/VAD/enhancement | planned | Windows-capable local adapters |
| OpenAI and Anthropic agent adapters | bootstrap | Independent API-key/provider tests |
| Codex account sign-in | bootstrap | Managed `codex app-server` process |
| Image/video/audio generation | planned | Capability-advertised provider adapters |

## Automation and release

| Feature | Status | Acceptance gate |
| --- | --- | --- |
| Default loopback MCP endpoint | working | Discovery test and origin rejection |
| MCP/UI shared dispatcher | working | MCP edits appear in undo history |
| Port conflict diagnostics | working | Actionable startup message |
| OAuth with PKCE | bootstrap | Documented provider callback test |
| Windows protected credentials | working | Credential save/read/remove smoke test |
| Cancellation and reconnection | bootstrap | Codex/provider failure tests |
| Performance benchmarks | planned | 1080p30 and 1,000-clip fixture |
| Accessibility and scaling scenarios | planned | Manual Windows verification sheet |
| Portable package and installer | planned | Clean Windows 11 VM |

The port is complete only when all source editing/rendering features have a working scenario,
the source project fixtures round-trip without semantic loss, independent AI routes are tested,
and the clean-machine installation gate passes. Bootstrap milestones are deliberately not labeled
as full parity.
