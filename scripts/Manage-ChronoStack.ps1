<#
.SYNOPSIS
A unified helper script to Clean, Build, Test, Run, Pack, and Publish the ChronoStack.NET solution.

.DESCRIPTION
Eliminates the need to memorize complex 'dotnet' CLI commands. 
Provides tab-completion for Projects, Configurations, and Frameworks.

.EXAMPLE
.\Manage-ChronoStack.ps1 -Action Build -Configuration Release
# Builds the entire solution in Release mode.

.EXAMPLE
.\Manage-ChronoStack.ps1 -Action Publish -Project Driver -Framework net8.0 -SingleFile
# Publishes the Driver project as a single, standalone .exe file for Windows.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('Clean', 'Build', 'Test', 'Run', 'Publish', 'Pack')]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [ValidateSet('All', 'Library', 'Demo', 'Tests', 'Driver')]
    [string]$Project = 'All',

    [Parameter(Mandatory = $false)]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [Parameter(Mandatory = $false)]
    [ValidateSet('net8.0', 'net6.0', 'net48', 'net481')]
    [string]$Framework,

    [Parameter(Mandatory = $false)]
    [ValidateSet('win-x64', 'linux-x64', 'win-x86')]
    [string]$Runtime = 'win-x64',

    [Parameter(Mandatory = $false)]
    [switch]$SingleFile,

    [Parameter(Mandatory = $false)]
    [switch]$SelfContained
)

# 1. Map the friendly project names to the actual paths
$targetPath = switch ($Project) {
    'All'     { "ChronoStack.sln" }
    'Library' { "src\ChronoStack\ChronoStack.csproj" }
    'Demo'    { "src\ChronoStack.Demo\ChronoStack.Demo.csproj" }
    'Tests'   { "src\ChronoStack.Tests\ChronoStack.Tests.csproj" }
    'Driver'  { "src\ChronoStack.Driver\ChronoStack.Driver.csproj" }
}

# 2. Build the base dotnet arguments
$dotnetArgs = @(
    $Action.ToLower()
)

# Run command requires --project instead of just passing the path
if ($Action -eq 'Run') {
    if ($Project -eq 'All' -or $Project -eq 'Library') {
        Write-Error "You can only 'Run' the Demo, Tests, or Driver project."
        exit 1
    }
    $dotnetArgs += "--project", $targetPath
} else {
    $dotnetArgs += $targetPath
}

# Add Configuration
$dotnetArgs += "-c", $Configuration

# Add Framework (if specified)
if (-not [string]::IsNullOrWhiteSpace($Framework)) {
    # MUST be strictly lowercase!
    $dotnetArgs += "--framework", $Framework
}

# 3. Add Publish-Specific Arguments
if ($Action -eq 'Publish') {
    if ($Project -eq 'All') {
        Write-Error "Please specify a specific project to publish (e.g., -Project Driver)."
        exit 1
    }
    
    # MUST be strictly lowercase!
    $dotnetArgs += "--runtime", $Runtime
    
    # Modern .NET uses --self-contained and --no-self-contained
    if ($SelfContained) {
        $dotnetArgs += "--self-contained"
    } else {
        $dotnetArgs += "--no-self-contained"
    }

    if ($SingleFile) {
        $dotnetArgs += "-p:PublishSingleFile=true"
    }
}

# 4. Print what we are about to do
$commandString = "dotnet " + ($dotnetArgs -join " ")
Write-Host ""
Write-Host "===========================================================" -ForegroundColor Cyan
Write-Host " Executing: " -NoNewline; Write-Host $commandString -ForegroundColor Yellow
Write-Host "===========================================================" -ForegroundColor Cyan
Write-Host ""

# 5. Execute the command!
& dotnet @dotnetArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[ERROR] Command failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
} else {
    Write-Host "`n[SUCCESS] Command completed successfully." -ForegroundColor Green
}

