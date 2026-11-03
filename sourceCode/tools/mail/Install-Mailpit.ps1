<#
.SYNOPSIS
    Downloads the pinned Mailpit release for Windows and verifies it before use. For local development only.

.DESCRIPTION
    Fetches mailpit-windows-amd64.zip for the pinned version from the project's GitHub releases, checks its SHA-256
    against the digest GitHub publishes for that release asset (recorded below), and extracts mailpit.exe to
    %LOCALAPPDATA%\MeritDevTools\mailpit. A file whose digest does not match is deleted and nothing is installed.
    Re-runnable: an installed, matching version is left alone.

    Docker users can run Mailpit from docker-compose.mail.yml instead.

.EXAMPLE
    .\Install-Mailpit.ps1
#>
[CmdletBinding()]
param(
    [Parameter()] [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'MeritDevTools')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Pinned: change both together, from the release page's asset digest (gh api repos/axllent/mailpit/releases/tags/<tag>).
$Version = 'v1.33.0'
$Sha256 = '47122dd828ff80dcaef62b2952c478b178afadadf066c990489608e537121bfd'
$Asset = 'mailpit-windows-amd64.zip'
$Url = "https://github.com/axllent/mailpit/releases/download/$Version/$Asset"

$target = Join-Path $InstallRoot 'mailpit'
$exe = Join-Path $target 'mailpit.exe'
$stamp = Join-Path $target 'version.txt'
if ((Test-Path $exe) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq "$Version $Sha256")) {
    Write-Host "Mailpit $Version is already installed at $exe"
    return
}

New-Item -ItemType Directory -Force -Path (Join-Path $target 'dl') | Out-Null
$zip = Join-Path $target "dl\$Asset"
Write-Host "Downloading Mailpit $Version ..."
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

$actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
if ($actual -ne $Sha256) {
    Remove-Item -Force $zip
    throw "Checksum mismatch for $Asset (expected $Sha256, got $actual). Nothing was installed."
}

Expand-Archive -Path $zip -DestinationPath $target -Force
Set-Content -Path $stamp -Value "$Version $Sha256" -Encoding ascii
Write-Host "Mailpit $Version installed and verified: $exe"
