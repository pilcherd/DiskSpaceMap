# Building and releasing

DiskSpaceMap uses .NET 11 and WPF. Build and test it on Windows with a .NET SDK
compatible with `global.json` (currently 11.0.100). PowerShell 7 is used by the
scripts and GitHub Actions.

## Build and test

From the repository root:

```powershell
./tools/Test-Project.ps1
```

This builds all four projects in Release mode and runs the scanner and
Shell/render checks. The latter render WPF controls in memory; they do not open
windows, send desktop input, or scan your actual drives. Test fixtures live in
unique temporary directories and are removed on success and failure. To also
save sample map PNGs under `artifacts/render-checks`, add `-Render`.

Outputs and uniquely named MSBuild binary logs are kept in `artifacts/`.
Ordinary builds use the offline `NuGet.Config`; no third-party application
packages are needed.

To regenerate the README image from the real `C:\Program Files` directory,
render the map offscreen after building the checks:

```powershell
dotnet artifacts/validated/bin/DiskSpaceMap.RenderChecks/release/DiskSpaceMap.RenderChecks.dll --render-folder 'C:\Program Files' docs/images/treemap.png
```

This reads directory metadata and writes only the output PNG. It does not
display or control a desktop window.

## Publish standalone executables

```powershell
./tools/Publish-Standalone.ps1 -RuntimeIdentifier win-x64
./tools/Publish-Standalone.ps1 -RuntimeIdentifier win-arm64
```

Each command produces one `DiskSpaceMap.exe` in `dist/<runtime>/`. Publishing
for the current computer's architecture also updates the original launch path,
`DiskSpaceMap/bin/Release/net11.0-windows/DiskSpaceMap.exe`, with that same EXE.
Close any running copy before replacing it. Normal development builds can
produce a framework-dependent launcher; use the publish script for distribution.

Publishing uses `NuGet.publish.config` to obtain Microsoft's runtime packs and
ReadyToRun compiler from NuGet.org. The first publish needs network access.
ReadyToRun is ahead-of-time compilation with JIT optimization still available;
WPF does not support full Native AOT or trimming. The profile deliberately
extracts all bundled files so mixed managed/native WPF graphics libraries load
correctly. The SDK's NETSDK1244 compatibility-mode warning is expected for this
profile. Compression is disabled inside the EXE to avoid startup decompression.

## Verify the packaged executable

After building the checks and publishing:

```powershell
./tools/Verify-Standalone.ps1 -RuntimeIdentifier win-x64
./tools/Verify-Standalone.ps1 -RuntimeIdentifier win-arm64
```

Verification inspects the native ReadyToRun headers, redirects runtime lookup
away from installed .NET runtimes, and runs the Shell/render checks inside the
packaged executable using a test-only startup hook. The hook exits before the
normal app entry point. No window is shown. Extraction caches are cleaned up.
Run each architecture on a Windows machine that can execute it; CI uses a native
runner for each architecture.

## Make a release archive

```powershell
./tools/New-ReleaseArchive.ps1 -RuntimeIdentifier win-x64
./tools/New-ReleaseArchive.ps1 -RuntimeIdentifier win-arm64
```

Archives and SHA-256 files appear in `artifacts/release/`, named
`DiskSpaceMap-1.0.0-win-x64.zip` and `DiskSpaceMap-1.0.0-win-arm64.zip` for the
initial release. They contain the executable, README, changelog, project MIT
license, and the exact bundled .NET runtime licenses and third-party notices.
Keep those license files with redistributed archives.

## GitHub Actions

- `build.yml`: runs on branch pushes, pull requests, and manual dispatch. Native
  Windows x64 and ARM64 jobs build, test, publish, verify, and upload release ZIPs
  and checksums. Artifacts expire after three days to limit storage use.
- `release.yml`: runs on a `v*` tag, checks it matches the project version,
  calls the build workflow, then publishes a GitHub release with both
  verified packages. Only the release-publishing job has repository write permission.
- Actions are pinned to checked commit SHAs. Builds use the SDK in `global.json`.

The first public version is **1.0.0** in `DiskSpaceMap/DiskSpaceMap.csproj`.
The window title and release archive names derive from the assembly/project
version rather than separate hard-coded release strings.

When you are ready to publish, commit and push the source to
<https://github.com/pilcherd/DiskSpaceMap>, then create and push a matching tag:

```powershell
git tag -a v1.0.0 -m "DiskSpaceMap 1.0.0"
git push origin v1.0.0
```

The tag workflow publishes the GitHub release after both architecture builds and
all verification steps pass. Inspect the completed Actions run and release assets. Preparing these files locally does not push source,
create a tag, run GitHub Actions, or publish a release.
