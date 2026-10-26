<#
.SYNOPSIS
    Produces the release artifacts: web publish folder, worker publish folder and EF migrations bundle.

.DESCRIPTION
    Framework-dependent, win-x64 (the server needs the .NET 10 Hosting Bundle, which includes the runtime).
    Works with PowerShell 7 on Windows (CI: windows-latest). Output:

        artifacts/web/        -> IIS site folder (includes deploy/web.config)
        artifacts/worker/     -> worker service folder (AssetTracker.Worker.exe)
        artifacts/efbundle/   -> efbundle.exe (see deploy/Apply-Migrations.ps1)
        artifacts/deploy/     -> scripts, config templates and the outbox quarantine SQL

    appsettings.Production.json is NOT produced: it is created per environment from the templates in deploy/.

.EXAMPLE
    pwsh ./build/publish.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Output = (Join-Path $PSScriptRoot '..' 'artifacts'),
    # Becomes the assembly informational version shown on the Ops page (the SDK appends +<commit>).
    [string] $Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

function Invoke-Dotnet([string[]] $Arguments) {
    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output | Out-Null

$common = @('-c', $Configuration, '-r', $Runtime, '--self-contained', 'false', "-p:Version=$Version")

Invoke-Dotnet (@('publish', (Join-Path $root 'src/AssetTracker.Web/AssetTracker.Web.csproj'), '-o', (Join-Path $Output 'web')) + $common)
Copy-Item (Join-Path $root 'deploy/web.config') (Join-Path $Output 'web/web.config') -Force

Invoke-Dotnet (@('publish', (Join-Path $root 'src/AssetTracker.Worker/AssetTracker.Worker.csproj'), '-o', (Join-Path $Output 'worker')) + $common)

# Development settings never ship.
Get-ChildItem $Output -Recurse -Filter 'appsettings.Development.json' | Remove-Item -Force

Push-Location $root
try {
    Invoke-Dotnet @('tool', 'restore')
    New-Item -ItemType Directory -Path (Join-Path $Output 'efbundle') | Out-Null
    Invoke-Dotnet @('ef', 'migrations', 'bundle',
        '-p', 'src/AssetTracker.Infrastructure', '-s', 'src/AssetTracker.Infrastructure',
        '-r', $Runtime, '--configuration', $Configuration,
        '-o', (Join-Path $Output 'efbundle/efbundle.exe'), '--force')
}
finally { Pop-Location }

$deployOut = Join-Path $Output 'deploy'
New-Item -ItemType Directory -Path $deployOut | Out-Null
Copy-Item (Join-Path $root 'deploy/*') $deployOut -Recurse

Write-Host "Artifacts written to $Output"
