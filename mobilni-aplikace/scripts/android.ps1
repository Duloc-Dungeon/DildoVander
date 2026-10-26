[CmdletBinding()]
param(
    [ValidateSet('debug', 'run', 'release', 'verify')][string]$Command = 'debug',
    [int]$VersionCode = 0,
    [string]$VersionName,
    [string]$Apk,
    [string]$SigningDirectory = (Join-Path $env:USERPROFILE '.wand-night-stand\android-signing')
)
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'android.py'
$previousJavaHome = $env:JAVA_HOME
$localJdk = Join-Path $env:USERPROFILE '.wand-night-stand\tools\jdk-21'
if (Test-Path -LiteralPath (Join-Path $localJdk 'bin\java.exe')) { $env:JAVA_HOME = $localJdk }
$restore = @{}
foreach ($name in @('ANDROID_KEYSTORE_PATH', 'ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_ALIAS', 'ANDROID_KEY_PASSWORD')) {
    $restore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    if ($Command -eq 'release' -and -not $env:ANDROID_KEYSTORE_PATH) {
        $env:ANDROID_KEYSTORE_PATH = Join-Path $SigningDirectory 'release.p12'
        $securePassword = Import-Clixml -LiteralPath (Join-Path $SigningDirectory 'password.clixml')
        $env:ANDROID_KEYSTORE_PASSWORD = [System.Net.NetworkCredential]::new('', $securePassword).Password
        $env:ANDROID_KEY_ALIAS = 'wand-night-stand'
    }
    $arguments = @($script, $Command)
    if ($VersionCode -gt 0) { $arguments += @('--version-code', "$VersionCode") }
    if ($VersionName) { $arguments += @('--version-name', $VersionName) }
    if ($Apk) { $arguments += @('--apk', $Apk) }
    & python @arguments
    if ($LASTEXITCODE -ne 0) { throw "Android $Command failed (exit $LASTEXITCODE)" }
} finally {
    $env:JAVA_HOME = $previousJavaHome
    foreach ($name in $restore.Keys) { [Environment]::SetEnvironmentVariable($name, $restore[$name], 'Process') }
}
