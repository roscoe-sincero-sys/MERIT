<#
.SYNOPSIS
    Stops a mail capture tool started by Start-MailTool.ps1. Captured messages are discarded (they live in memory).

.EXAMPLE
    .\Stop-MailTool.ps1 -Tool All
#>
[CmdletBinding()]
param(
    [Parameter()] [ValidateSet('Mailpit', 'Smtp4dev', 'All')] [string] $Tool = 'All'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path $env:LOCALAPPDATA 'MeritDevTools'
$tools = @('Mailpit', 'Smtp4dev')
if ($Tool -ne 'All') { $tools = @($Tool) }

foreach ($name in $tools) {
    $pidFile = Join-Path $root "$($name.ToLowerInvariant()).pid"
    if (-not (Test-Path $pidFile)) {
        Write-Host "$name was not started by Start-MailTool.ps1."
        continue
    }

    $id = [int](Get-Content $pidFile -Raw)
    $process = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        # smtp4dev runs under dotnet: stop the whole tree so no child keeps the ports.
        & taskkill.exe /PID $id /T /F | Out-Null
        Write-Host "$name stopped (process $id)."
    }
    else {
        Write-Host "$name was not running."
    }

    Remove-Item -Force $pidFile
}
