<#
.SYNOPSIS
    Starts (or stops) the Partner Bank - each deployable in its own console window.

.DESCRIPTION
    Builds the solution ONCE (the deployables share PartnerBank.Security - parallel builds would
    clash), then opens one window per deployable, running it without building again:

        Partner Bank - authorization server   https://partner-auth.ewp.home.arpa:47401 (public), :47411 (mutual TLS)
        Partner Bank - API                    https://partner-api.ewp.home.arpa:47403 (EWP's server, mutual TLS), :47404 (the window - localhost ONLY)
        Partner Bank - window                 https://partner-products.ewp.home.arpa:47402 (the customer's browser)

    Each starts after the one before it is listening (the API and the window use the authorization server;
    the window uses the API).

    By default every port listens on this machine only (localhost). With -Lan they listen on every
    interface, so the laptop can reach them - use it on the home PC (the firewall rules allow the four
    ports on Private networks only; see README.md). The API's internal port (47404) stays on localhost
    even with -Lan: only the window on the same machine may use it.

.EXAMPLE
    .\ps\Start-PartnerBank.ps1          # on the laptop: localhost only
    .\ps\Start-PartnerBank.ps1 -Lan     # on the home PC: reachable from the home network
    .\ps\Start-PartnerBank.ps1 -Stop    # stop all three (the processes listening on their ports)
#>
param(
    [switch] $Lan,
    [switch] $Stop
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path   # the repository root (this script is in ps\)
$host_ = if ($Lan) { '*' } else { 'localhost' }

$deployables = @(
    [pscustomobject]@{
        Name = 'Partner Bank - authorization server'; Project = 'src\PartnerBank.AuthorizationServer'; Ports = @(47401, 47411)
        Urls = @("--Kestrel:Endpoints:Public:Url=https://${host_}:47401", "--Kestrel:Endpoints:Mtls:Url=https://${host_}:47411")
    }
    [pscustomobject]@{
        Name = 'Partner Bank - API'; Project = 'src\PartnerBank.Api'; Ports = @(47403, 47404)
        # ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        # │ Only the external (mutual-TLS) port follows -Lan; the internal port is never given
        # │ an address here, so it keeps its localhost-only URL from appsettings.
        # └──────────────────────────────────────────────────────────────────────────────────────
        Urls = @("--Kestrel:Endpoints:External:Url=https://${host_}:47403")
    }
    [pscustomobject]@{
        Name = 'Partner Bank - window'; Project = 'src\PartnerBank.Web'; Ports = @(47402)
        Urls = @("--Kestrel:Endpoints:Window:Url=https://${host_}:47402")
    }
)

function Get-Listener([int] $port) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
}

if ($Stop) {
    foreach ($d in $deployables) {
        $listener = Get-Listener $d.Ports[0]
        if ($listener) {
            Stop-Process -Id $listener.OwningProcess -Force
            Write-Host "Stopped $($d.Name)." -ForegroundColor Yellow
        } else {
            Write-Host "$($d.Name) was not running." -ForegroundColor DarkGray
        }
    }
    return
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ Fail early with the fix: without the Partner Bank's certificate and the trust root (from
# │ EWP's ps\pki\New-EwpLocalPki.ps1, copied into %USERPROFILE%\.ewp-pki) nothing starts.
# └──────────────────────────────────────────────────────────────────────────────────────
$pki = Join-Path $env:USERPROFILE '.ewp-pki'
$missing = @('ewp-trust-root.cer', 'partner-bank-server.pfx', 'partner-bank-server.pfx.password') | Where-Object { -not (Test-Path (Join-Path $pki $_)) }
if ($missing) {
    Write-Host "Missing in ${pki}: $($missing -join ', ') - see README.md, 'Certificates'." -ForegroundColor Red
    exit 1
}

$running = $deployables | Where-Object { Get-Listener $_.Ports[0] }
if ($running.Count -eq $deployables.Count) {
    Write-Host 'All deployables are already running. Stop them first with: .\ps\Start-PartnerBank.ps1 -Stop' -ForegroundColor DarkGray
    return
}

Write-Host 'Building the solution once...' -ForegroundColor Cyan
& dotnet build (Join-Path $root 'PartnerBank.slnx') -nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host 'The build failed - nothing was started.' -ForegroundColor Red
    exit 1
}

foreach ($d in $deployables) {
    if (Get-Listener $d.Ports[0]) {
        Write-Host "$($d.Name) is already running." -ForegroundColor DarkGray
        continue
    }

    # Each depends on the one before it: let that one listen first.
    $index = [array]::IndexOf($deployables, $d)
    if ($index -gt 0) {
        $previous = $deployables[$index - 1]
        Write-Host "Waiting for $($previous.Name)..." -ForegroundColor DarkGray
        $deadline = (Get-Date).AddMinutes(2)
        while (-not (Get-Listener $previous.Ports[0]) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    }

    $arguments = "/k title $($d.Name) && dotnet run --project `"$(Join-Path $root $d.Project)`" --no-build -- $($d.Urls -join ' ')"
    Start-Process -FilePath 'cmd.exe' -WorkingDirectory (Join-Path $root $d.Project) -ArgumentList $arguments
    Write-Host "Started $($d.Name) in its own window (ports $($d.Ports -join ', ') on $host_)." -ForegroundColor Green
}

Write-Host "`nStop them with: .\ps\Start-PartnerBank.ps1 -Stop"
if ($Lan) {
    Write-Host 'First start on this machine: allow each one in the Windows Firewall prompt on PRIVATE networks only.' -ForegroundColor Yellow
}