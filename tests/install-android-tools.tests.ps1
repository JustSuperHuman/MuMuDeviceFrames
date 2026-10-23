$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\install-android-tools.ps1')

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    Write-Host "PASS $message"
}

# Mock only external state/commands. Run the real setup orchestration without changing installed packages.
function Find-SetupJava { if ($script:hasJava) { return 'C:\Java 21' }; return $null }
function Test-SetupSdk([string]$Root) { return $script:hasSdk }
function Find-SetupCommand([string]$Name) {
    if ($Name -eq 'winget' -and $script:hasWinget) { return 'winget.exe' }
    if ($Name -eq 'android' -and $script:hasAndroid) { return 'android.exe' }
    return $null
}
function Invoke-SetupCommand([string]$File, [string[]]$Arguments) {
    $script:calls.Add("$File $($Arguments -join ' ')")
    if ($script:failCommand) { throw 'Simulated cancellation or download failure' }
    if ($Arguments -contains 'EclipseAdoptium.Temurin.21.JDK') { $script:hasJava = $true }
    if ($Arguments -contains 'Google.AndroidCLI') { $script:hasAndroid = $true }
    if ($File -eq 'android.exe' -and -not $script:incompleteInstall) { $script:hasSdk = $true }
}
function Reset-State {
    $script:calls = New-Object 'System.Collections.Generic.List[string]'
    $script:hasJava = $false; $script:hasSdk = $false; $script:hasAndroid = $false
    $script:hasWinget = $true; $script:failCommand = $false; $script:incompleteInstall = $false
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    Assert ($failure -and $failure -match $Pattern) "Failure handled: $Pattern"
}
$savedJava = $env:JAVA_HOME
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('deviceframes-setup-tests-' + [guid]::NewGuid().ToString('N'))
try {
    Reset-State
    $script:hasJava = $true; $script:hasSdk = $true; $script:hasWinget = $false
    Install-AndroidTools $temporary
    Assert ($script:calls.Count -eq 0) 'Ready tools need neither WinGet nor downloads'

    Reset-State
    Install-AndroidTools $temporary
    Assert ($script:calls.Count -eq 3) 'Fresh setup installs Java, CLI, and SDK'
    Assert ($script:calls[0] -match '--id EclipseAdoptium.Temurin.21.JDK --exact --source winget --interactive') 'Java uses exact WinGet package with visible installer'
    Assert ($script:calls[1] -match '--id Google.AndroidCLI --exact --source winget --scope user') 'Android CLI uses per-user WinGet install'
    Assert ($script:calls[2] -match 'build-tools/35.0.0 platforms/android-35') 'Only required SDK packages requested'
    Assert (-not ($script:calls -match 'accept-.*agreements|--silent')) 'License prompts remain interactive'

    Reset-State
    $script:hasJava = $true; $script:hasAndroid = $true
    Install-AndroidTools (Join-Path $temporary 'SDK with spaces & symbols')
    Assert ($script:calls.Count -eq 1) 'Partial setup reuses existing Java and CLI'

    Reset-State
    $script:hasSdk = $true
    Install-AndroidTools $temporary
    Assert ($script:calls.Count -eq 1) 'Java-only repair leaves SDK alone'

    Reset-State
    $script:hasWinget = $false
    Assert-Fails { Install-AndroidTools $temporary } 'App Installer'
    Assert ($script:calls.Count -eq 0) 'Missing WinGet does not start installation'

    Reset-State
    $script:failCommand = $true
    Assert-Fails { Install-AndroidTools $temporary } 'cancellation'
    Assert ($script:calls.Count -eq 1) 'Cancellation stops subsequent installs'

    Reset-State
    $script:hasJava = $true; $script:hasAndroid = $true; $script:incompleteInstall = $true
    Assert-Fails { Install-AndroidTools $temporary } 'still missing'
} finally {
    $env:JAVA_HOME = $savedJava
    if (Test-Path -LiteralPath $temporary) {
        # This literal GUID path was created by this test, under the system temp directory.
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
