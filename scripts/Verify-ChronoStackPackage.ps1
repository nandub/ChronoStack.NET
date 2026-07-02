<#
.SYNOPSIS
Verifies that the ChronoStack NuGet package contains the expected release artifacts.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'

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
$symbolPackage = [System.IO.Path]::ChangeExtension($resolvedPackage.Path, '.snupkg')

if (-not (Test-Path -LiteralPath $symbolPackage)) {
    throw "Expected symbol package was not found: $symbolPackage"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage.Path)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName })
}
finally {
    $zip.Dispose()
}

$requiredEntries = @(
    'README.md',
    'lib/net48/ChronoStack.dll',
    'lib/net48/ChronoStack.xml',
    'lib/net481/ChronoStack.dll',
    'lib/net481/ChronoStack.xml',
    'lib/net6.0/ChronoStack.dll',
    'lib/net6.0/ChronoStack.xml',
    'lib/net8.0/ChronoStack.dll',
    'lib/net8.0/ChronoStack.xml'
)

foreach ($entry in $requiredEntries) {
    if ($entries -notcontains $entry) {
        throw "Package is missing required entry: $entry"
    }
}

$nuspecEntry = $entries | Where-Object { $_ -like '*.nuspec' } | Select-Object -First 1
if (-not $nuspecEntry) {
    throw 'Package is missing a .nuspec file.'
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("chronostack-package-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($resolvedPackage.Path, $tempDir)
    $nuspecPath = Join-Path $tempDir $nuspecEntry
    [xml]$nuspec = Get-Content -LiteralPath $nuspecPath
    $metadata = $nuspec.package.metadata

    if ($metadata.license.'#text' -ne 'MIT' -or $metadata.license.type -ne 'expression') {
        throw "Expected MIT license expression, found '$($metadata.license.OuterXml)'."
    }
    if ($metadata.repository.type -ne 'git') {
        throw "Expected git repository metadata, found '$($metadata.repository.OuterXml)'."
    }
    if ($metadata.repository.url -ne 'https://github.com/nandub/ChronoStack.NET') {
        throw "Unexpected repository URL '$($metadata.repository.url)'."
    }
    if ($metadata.readme -ne 'README.md') {
        throw "Expected package readme README.md, found '$($metadata.readme)'."
    }
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Verified package: $($resolvedPackage.Path)"
Write-Host "Verified symbols: $symbolPackage"
