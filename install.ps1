<#
.SYNOPSIS
    Installs (or updates / removes) MuMu Device Frames for the current user.

.DESCRIPTION
    One line, no admin rights:

        irm https://raw.githubusercontent.com/JustSuperHuman/MuMuDeviceFrames/main/install.ps1 | iex

    Downloads the latest release, installs it to %LOCALAPPDATA%\Programs\MuMuDeviceFrames, fetches the frame set,
    adds a Start menu shortcut, turns on Start with Windows and starts the tray app. Running it again updates in place.

    With options:

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/JustSuperHuman/MuMuDeviceFrames/main/install.ps1))) -NoStartup
        .\install.ps1 -Zip .\MuMuDeviceFrames-win-x64.zip     # install a local build
        .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Version = 'latest',   # release tag, e.g. v1.1.0
    [string]$Zip,                  # install from a local zip (or publish folder) instead of GitHub
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MuMuDeviceFrames'),
    [switch]$NoStartup,
    [switch]$NoLaunch,
    [switch]$InstallAndroidTools,  # optional interactive WinGet setup for safe areas / iOS status bar
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo = 'JustSuperHuman/MuMuDeviceFrames'
$ExeName = 'MuMuDeviceFrames.exe'
$Exe = Join-Path $InstallDir $ExeName
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue = 'MuMuDeviceFrames'   # same value the app's own "Start with Windows" option uses
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MuMu Device Frames.lnk'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# Quit the running tray app cleanly (it un-clips the emulator windows on exit), then make sure it is gone.
function Stop-Running {
    $running = Get-Process -Name 'MuMuDeviceFrames' -ErrorAction SilentlyContinue
    if (-not $running) { return }
    Step 'Closing the running MuMu Device Frames'
    $exe = ($running | Select-Object -First 1).Path
    if ($exe) { Start-Process -FilePath $exe -ArgumentList '--exit' -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Name 'MuMuDeviceFrames' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Get-Process -Name 'MuMuDeviceFrames' -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Uninstall) {
    Stop-Running
    if (Test-Path $Exe) { Start-Process -FilePath $Exe -ArgumentList '--restore' -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue }
    Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
    Remove-Item $Shortcut -Force -ErrorAction SilentlyContinue
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "MuMu Device Frames removed. Settings and frames are kept in $env:APPDATA\MuMuDeviceFrames." -ForegroundColor Green
    return
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("mumu-device-frames-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if ($Zip) {
        $source = (Resolve-Path $Zip).Path
        Step "Installing from $source"
    } else {
        $api = if ($Version -eq 'latest') { "https://api.github.com/repos/$Repo/releases/latest" } else { "https://api.github.com/repos/$Repo/releases/tags/$Version" }
        $release = Invoke-RestMethod $api -Headers @{ 'User-Agent' = 'MuMuDeviceFrames-installer' }
        $asset = $release.assets | Where-Object name -eq 'MuMuDeviceFrames-win-x64.zip' | Select-Object -First 1
        if (-not $asset) { throw "Release $($release.tag_name) has no MuMuDeviceFrames-win-x64.zip" }
        Step "Downloading MuMu Device Frames $($release.tag_name)"
        $source = Join-Path $work 'MuMuDeviceFrames-win-x64.zip'
        Invoke-WebRequest $asset.browser_download_url -OutFile $source -UseBasicParsing
    }

    $staged = $source
    if (-not (Test-Path $source -PathType Container)) {
        $staged = Join-Path $work 'files'
        Expand-Archive -Path $source -DestinationPath $staged -Force
    }
    if (-not (Test-Path (Join-Path $staged $ExeName))) { throw "$ExeName not found in $source" }

    Stop-Running
    Step "Installing to $InstallDir"
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $staged '*') -Destination $InstallDir -Recurse -Force
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Step 'Downloading device frames (first run only, ~35 MB)'
$download = Start-Process -FilePath $Exe -ArgumentList '--download' -Wait -PassThru -WindowStyle Hidden
if ($download.ExitCode -ne 0) { Write-Warning "Some frames failed to download; the app will offer to retry. See $env:APPDATA\MuMuDeviceFrames\log.txt" }

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($Shortcut)
$link.TargetPath = $Exe
$link.WorkingDirectory = $InstallDir
$link.Description = 'Real device frames around MuMu Player windows'
$link.Save()

if ($NoStartup) {
    Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
} else {
    Step 'Turning on Start with Windows'
    Set-ItemProperty -Path $RunKey -Name $RunValue -Value "`"$Exe`""
}

if ($InstallAndroidTools) {
    Step 'Setting up optional Android display tools (package and license prompts may appear)'
    $setup = Join-Path $InstallDir 'tools\install-android-tools.ps1'
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Android tools setup script is missing from this release: $setup" }
    $setupArguments = @{}
    $configPath = Join-Path $env:APPDATA 'MuMuDeviceFrames\config.json'
    if (Test-Path -LiteralPath $configPath) {
        try {
            $savedConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            if ($savedConfig.AndroidSdk) { $setupArguments.SdkRoot = [string]$savedConfig.AndroidSdk }
        } catch { Write-Warning 'Could not read the saved SDK path. Android tools setup will use the default location.' }
    }
    & $setup @setupArguments
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Android display tools setup did not finish. Retry from the tray menu.' }
}

if (-not $NoLaunch) {
    Step 'Starting MuMu Device Frames (look for it in the tray)'
    Start-Process -FilePath $Exe -WorkingDirectory $InstallDir
}

Write-Host "MuMu Device Frames installed: $Exe" -ForegroundColor Green
