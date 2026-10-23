<#
.SYNOPSIS
    Installs the optional Android display tools using WinGet and Google's Android CLI.
.DESCRIPTION
    Run interactively. Existing tools are reused. Package and SDK license prompts remain visible.
    No Android Studio, emulator image, global SDK environment changes, or automatic reboot is required.
    Java's installer may request Windows administrator approval. Closing the window cancels setup.
.EXAMPLE
    .\tools\install-android-tools.ps1
.EXAMPLE
    .\tools\install-android-tools.ps1 -SdkRoot 'D:\Android SDK'
#>
[CmdletBinding()]
param(
    [string]$SdkRoot = (Join-Path $env:LOCALAPPDATA 'Android\Sdk'),
    [string]$LogPath = (Join-Path $env:APPDATA 'MuMuDeviceFrames\android-tools-install.log')
)

function Find-SetupCommand([string]$Name) {
    # WinGet adds aliases to this directory; the running tray app may have an older PATH.
    $command = Get-Command "$Name.exe" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($path in @(
        (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\$Name.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$Name.exe")
    )) { if (Test-Path -LiteralPath $path -PathType Leaf) { return $path } }
    return $null
}

function Find-SetupJava {
    $homes = @($env:JAVA_HOME,
        [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User'),
        [Environment]::GetEnvironmentVariable('JAVA_HOME', 'Machine'),
        (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'))
    $javaCommand = Find-SetupCommand 'java'
    if ($javaCommand) { $homes += Split-Path (Split-Path $javaCommand -Parent) -Parent }
    foreach ($root in @(
        (Join-Path $env:ProgramFiles 'Eclipse Adoptium'),
        (Join-Path $env:ProgramFiles 'Microsoft'),
        (Join-Path $env:ProgramFiles 'Java'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Eclipse Adoptium'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Java'),
        (Join-Path $env:LOCALAPPDATA 'Programs\MuMuDeviceFramesJava')
    )) {
        if (Test-Path -LiteralPath $root -PathType Container) {
            $homes += Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name -Descending | ForEach-Object FullName
        }
    }
    foreach ($homePath in ($homes | Where-Object { $_ } | Select-Object -Unique)) {
        $java = Join-Path $homePath 'bin\java.exe'
        if (-not (Test-Path -LiteralPath $java -PathType Leaf)) { continue }
        # Windows PowerShell treats native stderr as an error with ErrorActionPreference=Stop.
        $versionText = & $java -version 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -and $versionText -match 'version\s+"(\d+)') {
            if ([int]$Matches[1] -ge 17) { return $homePath }
        }
    }
    return $null
}

function Test-SetupSdk([string]$Root) {
    $build = @(Get-ChildItem -LiteralPath (Join-Path $Root 'build-tools') -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            $dir = $_.FullName
            @('aapt2.exe', 'zipalign.exe', 'apksigner.bat', 'lib\apksigner.jar' |
                Where-Object { -not (Test-Path -LiteralPath (Join-Path $dir $_) -PathType Leaf) }).Count -eq 0
        })
    $platform = @(Get-ChildItem -LiteralPath (Join-Path $Root 'platforms') -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'android.jar') -PathType Leaf })
    return $build.Count -gt 0 -and $platform.Count -gt 0
}

function Invoke-SetupCommand([string]$File, [string[]]$Arguments) {
    Write-Host "Running: $File $($Arguments -join ' ')" -ForegroundColor Cyan
    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed (exit $LASTEXITCODE): $File. Retry setup after checking the output above."
    }
}

function Install-AndroidTools([string]$Root) {
    $Root = [IO.Path]::GetFullPath($Root)
    $javaHome = Find-SetupJava
    $sdkReady = Test-SetupSdk $Root
    if (-not $javaHome -or -not $sdkReady) {
        $winget = Find-SetupCommand 'winget'
        if (-not $winget) {
            throw 'WinGet is unavailable. Install or update App Installer from https://aka.ms/getwinget, then retry from the tray menu. Alternatively install Android SDK build-tools/platform 35 and Java 17+ manually.'
        }
        if (-not $javaHome) {
            Invoke-SetupCommand $winget @('install', '--id', 'EclipseAdoptium.Temurin.21.JDK', '--exact', '--source', 'winget', '--interactive')
            $javaHome = Find-SetupJava
            if (-not $javaHome) { throw 'Java 17+ was not found after installation. The install may have been cancelled or require a restart. Retry setup.' }
        }
        if (-not $sdkReady) {
            $android = Find-SetupCommand 'android'
            if (-not $android) {
                Invoke-SetupCommand $winget @('install', '--id', 'Google.AndroidCLI', '--exact', '--source', 'winget', '--scope', 'user')
                $android = Find-SetupCommand 'android'
            }
            if (-not $android) { throw 'Android CLI was not found after installation. Retry setup, or install Google.AndroidCLI with WinGet.' }
            # Scope Java to this setup process; do not replace the user's global JAVA_HOME or PATH.
            $env:JAVA_HOME = $javaHome
            New-Item -ItemType Directory -Path $Root -Force -ErrorAction Stop | Out-Null
            Invoke-SetupCommand $android @("--sdk=$Root", 'sdk', 'install', 'build-tools/35.0.0', 'platforms/android-35')
        }
    }
    if (-not (Test-SetupSdk $Root) -or -not (Find-SetupJava)) {
        throw 'Setup finished, but required files are still missing. See the setup log and retry from the tray menu.'
    }
    Write-Host "Android display tools ready in $Root" -ForegroundColor Green
}

# Dot-sourcing exposes functions for checks without launching an installer.
if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Continue'
    $exitCode = 1
    $transcribing = $false
    try {
        New-Item -ItemType Directory -Path (Split-Path $LogPath -Parent) -Force -ErrorAction Stop | Out-Null
        Start-Transcript -LiteralPath $LogPath -Append -ErrorAction Stop | Out-Null
        $transcribing = $true
        Write-Host 'MuMu Device Frames - Android display tools setup' -ForegroundColor Cyan
        Write-Host 'Approve package/license prompts below. Close this window to cancel. Java may request administrator approval.'
        Install-AndroidTools $SdkRoot
        $exitCode = 0
    } catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
        Write-Host "Setup log: $LogPath"
        Read-Host 'Press Enter to close; you can retry from the tray menu' | Out-Null
    } finally {
        if ($transcribing) { Stop-Transcript | Out-Null }
    }
    exit $exitCode
}
