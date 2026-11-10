[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Navigation__Enhanced = 'true'
# Read existing local credentials without copying them into another configuration file.
foreach ($app in @('Web', 'Worker')) {
    $project = Join-Path $repo "sourceCode/src/Merit.$app"
    $settings = Get-Content (Join-Path $project 'appsettings.Development.json') -Raw | ConvertFrom-Json
    $env:ConnectionStrings__Merit = $settings.ConnectionStrings.Merit.Replace('Database=MERIT;', 'Database=MERITRegion3Demo;')
    if ($env:ConnectionStrings__Merit -notmatch 'Database=MERITRegion3Demo;') { throw 'Expected development database connection string.' }
    $env:ASPNETCORE_URLS = if ($app -eq 'Web') { 'https://localhost:7243' } else { 'http://localhost:5280' }
    $assembly = Join-Path $project "bin/Debug/net9.0/Merit.$app.dll"
    if (-not (Test-Path -LiteralPath $assembly)) { throw "Build the solution first: $assembly is missing." }
    $process = Start-Process dotnet -ArgumentList @('"' + $assembly + '"') -WorkingDirectory $project -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $PSScriptRoot "$app.stdout.log") -RedirectStandardError (Join-Path $PSScriptRoot "$app.stderr.log")
    $process.Id | Set-Content (Join-Path $PSScriptRoot "$app.pid")
    Write-Host "$app started with PID $($process.Id) using MERITRegion3Demo."
}
Remove-Item Env:\ConnectionStrings__Merit
Write-Host 'Sign in at https://localhost:7243/account/signin'
