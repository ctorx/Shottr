<#
.SYNOPSIS
    Builds Shottr as a portable exe and copies it to a permanent folder.

.DESCRIPTION
    Publishes a single-file Shottr.exe into .\publish, then copies it to the destination
    folder. If a running Shottr would lock the file, the script stops it first. If Shottr is
    already set to start with Windows, the script points that startup entry at the new copy.

.PARAMETER Destination
    Folder to copy Shottr.exe into. Defaults to %LOCALAPPDATA%\Programs\Shottr.

.PARAMETER FrameworkDependent
    Builds a small exe that needs the .NET 10 Desktop Runtime installed, instead of a
    larger self-contained exe that runs anywhere.

.PARAMETER StartMenuShortcut
    Adds a Shottr shortcut to the Start menu.

.PARAMETER NoInstall
    Only builds into .\publish. Nothing is copied, stopped or registered.

.PARAMETER Launch
    Starts the installed copy when the script finishes.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -StartMenuShortcut -Launch
    .\publish.ps1 -Destination D:\Tools\Shottr -FrameworkDependent
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Shottr'),
    [switch]$FrameworkDependent,
    [switch]$StartMenuShortcut,
    [switch]$NoInstall,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\Shottr\Shottr.csproj'
$publishDir = Join-Path $root 'publish'

# ---- Build -------------------------------------------------------------------------------

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
Write-Host "Publishing Shottr (self-contained: $selfContained)..." -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', $selfContained,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-o', $publishDir,
    '-nologo'
)
# Compressing the bundled runtime roughly halves a self-contained exe, for a slightly slower first start.
if (-not $FrameworkDependent) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$builtExe = Join-Path $publishDir 'Shottr.exe'
if (-not (Test-Path $builtExe)) { throw "Publish finished but $builtExe was not found." }
$sizeMb = [math]::Round((Get-Item $builtExe).Length / 1MB, 1)
Write-Host "Built $builtExe ($sizeMb MB)." -ForegroundColor Green

if ($NoInstall) { return }

# ---- Install -----------------------------------------------------------------------------

$targetExe = Join-Path $Destination 'Shottr.exe'

# A running Shottr locks its exe, and only one instance may run, so stop it first.
$running = Get-Process -Name Shottr -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping the running copy of Shottr...' -ForegroundColor Yellow
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item $builtExe $targetExe -Force
Write-Host "Installed to $targetExe." -ForegroundColor Green

# If start-with-Windows is already on, point it at the new copy.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$current = (Get-ItemProperty -Path $runKey -Name 'Shottr' -ErrorAction SilentlyContinue).Shottr
if ($current) {
    $wanted = "`"$targetExe`""
    if ($current -ne $wanted) {
        Set-ItemProperty -Path $runKey -Name 'Shottr' -Value $wanted
        Write-Host "Updated the startup entry to $targetExe." -ForegroundColor Green
    }
}

if ($StartMenuShortcut) {
    $programs = [Environment]::GetFolderPath('Programs')
    $lnk = Join-Path $programs 'Shottr.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $Destination
    $shortcut.IconLocation = "$targetExe,0"
    $shortcut.Description = 'Shottr screenshot tool'
    $shortcut.Save()
    Write-Host "Added Start menu shortcut $lnk." -ForegroundColor Green
}

if ($Launch) {
    Start-Process $targetExe
    Write-Host 'Started Shottr.' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
if (-not $current) {
    Write-Host '  - To start Shottr with Windows, open Settings from the tray icon and tick'
    Write-Host '    "Start Shottr when I sign in to Windows".'
}
Write-Host '  - If Controlled folder access is on, allow this exe in Windows Security >'
Write-Host '    Ransomware protection > "Allow an app through Controlled folder access".'
