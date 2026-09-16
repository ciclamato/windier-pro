[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$FfmpegDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$packageName = "PalmierPro-win-x64-$Configuration"
$packageRoot = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
if (Test-Path -LiteralPath $packageRoot) {
    $resolvedRoot = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not ([IO.Path]::GetFullPath($packageRoot).StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing to remove a package path outside the output directory."
    }
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

$project = Join-Path $repoRoot 'Windows\PalmierPro.WinUI\PalmierPro.WinUI.csproj'
& dotnet publish $project -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

if (-not [string]::IsNullOrWhiteSpace($FfmpegDirectory)) {
    $ffmpegRoot = (Resolve-Path $FfmpegDirectory).Path
    foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
        $source = Join-Path $ffmpegRoot $tool
        if (-not (Test-Path -LiteralPath $source)) { throw "FFmpeg bundle is missing $tool." }
        Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $tool)
    }
}

@"
Palmier Pro for Windows

Run PalmierPro.WinUI.exe.
Projects are saved as .palmier directory packages.
FFmpeg is optional for editing but required for media probing, preview, and rendered export.
This package is self-contained and targets Windows 11 x64.
"@ | Set-Content -LiteralPath (Join-Path $packageRoot 'README.txt') -Encoding UTF8

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output "Portable package: $zipPath"
