#Requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$applicationRoot = Join-Path $repositoryRoot 'mobilni-aplikace'
$backendProject = Join-Path $repositoryRoot 'backend\backend.csproj'
$backendOrigin = 'http://127.0.0.1:5082'
$frontendOrigin = 'https://localhost:5173'
$dataRoot = Join-Path $env:LOCALAPPDATA 'DildoVander\local-try-hub'
$identityRoot = Join-Path $dataRoot 'identity'
$keepsakeRoot = Join-Path $dataRoot 'keepsakes'
$identityDatabase = Join-Path $identityRoot 'identity.sqlite'
$logDirectory = Join-Path $env:TEMP 'DildoVander\local-try-hub'
$runId = [Guid]::NewGuid().ToString('N')
$backendLog = Join-Path $logDirectory "$runId-backend.log"
$backendErrorLog = Join-Path $logDirectory "$runId-backend-error.log"
$bootstrapErrorLog = Join-Path $logDirectory "$runId-bootstrap-error.log"
$bootstrapOutputLog = Join-Path $logDirectory "$runId-bootstrap-output.log"
$migrationErrorLog = Join-Path $logDirectory "$runId-migration-error.log"
$migrationOutputLog = Join-Path $logDirectory "$runId-migration-output.log"
$frontendLog = Join-Path $logDirectory "$runId-frontend.log"
$frontendErrorLog = Join-Path $logDirectory "$runId-frontend-error.log"
$backendProcess = $null
$frontendProcess = $null
$isolatedPrefixes = @(
  'ASPNETCORE_', 'Backend__', 'ApplicationIdentity__', 'Keepsake__', 'CustomerCharacters__',
  'Wands__', 'Memory__', 'Conversations__', 'SealedCase__', 'Game__', 'Show__', 'Launch__',
  'Push__', 'DigitalTwin__', 'ConnectionStrings__', 'SQLCONNSTR_', 'CUSTOMCONNSTR_',
  'AZURE_', 'AWS_', 'GOOGLE_', 'DATABASE_URL', 'SENTRY_', 'APPSETTING_', 'WEBSITE_',
  'KUFR_', 'DV_LOCAL_', 'LocalTryHub__', 'VITE_', 'NPM_CONFIG_', 'OTEL_', 'APPLICATIONINSIGHTS_', 'HONEYCOMB_'
)

function Test-LocalEndpoint([string] $url) {
  try {
    $response = Invoke-WebRequest -Uri $url -TimeoutSec 3 -UseBasicParsing
    return $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
  }
  catch {
    return $false
  }
}

function Wait-ForLocalEndpoint([string] $url, [System.Diagnostics.Process] $process, [string] $serviceName) {
  $deadline = [DateTime]::UtcNow.AddMinutes(2)
  while ([DateTime]::UtcNow -lt $deadline) {
    $process.Refresh()
    if ($process.HasExited) {
      throw "$serviceName skoncil pri spousteni. Zkontroluj lokalni logy v $logDirectory"
    }
    if (Test-LocalEndpoint $url) {
      return
    }
    Start-Sleep -Milliseconds 500
  }
  throw "$serviceName neodpovida na $url. Zkontroluj lokalni logy v $logDirectory"
}

function Stop-StartedProcessTree([System.Diagnostics.Process] $process) {
  if ($null -ne $process) {
    $process.Refresh()
    if (-not $process.HasExited) {
      try {
        $process.Kill($true)
      }
      catch {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
      }
      $process.WaitForExit(10000)
    }
  }
}

function Get-TrustedLocalhostCertificateBase64() {
  $trustedThumbprints = @{}
  Get-ChildItem Cert:\CurrentUser\Root | ForEach-Object { $trustedThumbprints[$_.Thumbprint] = $true }
  $now = [DateTime]::Now
  $certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
      $_.Subject -eq 'CN=localhost' -and $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now `
        -and $_.Extensions.Oid.Value -contains '1.3.6.1.4.1.311.84.1.1' `
        -and $trustedThumbprints.ContainsKey($_.Thumbprint)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

  if ($null -eq $certificate) {
    throw 'Nenasel se duveryhodny .NET HTTPS vyvojovy certifikat pro localhost. Spust dotnet dev-certs https --trust a potom launcher opakuj.'
  }

  $bytes = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx)
  return [Convert]::ToBase64String($bytes)
}

function Test-LocalPortAvailable([string] $origin) {
  $uri = [Uri]$origin
  $listener = [System.Net.Sockets.TcpClient]::new()
  try {
    $listener.Connect($uri.Host, $uri.Port)
    throw "Port $($uri.Port) uz pouziva jina sluzba: $origin"
  }
  catch [System.Net.Sockets.SocketException] {
    # An unused loopback port is expected.
  }
  finally {
    $listener.Dispose()
  }
}

function Test-LocalPortInUse([string] $origin) {
  $uri = [Uri]$origin
  $listener = [System.Net.Sockets.TcpClient]::new()
  try {
    $listener.Connect($uri.Host, $uri.Port)
    return $true
  }
  catch [System.Net.Sockets.SocketException] {
    return $false
  }
  finally {
    $listener.Dispose()
  }
}

function New-LocalChildEnvironment([bool] $includeProviderKeys) {
  $childEnvironment = @{}
  $processEnvironment = [Environment]::GetEnvironmentVariables('Process')
  foreach ($entry in $processEnvironment.GetEnumerator()) {
    $childEnvironment[$entry.Key] = $entry.Value
  }
  foreach ($name in @($childEnvironment.Keys)) {
    if ($isolatedPrefixes | Where-Object { $name.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) {
      $childEnvironment.Remove($name)
    }
  }
  foreach ($name in @('DOTNET_ENVIRONMENT', 'DOTNET_STARTUP_HOOKS', 'DOTNET_ADDITIONAL_DEPS',
      'DOTNET_SHARED_STORE', 'NODE_OPTIONS', 'NODE_PATH', 'DEBUG')) {
    $childEnvironment.Remove($name)
  }

  foreach ($name in @('OPENAI_API_KEY', 'ELEVENLABS_API_KEY')) {
    $processValue = [Environment]::GetEnvironmentVariable($name, 'Process')
    $userValue = [Environment]::GetEnvironmentVariable($name, 'User')
    $machineValue = [Environment]::GetEnvironmentVariable($name, 'Machine')
    $value = @($processValue, $userValue, $machineValue) |
      Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
      Select-Object -First 1
    $childEnvironment.Remove($name)
    if ($includeProviderKeys -and -not [string]::IsNullOrWhiteSpace($value)) {
      $childEnvironment[$name] = $value
    }
  }
  return $childEnvironment
}

function Initialize-LocalAccountSchema([hashtable] $childEnvironment) {
  $migrationProcess = Start-Process -FilePath (Get-Command dotnet).Source `
    -ArgumentList @('run', '--project', "`"$backendProject`"", '--no-launch-profile', '--', '--account-migrate') `
    -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru -Wait `
    -Environment $childEnvironment `
    -RedirectStandardOutput $migrationOutputLog -RedirectStandardError $migrationErrorLog
  if ($migrationProcess.ExitCode -ne 0) {
    throw "Místní databázi účtů se nepodařilo připravit. Zkontroluj lokální záznam $migrationErrorLog"
  }
}

try {
  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Chybí .NET SDK (příkaz dotnet).'
  }
  if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) {
    throw 'Chybí Node.js a npm. Nainstaluj verzi uvedenou v mobilni-aplikace/.nvmrc.'
  }
  if (-not (Test-Path (Join-Path $applicationRoot 'node_modules'))) {
    throw 'Chybí frontendové závislosti. Spusť npm ci ve složce mobilni-aplikace.'
  }
  if (-not $env:LOCALAPPDATA) {
    throw 'Windows profil nemá nastavenou složku LOCALAPPDATA.'
  }
  $backendEnvironment = New-LocalChildEnvironment $true
  $frontendEnvironment = New-LocalChildEnvironment $false
  if (-not $backendEnvironment.ContainsKey('OPENAI_API_KEY')) {
    throw 'Chybí OPENAI_API_KEY. Místní dílna používá skutečný OpenAI backend; nastav klíč jako Process, User nebo Machine proměnnou prostředí a spusť launcher znovu.'
  }
  if (-not $backendEnvironment.ContainsKey('ELEVENLABS_API_KEY')) {
    Write-Warning 'ELEVENLABS_API_KEY není v místním nastavení dostupný; hlasový vstup a výstup nebudou dostupné.'
  }

  foreach ($origin in @($backendOrigin, $frontendOrigin)) {
    Test-LocalPortAvailable $origin
  }

  New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
  New-Item -ItemType Directory -Path $identityRoot -Force | Out-Null
  New-Item -ItemType Directory -Path $keepsakeRoot -Force | Out-Null
  $certificateBase64 = Get-TrustedLocalhostCertificateBase64

  $backendEnvironment['ASPNETCORE_ENVIRONMENT'] = 'Development'
  $backendEnvironment['DOTNET_ENVIRONMENT'] = 'Development'
  $backendEnvironment['ASPNETCORE_URLS'] = $backendOrigin
  $backendEnvironment['Backend__DatabasePath'] = Join-Path $dataRoot 'backend.sqlite'
  $backendEnvironment['ApplicationIdentity__Enabled'] = 'true'
  $backendEnvironment['ApplicationIdentity__DataRoot'] = $identityRoot
  $backendEnvironment['ApplicationIdentity__DataProtectionRoot'] = Join-Path $dataRoot 'keys'
  $backendEnvironment['ApplicationIdentity__RelyingPartyId'] = 'localhost'
  $backendEnvironment['ApplicationIdentity__AllowedOrigins__0'] = $frontendOrigin
  $backendEnvironment['Keepsake__DataRoot'] = $keepsakeRoot
  $backendEnvironment['Keepsake__DataProtectionRoot'] = Join-Path $dataRoot 'keys'
  $backendEnvironment['Keepsake__Enabled'] = 'true'
  $backendEnvironment['Keepsake__GenerationEnabled'] = 'true'
  $backendEnvironment['Keepsake__ProviderMode'] = 'openai'
  $backendEnvironment['Keepsake__StoryModel'] = 'gpt-6.1-terra'
  $backendEnvironment['Keepsake__ImageModel'] = 'gpt-image-2'
  $backendEnvironment['Keepsake__ElevenLabsArtworkFallback'] = 'true'
  $backendEnvironment['Wands__Voice__Provider'] = 'elevenlabs'
  $backendEnvironment['Wands__Voice__VoiceId'] = 'U48DQ1c9SVmD2BVCSiHL'
  $backendEnvironment['Wands__Voice__Speed'] = '1.0'
  $backendEnvironment['Wands__Voice__Model'] = 'eleven_v4'
  $localSessionSecret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
  $backendEnvironment['LocalTryHub__Enabled'] = 'true'
  $backendEnvironment['LocalTryHub__SessionSecret'] = $localSessionSecret
  $backendEnvironment['LocalTryHub__Origin'] = $frontendOrigin
  $backendEnvironment['LocalTryHub__DataRoot'] = $dataRoot

  Initialize-LocalAccountSchema $backendEnvironment

  $frontendEnvironment['ASPNETCORE_ENVIRONMENT'] = 'Development'
  $frontendEnvironment['DOTNET_ENVIRONMENT'] = 'Development'
  $frontendEnvironment['DV_LOCAL_BACKEND_ORIGIN'] = $backendOrigin
  $frontendEnvironment['DV_LOCAL_HTTPS_PFX_BASE64'] = $certificateBase64
  $frontendEnvironment['DV_LOCAL_SESSION_SECRET'] = $localSessionSecret
  $npmCommand = (Get-Command npm.cmd).Source

  $backendProcess = Start-Process -FilePath (Get-Command dotnet).Source `
    -ArgumentList @('run', '--project', "`"$backendProject`"", '--no-launch-profile') `
    -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru `
    -Environment $backendEnvironment `
    -RedirectStandardOutput $backendLog -RedirectStandardError $backendErrorLog
  Wait-ForLocalEndpoint "$backendOrigin/healthz" $backendProcess 'Backend'

  $frontendProcess = Start-Process -FilePath $npmCommand `
    -ArgumentList 'run dev -- --host 127.0.0.1 --port 5173 --strictPort' `
    -WorkingDirectory $applicationRoot -WindowStyle Hidden -PassThru `
    -Environment $frontendEnvironment `
    -RedirectStandardOutput $frontendLog -RedirectStandardError $frontendErrorLog
  Wait-ForLocalEndpoint "$frontendOrigin/local-try-hub" $frontendProcess 'Frontend'

  Start-Process "$frontendOrigin/local-try-hub" | Out-Null

  Write-Host "Místní zkoušení: $frontendOrigin/local-try-hub"
  Write-Host 'Kufrova dílna používá zákaznický rozhovor. Persona Lab je samostatný test DV01.'
  Write-Host 'Obě služby naslouchají pouze na tomto počítači. Ctrl+C je ukončí.'
  Write-Host "Místní data: $dataRoot"
  Write-Host "Lokální logy: $logDirectory"
  while ($true) {
    $backendProcess.Refresh()
    $frontendProcess.Refresh()
    if ($backendProcess.HasExited -or $frontendProcess.HasExited) {
      throw "Jedna místní služba skončila. Logy jsou v $logDirectory"
    }
    Start-Sleep -Seconds 1
  }
}
finally {
  Stop-StartedProcessTree $frontendProcess
  Stop-StartedProcessTree $backendProcess
  foreach ($origin in @($backendOrigin, $frontendOrigin)) {
    if (Test-LocalPortInUse $origin) {
      $port = ([Uri]$origin).Port
      Write-Warning "Port $port is still in use after shutdown; inspect the local process before starting again."
    }
  }
}
