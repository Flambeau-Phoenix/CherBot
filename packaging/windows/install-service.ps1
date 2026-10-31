<#
.SYNOPSIS
  Installs (or removes) the CherBot engine as a Windows Service. Run from an elevated PowerShell in the published folder.
.EXAMPLE
  .\install-service.ps1                         # installs/updates the service from this folder, data in %ProgramData%\CherBot
  .\install-service.ps1 -DataDir D:\CherBotData # custom data folder
  .\install-service.ps1 -Uninstall
#>
param(
    [string]$DataDir,
    [string]$ServiceName = 'CherBot',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
if ($Uninstall) {
    Write-Host "Removed service $ServiceName. Data was left in place."
    return
}

$exe = Join-Path $PSScriptRoot 'cherbot.exe'
if (-not (Test-Path $exe)) { throw "cherbot.exe not found next to this script ($PSScriptRoot)." }

$binPath = "`"$exe`""
if ($DataDir) { $binPath += " --data-dir `"$DataDir`"" }
$data = if ($DataDir) { $DataDir } else { Join-Path $env:ProgramData 'CherBot' }
New-Item -ItemType Directory -Force -Path $data | Out-Null
if (-not (Test-Path (Join-Path $data 'cherbot.json'))) {
    Copy-Item (Join-Path $PSScriptRoot 'cherbot.example.json') (Join-Path $data 'cherbot.json')
}

New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName 'CherBot' `
    -Description 'Headless Streamer.bot-compatible bot engine (web UI on http://127.0.0.1:8091).' -StartupType Automatic | Out-Null
# Restart after 5 s on the first three failures; reset the failure count daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Start-Service -Name $ServiceName

Write-Host "CherBot service installed and started."
Write-Host "  Config: $(Join-Path $data 'cherbot.json')  (then: Restart-Service $ServiceName)"
Write-Host "  Logs:   $(Join-Path $data 'logs')"
Write-Host "  Token:  & '$exe' token$(if ($DataDir) { " --data-dir '$DataDir'" })"
Write-Host "  Web UI: http://127.0.0.1:8091/"
