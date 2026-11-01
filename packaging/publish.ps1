<#
.SYNOPSIS
  Publishes self-contained single-file CherBot builds.
.EXAMPLE
  ./packaging/publish.ps1                       # win-x64 and linux-x64 engine + Windows desktop app
  ./packaging/publish.ps1 -Runtimes linux-x64   # one runtime only
#>
param(
    [string[]]$Runtimes = @('win-x64', 'linux-x64'),
    [string]$Configuration = 'Release',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts'),
    [switch]$NoDesktop,
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Output = [IO.Path]::GetFullPath($Output)

$common = @(
    '-c', $Configuration, '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    # Roslyn needs the plugin interface assembly on disk; self-extract keeps Assembly.Location valid.
    '-p:IncludeAllContentForSelfExtract=true',
    '-p:DebugType=none'
)

foreach ($rid in $Runtimes) {
    $out = Join-Path $Output "cherbot-$rid"
    Write-Host "==> engine $rid -> $out"
    dotnet publish (Join-Path $root 'src/CherBot.Host/CherBot.Host.csproj') -r $rid -o $out @common
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    Copy-Item (Join-Path $root 'packaging/README-install.md') $out -Force
    Copy-Item (Join-Path $root 'packaging/cherbot.example.json') $out -Force
    New-Item -ItemType Directory -Force (Join-Path $out 'docs') | Out-Null
    Copy-Item (Join-Path $root 'docs/*') (Join-Path $out 'docs') -Recurse -Force
    if ($rid -like 'linux-*') {
        foreach ($name in @('install.sh', 'cherbot.service', 'cherbot.user.service')) {
            Copy-Item (Join-Path $root "packaging/linux/$name") $out -Force
        }
    } else {
        Copy-Item (Join-Path $root 'packaging/windows/*') $out -Force
    }
    if ($Zip) {
        $zipPath = Join-Path $Output "cherbot-$rid.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath
    }
}

if (-not $NoDesktop -and ($Runtimes -contains 'win-x64')) {
    $out = Join-Path $Output 'cherbot-desktop-win-x64'
    Write-Host "==> desktop win-x64 -> $out"
    dotnet publish (Join-Path $root 'src/CherBot.Desktop/CherBot.Desktop.csproj') -r win-x64 -o $out @common
    if ($LASTEXITCODE -ne 0) { throw 'desktop publish failed' }
    Copy-Item (Join-Path $root 'packaging/README-install.md') $out -Force
    Copy-Item (Join-Path $root 'packaging/cherbot.example.json') $out -Force
    New-Item -ItemType Directory -Force (Join-Path $out 'docs') | Out-Null
    Copy-Item (Join-Path $root 'docs/*') (Join-Path $out 'docs') -Recurse -Force
    if ($Zip) {
        $zipPath = Join-Path $Output 'cherbot-desktop-win-x64.zip'
        if (Test-Path $zipPath) { Remove-Item $zipPath }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath
    }
}
Write-Host "done: $Output"
