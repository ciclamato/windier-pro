# Windows packaging

`Publish-Windows.ps1` creates a reproducible, self-contained Windows 11 x64 directory and ZIP.
It can also bundle `ffmpeg.exe` and `ffprobe.exe` when `-FfmpegDirectory` points to a licensed
FFmpeg distribution directory.

```powershell
pwsh Windows/Packaging/Publish-Windows.ps1 -FfmpegDirectory C:/tools/ffmpeg/bin
```

The current WinUI host is intentionally unpackaged, so the portable artifact does not require
administrator rights or a machine-wide install. A signed installer/MSIX is a separate release
step because it requires the publisher's signing identity and its chosen installer toolchain.
