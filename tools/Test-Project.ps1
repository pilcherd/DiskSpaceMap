param([switch]$Render)

$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $workspace 'artifacts'
$validated = Join-Path $artifacts 'validated'
[System.IO.Directory]::CreateDirectory($artifacts) | Out-Null
Push-Location $workspace
try {
    & dotnet build (Join-Path $workspace 'DiskSpaceMap.slnx') -c Release `
        '-p:UseArtifactsOutput=true' "-p:ArtifactsPath=$validated" `
        "-p:RestoreConfigFile=$(Join-Path $workspace 'NuGet.Config')" `
        "-bl:$(Join-Path $artifacts 'build-{}.binlog')"
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

    & dotnet (Join-Path $validated 'bin/DiskSpaceMap.Checks/release/DiskSpaceMap.Checks.dll')
    if ($LASTEXITCODE -ne 0) { throw "Scanner checks failed (exit code $LASTEXITCODE)." }

    $renderArguments = @()
    if ($Render) { $renderArguments += '--render' }
    & dotnet (Join-Path $validated 'bin/DiskSpaceMap.RenderChecks/release/DiskSpaceMap.RenderChecks.dll') @renderArguments
    if ($LASTEXITCODE -ne 0) { throw "Shell/render checks failed (exit code $LASTEXITCODE)." }
} finally { Pop-Location }
