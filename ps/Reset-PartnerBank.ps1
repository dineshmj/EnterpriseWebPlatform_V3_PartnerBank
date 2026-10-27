<#
.SYNOPSIS
    Resets the Partner Bank's data on this machine (the home PC, or the laptop for a local run).

.DESCRIPTION
    The Partner Bank keeps its data in two SQLite files, created again (empty, with their seed data)
    when the services next start:

      Data        src\PartnerBank.Api\partner-bank.db
                  Digital Gold accounts, orders, holdings, activity, applications, window links.
      Customers   src\PartnerBank.AuthorizationServer\partner-identity.db
                  the customers' sign-in accounts (the demo customers alex.partner and sam.partner
                  are seeded again at start-up; registered customers are gone).

    Level 1 erases the Data only; level 2 erases both. The authorization server's signing keys
    (src\PartnerBank.AuthorizationServer\keys) are kept: tokens issued before stay verifiable.

    It stops the three services first (Start-PartnerBank.ps1 -Stop) - SQLite files cannot be
    deleted while a service holds them - and asks before erasing anything (or pass -Force).

    After a reset, EWP may still hold records of accounts the Partner Bank no longer knows: send
    those again from EWP, or reset EWP as well (EWP's .\ps\Reset.ps1, level 1 or higher: its
    databases and its Temporal purchase workflows). Temporal runs on the EWP side only, so there is
    nothing of it to reset here.

.EXAMPLE
    .\ps\Reset-PartnerBank.ps1                # asks for the level, then for confirmation
.EXAMPLE
    .\ps\Reset-PartnerBank.ps1 -Level 1 -Force
#>
param(
    [ValidateSet(1, 2)]
    [int] $Level,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path   # the repository root (this script is in ps\)

$data = Join-Path $root 'src\PartnerBank.Api\partner-bank.db'
$customers = Join-Path $root 'src\PartnerBank.AuthorizationServer\partner-identity.db'

if (-not $Level) {
    Write-Host 'Reset the Partner Bank:' -ForegroundColor Cyan
    Write-Host '  1  Data only        Digital Gold accounts, orders, holdings, activity, applications'
    Write-Host '  2  Data + customers level 1, plus every customer sign-in account (demo customers come back)'
    $Level = [int](Read-Host 'Level (1 or 2)')
    if ($Level -notin 1, 2) { Write-Host 'Nothing done.' -ForegroundColor Yellow; return }
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ Erasing customer data is never silent: the script names what goes and asks first,
# │ unless -Force is given on purpose (e.g. by a test run).
# └──────────────────────────────────────────────────────────────────────────────────────
$files = @($data) + $(if ($Level -eq 2) { @($customers) } else { @() })
if (-not $Force) {
    Write-Host "`nThis ERASES:" -ForegroundColor Yellow
    $files | ForEach-Object { Write-Host "  $_ (and its -wal / -shm files)" }
    if ((Read-Host 'Type YES to continue') -ne 'YES') { Write-Host 'Nothing done.' -ForegroundColor Yellow; return }
}

# ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
# │ The services hold the SQLite files open: stop them first, then delete the database
# │ with its write-ahead log (-wal) and shared memory (-shm) files, so no half-old state
# │ survives. The services re-create the files, empty, at their next start.
# └──────────────────────────────────────────────────────────────────────────────────────
& (Join-Path $PSScriptRoot 'Start-PartnerBank.ps1') -Stop | Out-Null
Start-Sleep -Seconds 2

foreach ($file in $files) {
    foreach ($part in @($file, "$file-wal", "$file-shm")) {
        if (Test-Path $part) {
            Remove-Item $part -Force
            Write-Host "Deleted $part" -ForegroundColor Green
        }
    }
}

Write-Host "`nDone. Start the Partner Bank again: .\ps\Start-PartnerBank.ps1 -Lan" -ForegroundColor Cyan