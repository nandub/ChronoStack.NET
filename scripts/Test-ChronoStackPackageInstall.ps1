<#
.SYNOPSIS
Smoke-tests a packed ChronoStack NuGet package from a temporary consumer project.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [string]$PackagePath,

    [Parameter(Mandatory = $false)]
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'

function Invoke-Checked {
    param (
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageDir = Join-Path $repoRoot 'src\ChronoStack\bin\Release'

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $packages = Get-ChildItem -LiteralPath $packageDir -Filter 'ChronoStack.*.nupkg' | Sort-Object LastWriteTimeUtc -Descending
    if ($packages.Count -eq 0) {
        throw "No ChronoStack .nupkg was found in $packageDir. Run dotnet pack first."
    }
    $PackagePath = $packages[0].FullName
}

$resolvedPackage = Resolve-Path -LiteralPath $PackagePath
$packageName = Split-Path -Leaf $resolvedPackage.Path
if ($packageName -notmatch '^ChronoStack\.(?<version>.+)\.nupkg$') {
    throw "Could not determine ChronoStack package version from '$packageName'."
}

$version = $Matches.version
$sourceDir = Split-Path -Parent $resolvedPackage.Path
$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("chronostack-consumer-" + [Guid]::NewGuid().ToString('N'))
$projectDir = Join-Path $smokeRoot 'Consumer'

New-Item -ItemType Directory -Path $projectDir | Out-Null

try {
    Invoke-Checked -FilePath 'dotnet' -Arguments @('new', 'console', '--framework', 'net8.0', '--force', '--no-restore') -WorkingDirectory $projectDir

    $escapedSource = [System.Security.SecurityElement]::Escape($sourceDir)
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-chronostack" value="$escapedSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@
    Set-Content -LiteralPath (Join-Path $projectDir 'NuGet.config') -Value $nugetConfig -Encoding UTF8

    Invoke-Checked -FilePath 'dotnet' -Arguments @('add', 'package', 'ChronoStack', '--version', $version) -WorkingDirectory $projectDir

    $program = @'
using ChronoStack;

var path = Path.Combine(AppContext.BaseDirectory, "chronostack-smoke.jsonl");
if (File.Exists(path)) File.Delete(path);

using (var tracer = Tracer.Create(new ITraceSink[] { new JsonlTraceSink(path) }))
{
    var result = tracer.RunTimed(() =>
    {
        tracer.AddTag("Smoke", "PackageInstall");
        tracer.InvokeScope("Consumer.Execute", () => throw new InvalidOperationException("package smoke"));
    });

    if (result.Success) throw new Exception("Expected smoke exception to be captured.");
}

var jsonl = File.ReadAllText(path);
if (!jsonl.Contains("\"severity\":\"Error\"")) throw new Exception("Missing severity in JSONL output.");
if (!jsonl.Contains("PackageInstall")) throw new Exception("Missing tag in JSONL output.");

Console.WriteLine(path);
'@
    Set-Content -LiteralPath (Join-Path $projectDir 'Program.cs') -Value $program -Encoding UTF8

    Invoke-Checked -FilePath 'dotnet' -Arguments @('restore') -WorkingDirectory $projectDir
    Invoke-Checked -FilePath 'dotnet' -Arguments @('run', '--no-restore') -WorkingDirectory $projectDir
    Write-Host "Verified consumer install for ChronoStack $version from $($resolvedPackage.Path)"
}
finally {
    if ($KeepArtifacts) {
        Write-Host "Kept smoke artifacts at $smokeRoot"
    }
    else {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
