[CmdletBinding()]
param([string]$BuildDirectory = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    if (Test-Path -LiteralPath (Join-Path $workspace 'workspace.toml')) {
        $BuildDirectory = Join-Path $workspace '.work/products/SoD2SE-Loader'
    } else {
        $BuildDirectory = Join-Path $PSScriptRoot '.work/build'
    }
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BuildDirectory)
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $output
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

function Invoke-OfflineTest([string]$executable, [string]$arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $executable
    $start.Arguments = $arguments
    $start.WorkingDirectory = $output
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "Could not start offline test: $executable" }
    try {
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            throw "Offline test timed out: $executable"
        }
        if ($process.ExitCode -ne 0) {
            throw "Offline test failed ($($process.ExitCode)): $executable"
        }
    } finally {
        $process.Dispose()
    }
}

Invoke-OfflineTest (Join-Path $output 'SoD2SE.Loader.exe') '--self-test'

$compiler = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
$mcmSmoke = Join-Path $output 'McmSmoke.exe'
& $compiler '/nologo' '/platform:x64' '/codepage:65001' '/warnaserror+' "/reference:$(Join-Path $output 'SoD2SE.Core.dll')" "/out:$mcmSmoke" (Join-Path $PSScriptRoot 'Tests\McmSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'MCM smoke test build failed.' }
Invoke-OfflineTest $mcmSmoke ''
Write-Output 'PASS: loader self-test and MCM language/configuration smoke test; game was not launched.'
