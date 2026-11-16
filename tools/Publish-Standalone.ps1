param(
    [ValidateSet('win-arm64', 'win-x64')]
    [string]$RuntimeIdentifier = ('win-' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())
)

$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactDirectory = Join-Path $workspace 'artifacts'
$outputDirectory = Join-Path $workspace "dist/$RuntimeIdentifier"
[System.IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null
$arguments = @(
    'publish', (Join-Path $workspace 'DiskSpaceMap/DiskSpaceMap.csproj'),
    '-c', 'Release', '-r', $RuntimeIdentifier, '--self-contained', 'true',
    '-p:PublishProfile=Standalone', '-p:DebugType=embedded',
    "-p:RestoreConfigFile=$(Join-Path $workspace 'NuGet.publish.config')",
    '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$(Join-Path $artifactDirectory 'standalone')",
    '--output', $outputDirectory, "-bl:$(Join-Path $artifactDirectory 'publish-{}.binlog')"
)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Standalone publish failed (exit code $LASTEXITCODE)." }

$exe = Join-Path $outputDirectory 'DiskSpaceMap.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The standalone executable was not produced.' }
# Keep the original launch location current for this computer's architecture.
# This prevents an old framework-dependent apphost being mistaken for the release.
$nativeRuntime = 'win-' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($RuntimeIdentifier -eq $nativeRuntime) {
    $launcherDirectory = Join-Path $workspace 'DiskSpaceMap/bin/Release/net11.0-windows'
    [System.IO.Directory]::CreateDirectory($launcherDirectory) | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $launcherDirectory 'DiskSpaceMap.exe') -Force
}
Get-Item -LiteralPath $exe | Select-Object FullName, @{Name='SizeMiB'; Expression={[Math]::Round($_.Length / 1MB, 1)}}
