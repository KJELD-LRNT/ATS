<#
.SYNOPSIS
    Sets, generates, unlocks, disables or shows the Asset Tracker super admin account (the one account outside AD).

.DESCRIPTION
    Run on the application server, elevated, in a normal Windows PowerShell 5.1 (or PowerShell 7) console. Not in the
    ISE, not in a remote session, not with redirected input: the password is typed here and never leaves this session.

    How it works (docs/plans/system-roles-and-super-admin.md, setup command):
      1. set-password asks for the new password twice (no echo); generate-password makes a random 24-character password.
         Both refuse passwords shorter than 16 characters and the published demo password.
      2. "AssetTracker.Worker.exe superadmin hash" turns the password into a hash, in this session (no database).
         Only the hash goes further.
      3. The action and the hash are written to a request file in a folder that only Administrators and the worker's
         gMSA can use. The file's owner is set to you: the worker audits the change with that account.
      4. A one-off scheduled task runs "AssetTracker.Worker.exe superadmin apply <file>" under the gMSA (the database
         only accepts the gMSA). It applies the action, deletes the request and writes a result file.
      5. This script shows the result, then removes the task and the result file.

    The gMSA needs "Log on as a batch job" on this server to run the task (Local Security Policy, or the GPO that
    sets user rights). See docs/runbook.md, "Super admin".

.PARAMETER Action
    set-password | generate-password | unlock | disable | status

.PARAMETER WorkerPath
    Folder with AssetTracker.Worker.exe and appsettings.Production.json. Default: the folder of the AssetTrackerWorker
    service.

.PARAMETER ServiceAccount
    The worker's gMSA, e.g. 'DOMAIN\gmsa-ats$'. Default: the account the AssetTrackerWorker service runs as.

.PARAMETER RequestFolder
    Folder for request and result files. Default: 'SuperAdmin-Requests' next to the Data Protection key folder
    (DataProtection:KeyPath in the worker's appsettings.Production.json), else
    %ProgramData%\AssetTracker\SuperAdmin-Requests.

.EXAMPLE
    .\Set-SuperAdmin.ps1 -Action set-password

.EXAMPLE
    .\Set-SuperAdmin.ps1 -Action unlock -ServiceAccount 'DOMAIN\gmsa-ats$' -WorkerPath 'D:\Apps\AssetTracker\Worker'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('set-password', 'generate-password', 'unlock', 'disable', 'status')]
    [string] $Action,
    [string] $WorkerPath,
    [string] $ServiceAccount,
    [string] $RequestFolder,
    [string] $ServiceName = 'AssetTrackerWorker',
    [ValidateRange(10, 900)]
    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$MinimumLength = 16
$DemoPassword = 'demo-superadmin-2026'

# --- Where and how this runs ---------------------------------------------------------------------------------------
if ($Host.Name -eq 'Windows PowerShell ISE Host' -or (Get-Variable -Name psISE -ErrorAction SilentlyContinue)) {
    throw 'Run this script in a normal PowerShell console, not in the ISE.'
}
if ($Host.Name -eq 'ServerRemoteHost' -or (Get-Variable -Name PSSenderInfo -ErrorAction SilentlyContinue)) {
    throw 'Run this script on the server itself, not in a remote PowerShell session.'
}
if ([Console]::IsInputRedirected) {
    throw 'Input is redirected. Run this script interactively: the password must be typed, not piped.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal] $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated console (Run as administrator).'
}

$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
if (-not $WorkerPath) {
    if (-not $service) { throw "Service '$ServiceName' not found. Pass -WorkerPath (the folder with AssetTracker.Worker.exe)." }
    $WorkerPath = Split-Path -Parent ($service.PathName.Trim().Trim('"'))
}
$exe = Join-Path $WorkerPath 'AssetTracker.Worker.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "AssetTracker.Worker.exe not found in '$WorkerPath'." }

if (-not $ServiceAccount) {
    if (-not $service) { throw "Service '$ServiceName' not found. Pass -ServiceAccount (the worker's gMSA, e.g. 'DOMAIN\gmsa-ats`$')." }
    $ServiceAccount = $service.StartName
}
if ($ServiceAccount -notmatch '\$$') {
    throw "'$ServiceAccount' is not a gMSA (it must end with `$). Pass -ServiceAccount 'DOMAIN\gmsa-ats`$'."
}

if (-not $RequestFolder) {
    $RequestFolder = Join-Path $env:ProgramData 'AssetTracker\SuperAdmin-Requests'
    $settingsFile = Join-Path $WorkerPath 'appsettings.Production.json'
    if (Test-Path -LiteralPath $settingsFile) {
        $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        $keyPath = $null
        if ($settings.PSObject.Properties['DataProtection'] -and $settings.DataProtection.PSObject.Properties['KeyPath']) {
            $keyPath = $settings.DataProtection.KeyPath
        }
        if ($keyPath) { $RequestFolder = Join-Path (Split-Path -Parent $keyPath) 'SuperAdmin-Requests' }
    }
}

# --- The password stays in this session -----------------------------------------------------------------------------
function ConvertTo-PlainText([Security.SecureString] $Secure) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function New-RandomPassword([int] $Length = 24) {
    # Letters and digits without look-alikes (0/O, 1/l/I) plus a few symbols; rejection sampling keeps it uniform.
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_.!?#%+='.ToCharArray()
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $limit = 256 - (256 % $alphabet.Length)
    $chars = New-Object System.Collections.Generic.List[char]
    $buffer = New-Object byte[] 64
    try {
        while ($chars.Count -lt $Length) {
            $rng.GetBytes($buffer)
            foreach ($b in $buffer) {
                if ($b -lt $limit -and $chars.Count -lt $Length) { $chars.Add($alphabet[$b % $alphabet.Length]) }
            }
        }
    }
    finally { $rng.Dispose() }
    return -join $chars
}

function Get-PasswordHash([string] $Password) {
    # "superadmin hash" reads one line from standard input (UTF-8) and prints the hash. No database is used.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = 'superadmin hash'
    $psi.WorkingDirectory = $WorkerPath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($Password + "`n")
    try {
        $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $process.StandardInput.BaseStream.Flush()
        $process.StandardInput.Close()
    }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "The password was refused: $($stderr.Trim())" }
    return $stdout.Trim()
}

$password = $null
$hash = $null
switch ($Action) {
    'set-password' {
        $first = Read-Host -AsSecureString -Prompt "New super admin password (at least $MinimumLength characters)"
        $second = Read-Host -AsSecureString -Prompt 'Type it again'
        $password = ConvertTo-PlainText $first
        $again = ConvertTo-PlainText $second
        $first.Dispose(); $second.Dispose()
        if ($password -cne $again) { throw 'The passwords do not match. Nothing was changed.' }
        $again = $null
        if ($password.Length -lt $MinimumLength) { throw "The password must be at least $MinimumLength characters long. Nothing was changed." }
        if ($password.Trim() -eq $DemoPassword) { throw 'This is the published demo password. Choose another one. Nothing was changed.' }
        $hash = Get-PasswordHash $password
        $password = $null
    }
    'generate-password' {
        $password = New-RandomPassword 24
        $hash = Get-PasswordHash $password
    }
}

# --- Request folder: Administrators and the gMSA only ---------------------------------------------------------------
if (-not (Test-Path -LiteralPath $RequestFolder)) { New-Item -ItemType Directory -Path $RequestFolder | Out-Null }
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)   # no inherited entries
$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$none = [System.Security.AccessControl.PropagationFlags]::None
$allow = [System.Security.AccessControl.AccessControlType]::Allow
$administrators = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($administrators, 'FullControl', $inherit, $none, $allow)))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($ServiceAccount, 'Modify', $inherit, $none, $allow)))
Set-Acl -LiteralPath $RequestFolder -AclObject $acl

$id = [Guid]::NewGuid().ToString('N')
$requestFile = Join-Path $RequestFolder "superadmin-$id.json"
$resultFile = Join-Path $RequestFolder "superadmin-$id.result.json"
$taskName = "AssetTracker super admin $($id.Substring(0, 8))"
$request = @{ action = $Action }
if ($hash) { $request.hash = $hash }
[IO.File]::WriteAllText($requestFile, ($request | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
$hash = $null
# The worker audits the change with the file's owner. An elevated administrator's files are often owned by the
# Administrators group, so make the owner this account.
$fileAcl = Get-Acl -LiteralPath $requestFile
$fileAcl.SetOwner($identity.User)
Set-Acl -LiteralPath $requestFile -AclObject $fileAcl

# --- One-off task under the gMSA ------------------------------------------------------------------------------------
try {
    $taskAction = New-ScheduledTaskAction -Execute $exe -Argument "superadmin apply `"$requestFile`"" -WorkingDirectory $WorkerPath
    # A gMSA has no password to store: LogonType Password tells Task Scheduler to fetch the managed password from AD.
    $principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount -LogonType Password -RunLevel Limited
    $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $principal -Settings $taskSettings | Out-Null
    Start-ScheduledTask -TaskName $taskName

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $resultFile)) {
        if ((Get-Date) -gt $deadline) {
            $info = Get-ScheduledTaskInfo -TaskName $taskName
            throw ("No result after $TimeoutSeconds seconds (task result: 0x{0:X}). Check that the gMSA can log on as a batch job " +
                "and can read the worker folder, and the Application event log.") -f $info.LastTaskResult
        }
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Milliseconds 300
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json

    Write-Host ''
    if ($result.ok) { Write-Host $result.message -ForegroundColor Green } else { Write-Host $result.message -ForegroundColor Red }
    if ($result.PSObject.Properties['requester']) { Write-Host "Requested by: $($result.requester)" }
    if ($result.PSObject.Properties['account'] -and $result.account) {
        $a = $result.account
        Write-Host ("Account: {0}, {1}" -f $(if ($a.enabled) { 'on' } else { 'off' }), $(if ($a.locked) { "locked until $($a.lockedUntil)" } else { 'not locked' }))
        if ($a.PSObject.Properties['passwordChangedAt']) { Write-Host "Password set: $($a.passwordChangedAt)" }
        if ($a.PSObject.Properties['lastSignInAt']) { Write-Host "Last sign-in: $($a.lastSignInAt) from $($a.lastSignInIp)" }
        Write-Host "Failed attempts since the last sign-in: $($a.failedCount)"
    }
    if ($result.ok -and $Action -eq 'generate-password') {
        Write-Host ''
        Write-Host 'The new super admin password (shown once; store it in the password safe now):' -ForegroundColor Yellow
        Write-Host "    $password"
        Write-Host ''
    }
    if (-not $result.ok) { exit 1 }
}
finally {
    $password = $null
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $requestFile, $resultFile -Force -ErrorAction SilentlyContinue
}
