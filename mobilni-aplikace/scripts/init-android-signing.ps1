[CmdletBinding()]
param([string]$Directory = (Join-Path $env:USERPROFILE '.wand-night-stand\android-signing'))
$ErrorActionPreference = 'Stop'
$keytool = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\keytool.exe' } else { 'keytool' }
$keystore = Join-Path $Directory 'release.p12'
$credentials = Join-Path $Directory 'password.clixml'
if ((Test-Path -LiteralPath $keystore) -or (Test-Path -LiteralPath $credentials)) {
    throw 'Signing material already exists. Reuse it; never overwrite an update signing key.'
}
New-Item -ItemType Directory -Force -Path $Directory | Out-Null
$owner = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $Directory /inheritance:r /grant:r "*$($owner):(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict signing directory permissions' }
$random = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($random)
$password = [Convert]::ToBase64String($random)
$env:WNS_NEW_SIGNING_PASSWORD = $password
try {
    & $keytool -genkeypair -keystore $keystore -storetype PKCS12 -storepass:env WNS_NEW_SIGNING_PASSWORD -keypass:env WNS_NEW_SIGNING_PASSWORD -alias wand-night-stand -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=Wand Night Stand Android Beta,O=Wand Night Stand,C=DE'
    if ($LASTEXITCODE -ne 0) { throw 'Keystore creation failed' }
    ConvertTo-SecureString $password -AsPlainText -Force | Export-Clixml -LiteralPath $credentials
} finally {
    Remove-Item Env:WNS_NEW_SIGNING_PASSWORD -ErrorAction SilentlyContinue
    $password = $null
    [Array]::Clear($random, 0, $random.Length)
}
Write-Output "Created signing key: $keystore"
Write-Output 'Password is encrypted for this Windows user. Back up the key and recoverable password before distributing.'
