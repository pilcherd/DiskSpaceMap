$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $workspace 'artifacts'
if (-not (Test-Path -LiteralPath $artifacts -PathType Container)) { return }
$artifacts = (Resolve-Path -LiteralPath $artifacts).Path
if (-not $artifacts.StartsWith($workspace + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Artifacts directory is outside this workspace.'
}

$targets = @(Get-ChildItem -LiteralPath $artifacts -Directory | Where-Object {
    $_.Name -match '^(check-fixture|render-fixture)-[0-9a-f]{32}$' -or $_.Name -eq 'render-checks'
})
$files = 0L
$logicalBytes = 0L
foreach ($target in $targets) {
    $resolved = (Resolve-Path -LiteralPath $target.FullName).Path
    if ([System.IO.Path]::GetDirectoryName($resolved) -ne $artifacts -or
        ($target.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing an unexpected cleanup target: $resolved"
    }
    $pending = [System.Collections.Generic.Stack[object]]::new()
    $pending.Push(@{ Path = $resolved; Visited = $false })
    while ($pending.Count -gt 0) {
        $item = $pending.Pop()
        if ($item.Visited) { [System.IO.Directory]::Delete($item.Path); continue }
        $pending.Push(@{ Path = $item.Path; Visited = $true })
        foreach ($entry in [System.IO.DirectoryInfo]::new($item.Path).EnumerateFileSystemInfos()) {
            if (-not ($entry.Attributes -band [System.IO.FileAttributes]::Directory)) {
                $files++
                if (-not ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { $logicalBytes += $entry.Length }
                $entry.Delete()
            } elseif ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                # Unlink the fixture's junction without traversing its target.
                [System.IO.Directory]::Delete($entry.FullName)
            } else {
                $pending.Push(@{ Path = $entry.FullName; Visited = $false })
            }
        }
    }
}
[pscustomobject]@{ RemovedDirectories = $targets.Count; RemovedFiles = $files; LogicalBytes = $logicalBytes }
