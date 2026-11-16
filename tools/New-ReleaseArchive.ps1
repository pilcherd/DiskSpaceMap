param(
    [ValidateSet('win-arm64', 'win-x64')]
    [string]$RuntimeIdentifier = ('win-' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())
)

$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $workspace 'artifacts'
$exe = Join-Path $workspace "dist/$RuntimeIdentifier/DiskSpaceMap.exe"
[xml]$project = Get-Content -LiteralPath (Join-Path $workspace 'DiskSpaceMap/DiskSpaceMap.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The project Version must be a three-part release version.' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Publish and verify the standalone executable first.' }
if ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne "$version.0") {
    throw 'The published executable version does not match the project. Publish it again.'
}

$releaseDirectory = Join-Path $artifacts 'release'
$stage = Join-Path $artifacts ('release-stage-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    Copy-Item -LiteralPath $exe -Destination (Join-Path $stage 'DiskSpaceMap.exe')
    foreach ($document in @('README.md', 'BUILDING.md', 'LICENSE', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $workspace $document) -Destination (Join-Path $stage $document)
    }
    [System.IO.Directory]::CreateDirectory((Join-Path $stage 'docs/images')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspace 'docs/images/treemap.png') -Destination (Join-Path $stage 'docs/images/treemap.png')

    # Use the exact Microsoft runtime packs selected during this publish.
    $assets = Get-Content -LiteralPath (Join-Path $artifacts 'standalone/obj/DiskSpaceMap/project.assets.json') -Raw | ConvertFrom-Json
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $artifacts "standalone/bin/DiskSpaceMap/release_$RuntimeIdentifier/DiskSpaceMap.runtimeconfig.json") -Raw | ConvertFrom-Json
    $licenses = Join-Path $stage 'licenses'
    [System.IO.Directory]::CreateDirectory($licenses) | Out-Null
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $package = ($framework.name + '.Runtime.' + $RuntimeIdentifier).ToLowerInvariant()
        $packageDirectory = $assets.packageFolders.PSObject.Properties.Name |
            ForEach-Object { Join-Path $_ "$package/$($framework.version)" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $packageDirectory) { throw "Runtime license package is missing: $package" }
        $license = @('LICENSE.TXT', 'LICENSE') | ForEach-Object { Join-Path $packageDirectory $_ } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $license) { throw "Runtime license is missing: $package" }
        Copy-Item -LiteralPath $license -Destination (Join-Path $licenses "$($framework.name)-LICENSE.txt")
        $notices = Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.TXT'
        if (Test-Path -LiteralPath $notices -PathType Leaf) {
            Copy-Item -LiteralPath $notices -Destination (Join-Path $licenses "$($framework.name)-THIRD-PARTY-NOTICES.txt")
        }
    }
    $zipName = "DiskSpaceMap-$version-$RuntimeIdentifier.zip"
    $zip = Join-Path $releaseDirectory $zipName
    if (Test-Path -LiteralPath $zip -PathType Leaf) { Remove-Item -LiteralPath $zip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $checksum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($zip + '.sha256', "$checksum  $zipName`n", [System.Text.UTF8Encoding]::new($false))
    Get-Item -LiteralPath $zip
} finally {
    $resolved = [System.IO.Path]::GetFullPath($stage)
    if ([System.IO.Path]::GetDirectoryName($resolved) -ne $artifacts -or
        -not [System.IO.Path]::GetFileName($resolved).StartsWith('release-stage-')) { throw 'Unexpected release staging cleanup target.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
