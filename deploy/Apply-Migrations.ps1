<#
.SYNOPSIS
    Applies EF Core migrations to the Asset Tracker database using a migrations bundle (efbundle.exe).

.DESCRIPTION
    The bundle is produced by build/publish.ps1 (or the CI "publish" job), which runs:

        dotnet tool restore
        dotnet ef migrations bundle -p src/AssetTracker.Infrastructure -s src/AssetTracker.Infrastructure `
            -r win-x64 --configuration Release -o artifacts/efbundle/efbundle.exe --force

    Run this script as the MIGRATION account (separate from the app pool / worker identity), which needs
    db_ddladmin + db_datareader + db_datawriter on the database (see docs/runbook.md, "SQL logins").
    Integrated security is used by default, so no password is stored or typed.

    Migrations are forward-only. Always take a SQL-aware backup first; rollback = restore that backup and redeploy
    the previous release artifact.

.EXAMPLE
    .\Apply-Migrations.ps1 -Bundle .\efbundle\efbundle.exe -Server '<SQL-HOST>\SQLEXPRESS' -Database 'AssetTracker'

.EXAMPLE
    # Review the SQL first (from a build machine with the SDK):
    # dotnet ef migrations script --idempotent -p src/AssetTracker.Infrastructure -s src/AssetTracker.Infrastructure -o migrate.sql
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $Bundle,
    [Parameter(Mandatory)] [string] $Server,
    [string] $Database = 'AssetTracker',
    # Set when the SQL Server certificate is not trusted by this host (prefer fixing the certificate).
    [switch] $TrustServerCertificate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $Bundle)) { throw "Migrations bundle not found: $Bundle" }

$cs = "Server=$Server;Database=$Database;Integrated Security=True;Encrypt=True"
if ($TrustServerCertificate) { $cs += ';TrustServerCertificate=True' }

Write-Host "Applying migrations to database '$Database' on '$Server' as $([Security.Principal.WindowsIdentity]::GetCurrent().Name)."
Write-Host 'Confirm that the worker is stopped and a pre-release backup exists.'

if ($PSCmdlet.ShouldProcess("$Server/$Database", 'Apply pending migrations')) {
    & $Bundle --connection $cs --verbose
    if ($LASTEXITCODE -ne 0) { throw "Migration bundle failed with exit code $LASTEXITCODE. Restore the pre-release backup if the schema is partially applied." }
    Write-Host 'Migrations applied.'
}
