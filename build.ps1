[CmdletBinding()]
param([string]$OutputDirectory = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'artifacts\bin'
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null

$compiler = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $compiler) { throw 'Windows .NET Framework 4.x C# compiler (csc.exe) was not found.' }

# The framework version is defined only in Core/SoD2SE.Core.cs.
$versionSourceFile = Join-Path $PSScriptRoot 'Core\SoD2SE.Core.cs'
$match = [regex]::Match((Get-Content -LiteralPath $versionSourceFile -Raw),
    'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw 'FrameworkInfo.Version was not found in Core/SoD2SE.Core.cs.' }
$frameworkVersion = $match.Groups[1].Value
$parts = @(($frameworkVersion -split '-')[0].Split('.'))
if ($parts.Count -lt 1 -or $parts.Count -gt 4 -or @($parts | Where-Object { $_ -notmatch '^\d+$' }).Count -ne 0) {
    throw "Invalid framework version: $frameworkVersion"
}
while ($parts.Count -lt 4) { $parts += '0' }
$assemblyVersion = $parts -join '.'
$generated = Join-Path $output '.generated'
[IO.Directory]::CreateDirectory($generated) | Out-Null
$versionSource = Join-Path $generated 'AssemblyVersion.g.cs'
$attributes = @"
using System.Reflection;
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$frameworkVersion")]
"@
[IO.File]::WriteAllText($versionSource, $attributes, [Text.UTF8Encoding]::new($true))

$common = @('/nologo', '/platform:x64', '/highentropyva+', '/codepage:65001',
    '/optimize+', '/debug-', '/warn:4', '/warnaserror+', '/reference:System.dll',
    '/reference:System.Core.dll', '/reference:System.Numerics.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll')

function Get-Sources([string]$directory) {
    return @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $directory) -Filter '*.cs' -File |
        Sort-Object Name | Select-Object -ExpandProperty FullName)
}
function Assert-Build([string]$label) {
    if ($LASTEXITCODE -ne 0) { throw "$label failed with exit code $LASTEXITCODE" }
}

$core = Join-Path $output 'SoD2SE.Core.dll'
& $compiler @common '/target:library' "/out:$core" @(Get-Sources 'Core') $versionSource
Assert-Build 'Core build'

$gameApi = Join-Path $output 'SoD2SE.GameApi.dll'
& $compiler @common "/reference:$core" '/target:library' "/out:$gameApi" @(Get-Sources 'GameApi') $versionSource
Assert-Build 'Game API build'

$loader = Join-Path $output 'SoD2SE.Loader.exe'
& $compiler @common "/reference:$core" "/reference:$gameApi" '/target:winexe' "/out:$loader" @(Get-Sources 'Loader') $versionSource
Assert-Build 'Loader build'

Write-Output "Built $frameworkVersion in $output"
Write-Output 'No game process or game file was used by this build.'
