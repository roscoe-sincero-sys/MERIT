<#
.SYNOPSIS
    Starts a local mail capture tool for MERIT development: Mailpit or smtp4dev. Captured mail never leaves this machine.

.DESCRIPTION
    Both tools listen on 127.0.0.1 only, keep messages in memory, and have relaying turned off, so a message sent to a
    real address is captured, not delivered. Pick one with -Tool; they use different ports and can run side by side.

        Mailpit    SMTP 127.0.0.1:1025   web UI and API http://127.0.0.1:8025   (Chaos fault injection with -Chaos)
        smtp4dev   SMTP 127.0.0.1:2525   web UI and API http://127.0.0.1:8080

    Then run MERIT with the matching launch profile, "Merit.Web (Mailpit)" or "Merit.Web (smtp4dev)", which set
    Email__Provider=Smtp and the port; nothing in appsettings changes. Stop the tool with Stop-MailTool.ps1.

    Mailpit comes from Install-Mailpit.ps1 (pinned and checksum-verified). smtp4dev is the repository-local dotnet tool
    in sourceCode\.config\dotnet-tools.json (restored here with dotnet tool restore).

.PARAMETER Chaos
    Mailpit only: start with --enable-chaos, so tests and people can make the SMTP server fail on demand through the
    web UI or PUT /api/v1/chaos. Triggers start at probability 0 (no failures).

.EXAMPLE
    .\Start-MailTool.ps1 -Tool Mailpit -Chaos
    .\Start-MailTool.ps1 -Tool Smtp4dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Mailpit', 'Smtp4dev')] [string] $Tool,
    [Parameter()] [switch] $Chaos,
    [Parameter()] [int] $SmtpPort = 0,
    [Parameter()] [int] $WebPort = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path $env:LOCALAPPDATA 'MeritDevTools'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$pidFile = Join-Path $root "$($Tool.ToLowerInvariant()).pid"
$log = Join-Path $root "$($Tool.ToLowerInvariant()).log"
$sourceCode = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if ((Test-Path $pidFile) -and (Get-Process -Id ([int](Get-Content $pidFile -Raw)) -ErrorAction SilentlyContinue)) {
    Write-Host "$Tool is already running (process $(Get-Content $pidFile -Raw)). Stop it first with Stop-MailTool.ps1."
    return
}

function Test-PortFree([int] $port) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
    return $null -eq $listener
}

if ($Tool -eq 'Mailpit') {
    if ($SmtpPort -eq 0) { $SmtpPort = 1025 }
    if ($WebPort -eq 0) { $WebPort = 8025 }
    $exe = Join-Path $root 'mailpit\mailpit.exe'
    if (-not (Test-Path $exe)) {
        & (Join-Path $PSScriptRoot 'Install-Mailpit.ps1')
    }

    $arguments = @('--listen', "127.0.0.1:$WebPort", '--smtp', "127.0.0.1:$SmtpPort", '--disable-version-check', '--max', '500')
    if ($Chaos) { $arguments += '--enable-chaos' }
    $api = "http://127.0.0.1:$WebPort/api/v1/info"
    $file = $exe
}
else {
    if ($SmtpPort -eq 0) { $SmtpPort = 2525 }
    if ($WebPort -eq 0) { $WebPort = 8080 }
    if ($Chaos) { Write-Warning 'smtp4dev has no Chaos feature; -Chaos is ignored. Use Mailpit for fault injection.' }
    Push-Location $sourceCode
    try { dotnet tool restore | Out-Null } finally { Pop-Location }
    # --db= keeps messages in memory; --relaysmtpserver= turns relaying off; IMAP and POP3 are not needed.
    $arguments = @('tool', 'run', 'smtp4dev', '--', "--smtpport=$SmtpPort", "--urls=http://127.0.0.1:$WebPort", '--db=', '--imapport=',
        '--pop3port=', '--nousersettings', '--relaysmtpserver=', '--hostname=localhost', '--allowremoteconnections-', '--bindaddress=127.0.0.1')
    $api = "http://127.0.0.1:$WebPort/api/messages"
    $file = 'dotnet'
}

foreach ($port in @($SmtpPort, $WebPort)) {
    if (-not (Test-PortFree $port)) {
        throw "Port $port is already in use. Choose another with -SmtpPort / -WebPort, or stop whatever is using it."
    }
}

$process = Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $sourceCode -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err"
Set-Content -Path $pidFile -Value $process.Id -Encoding ascii

$deadline = (Get-Date).AddSeconds(60)
$ready = $false
while ((Get-Date) -lt $deadline -and -not $ready) {
    Start-Sleep -Milliseconds 500
    try {
        $response = Invoke-WebRequest -Uri $api -UseBasicParsing -TimeoutSec 2
        $ready = $response.StatusCode -eq 200
    }
    catch {
        if ($process.HasExited) { throw "$Tool exited during start-up. See $log and $log.err." }
    }
}

if (-not $ready) { throw "$Tool did not answer at $api within 60 seconds. See $log." }

Write-Host "$Tool is running (process $($process.Id)), bound to 127.0.0.1 only."
Write-Host "  SMTP     127.0.0.1:$SmtpPort (plain text; loopback only)"
Write-Host "  Web/API  http://127.0.0.1:$WebPort"
$profileName = 'Merit.Web (Mailpit)'
if ($Tool -eq 'Smtp4dev') { $profileName = 'Merit.Web (smtp4dev)' }
Write-Host "Run MERIT with the launch profile '$profileName', or set:"
Write-Host "  `$env:Email__Provider='Smtp'; `$env:Email__Smtp__Host='127.0.0.1'; `$env:Email__Smtp__Port='$SmtpPort'; `$env:Email__Smtp__Security='None'; `$env:Email__Smtp__FromAddress='merit-dev@localhost.test'"
