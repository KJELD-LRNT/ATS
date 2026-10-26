<#
.SYNOPSIS
    Installs (or updates) the AssetTracker Worker as a Windows service running under a gMSA,
    with automatic restart on failure.

.DESCRIPTION
    Run elevated on the application server. The worker publish folder must already be copied to -InstallPath
    together with appsettings.Production.json (see deploy/README.md).

    The group Managed Service Account must be installed on this host first:
        Install-ADServiceAccount -Identity gmsa-ats      # requires the RSAT AD PowerShell module
        Test-ADServiceAccount   -Identity gmsa-ats      # must return True
    and needs "Log on as a service" (granted automatically by the SCM when the service is configured with it).

.EXAMPLE
    .\Install-Worker.ps1 -InstallPath 'D:\Apps\AssetTracker\Worker' -Account 'DOMAIN\gmsa-ats$'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $InstallPath,
    # Placeholder: the client's gMSA (note the trailing $). Never a personal account.
    [string] $Account = 'DOMAIN\gmsa-ats$',
    [string] $ServiceName = 'AssetTrackerWorker',
    # Must match the name passed to AddWindowsService in src/AssetTracker.Worker/Program.cs (used in the event log).
    [string] $DisplayName = 'AssetTracker Worker',
    [string] $Description = 'Asset Tracker background jobs: AD people sync, mail outbox sender, daily overdue reminders.'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$exe = Join-Path $InstallPath 'AssetTracker.Worker.exe'
if (-not (Test-Path $exe)) { throw "Worker executable not found: $exe (publish with -r win-x64)." }
if (-not (Test-Path (Join-Path $InstallPath 'appsettings.Production.json'))) {
    Write-Warning "appsettings.Production.json not found in $InstallPath. The worker will start without production settings."
}

function Invoke-Sc([string[]] $Arguments) {
    & sc.exe @Arguments | Write-Verbose
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped' -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop service')) {
        Stop-Service -Name $ServiceName -Force
    }
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Reconfigure service')) {
        Invoke-Sc @('config', $ServiceName, 'binPath=', "`"$exe`"", 'start=', 'delayed-auto', 'DisplayName=', $DisplayName)
    }
}
elseif ($PSCmdlet.ShouldProcess($ServiceName, 'Create service')) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName $DisplayName `
        -Description $Description -StartupType Automatic | Out-Null
    Invoke-Sc @('config', $ServiceName, 'start=', 'delayed-auto')
}

if ($PSCmdlet.ShouldProcess($ServiceName, "Run as $Account")) {
    # A gMSA has no password: pass an empty one; Windows retrieves the managed password from AD.
    Invoke-Sc @('config', $ServiceName, 'obj=', $Account, 'password=', '')
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Set recovery options')) {
    # Restart after 1 min, 1 min, then 5 min; reset the failure counter after one day.
    Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/300000')
    # Also apply the recovery actions when the process exits with a non-zero code (not only on crashes).
    Invoke-Sc @('failureflag', $ServiceName, '1')
    Invoke-Sc @('description', $ServiceName, $Description)
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    Start-Service -Name $ServiceName
    Get-Service -Name $ServiceName | Format-Table Name, Status, StartType -AutoSize
}

Write-Host "Done. Verify on the Ops page (IT) or GET /health/ready that the worker heartbeat is current within 1-2 minutes."
