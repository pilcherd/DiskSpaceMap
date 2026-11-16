param(
    [ValidateSet('win-arm64', 'win-x64')]
    [string]$RuntimeIdentifier = ('win-' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())
)

$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $workspace 'artifacts'
$exe = Join-Path $workspace "dist/$RuntimeIdentifier/DiskSpaceMap.exe"
$hook = Join-Path $artifacts 'validated/bin/DiskSpaceMap.RenderChecks/release/DiskSpaceMap.RenderChecks.dll'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Publish the standalone executable first.' }
if (-not (Test-Path -LiteralPath $hook -PathType Leaf)) { throw 'Build RenderChecks using the artifacts/validated output directory first.' }

# Confirm both application assemblies really contain native ReadyToRun code.
foreach ($assembly in @('DiskSpaceMap', 'DiskSpaceMap.Core')) {
    $path = Join-Path $artifacts "standalone/obj/DiskSpaceMap/release_$RuntimeIdentifier/R2R/$assembly.dll"
    $stream = [System.IO.File]::OpenRead($path)
    $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $directory = $reader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory
        if ($directory.Size -eq 0) { throw "$assembly has no ReadyToRun header." }
        $data = $reader.GetSectionData($directory.RelativeVirtualAddress).GetReader()
        if ($data.ReadUInt32() -ne 0x00525452) { throw "$assembly has no ReadyToRun signature." }
        Write-Output "PASS  $assembly contains native ReadyToRun code ($RuntimeIdentifier)."
    } finally { $reader.Dispose(); $stream.Dispose() }
}

$extractionRoot = Join-Path $artifacts ('bundle-check-' + [guid]::NewGuid().ToString('N'))
$start = [System.Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['DOTNET_STARTUP_HOOKS'] = $hook
$start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extractionRoot
$start.Environment['DOTNET_DISABLE_GUI_ERRORS'] = '1'
$start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_ARM64', 'DOTNET_ROOT_X64')) {
    $start.Environment[$name] = Join-Path $artifacts 'unavailable-runtime'
}
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'Standalone verification could not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'Standalone verification timed out.'
    }
    $output = $stdout.GetAwaiter().GetResult()
    $errors = $stderr.GetAwaiter().GetResult()
    [System.IO.File]::WriteAllText((Join-Path $artifacts "verify-$RuntimeIdentifier.log"), $output + $errors)
    Write-Output $output
    if ($process.ExitCode -ne 0) { throw "Standalone verification failed: $errors" }
} finally {
    $process.Dispose()
    # This cache contains only native libraries extracted by the tested executable.
    # Verify the exact generated location before any recursive cleanup.
    $resolved = [System.IO.Path]::GetFullPath($extractionRoot)
    if ([System.IO.Path]::GetDirectoryName($resolved) -ne $artifacts -or
        -not [System.IO.Path]::GetFileName($resolved).StartsWith('bundle-check-')) {
        throw 'Unexpected verification cleanup target.'
    }
    if (Test-Path -LiteralPath $resolved) {
        if (Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }) {
            throw 'Unexpected link in bundle extraction directory.'
        }
        # Image sections and antivirus scans can briefly retain extracted DLLs
        # after the process handle signals exit, especially under x64 emulation.
        for ($attempt = 0; ; $attempt++) {
            try { Remove-Item -LiteralPath $resolved -Recurse -Force; break }
            catch {
                if ($attempt -ge 9) { throw }
                Start-Sleep -Milliseconds 200
            }
        }
    }
}
