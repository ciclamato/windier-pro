# Palmier Pro for Windows

This directory is the Windows-native port workspace. The original application is a macOS SwiftUI/AppKit editor, so Apple UI and media frameworks cannot be reused directly on Windows.

## Current milestone

The `PalmierPro.Windows` WPF app provides a small working foundation:

- import media through a file picker or drag-and-drop;
- preview the selected clip with the Windows media stack;
- remove clips and preserve their order;
- save and load a portable `.palmier-windows.json` project file.

## Build and run

From PowerShell:

```powershell
dotnet build Windows/PalmierPro.Windows/PalmierPro.Windows.csproj -c Release
dotnet run --project Windows/PalmierPro.Windows/PalmierPro.Windows.csproj
```

The macOS Swift application remains unchanged and can still be built using the instructions in the repository root.

## Porting boundary

The next Windows milestones should move the shared timeline and project model into a platform-neutral library, then add native Windows implementations for decoding, compositing, audio, export, and GPU effects. The existing Metal shaders and AVFoundation services require separate Direct3D/Media Foundation or FFmpeg implementations.
