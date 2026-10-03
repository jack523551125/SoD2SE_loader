[CmdletBinding()]
param(
    [string]$BuildDirectory = '',
    [string]$OutputDirectory = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $workspace = Split-Path -Parent (Split-Path -Parent $repositoryRoot)
    if (Test-Path -LiteralPath (Join-Path $workspace 'workspace.toml')) { $BuildDirectory = Join-Path $workspace '.work/products/SoD2SE-Loader' }
    else { $BuildDirectory = Join-Path $repositoryRoot '.work/build' }
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $BuildDirectory 'packages' }
$build = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BuildDirectory))
$output = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory))
& (Join-Path $repositoryRoot 'build.ps1') -OutputDirectory $build
if ($LASTEXITCODE -ne 0) { throw "SoD2SE-Loader build failed ($LASTEXITCODE)." }
$versionSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Core/SoD2SE.Core.cs') -Raw
$match = [regex]::Match($versionSource, 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw 'FrameworkInfo.Version was not found.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$archive = Join-Path $output ('SoD2SE-Loader-v' + $match.Groups[1].Value + '.zip')
if (Test-Path -LiteralPath $archive) { throw "Refusing to overwrite an existing package: $archive" }
$files = @('SoD2SE.Loader.exe','SoD2SE.Core.dll','SoD2SE.GameApi.dll') | ForEach-Object { Join-Path $build $_ }
foreach ($file in $files) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release input: $file" } }
Compress-Archive -LiteralPath $files -DestinationPath $archive
Write-Output "PACKAGE $archive"
