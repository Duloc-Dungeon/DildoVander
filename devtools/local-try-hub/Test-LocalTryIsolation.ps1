#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SpeechEngineTunnel.ps1')

# Load the production environment builder without executing the launcher.
$syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Start-LocalTryHub.ps1'), [ref]$null, [ref]$null)
$definition = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-LocalChildEnvironment' }, $true)
$prefixes = $syntax.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$isolatedPrefixes' }, $true)
$isolatedPrefixes = @($prefixes.Right.FindAll({ param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object Value)
. ([scriptblock]::Create($definition.Extent.Text))

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('dv-hub-isolation-' + [Guid]::NewGuid().ToString('N'))
$originalName = [Environment]::GetEnvironmentVariable('TUNNEL_NAME', 'Process')
$originalBackend = [Environment]::GetEnvironmentVariable('Backend__DatabasePath', 'Process')
try {
  New-Item -ItemType Directory -Path (Join-Path $testRoot '.cloudflared') -Force | Out-Null
  $savedConfig = Join-Path $testRoot '.cloudflared/config.yml'
  [IO.File]::WriteAllText($savedConfig, 'tunnel: private-do-not-use')
  [Environment]::SetEnvironmentVariable('TUNNEL_NAME', 'private-do-not-use', 'Process')
  [Environment]::SetEnvironmentVariable('Backend__DatabasePath', 'private-do-not-use', 'Process')
  $environment = New-SpeechEngineTunnelEnvironment (New-LocalChildEnvironment $false)
  $environment['USERPROFILE'] = $testRoot
  $configPath = Join-Path $testRoot 'explicit-empty.yml'
  $arguments = New-SpeechEngineTunnelArguments $configPath
  if ($arguments[1] -ne '--config' -or $arguments[2].Trim('"') -ne $configPath -or
    $arguments[3] -ne '--url' -or $arguments[4] -ne 'http://127.0.0.1:5083') { throw 'Wrong tunnel launch contract' }
  if ((Get-Content -LiteralPath $configPath -Raw).Trim() -ne '{}') { throw 'Inherited named-tunnel config' }
  $probePath = Join-Path $testRoot 'probe.ps1'
  [IO.File]::WriteAllText($probePath, @'
$names = 'OPENAI_API_KEY','ELEVENLABS_API_KEY','TUNNEL_NAME','Backend__DatabasePath'
foreach ($name in $names) { if (![string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name,'Process'))) { exit 7 } }
Write-Output 'isolated'
'@)
  $probe = Start-Process -FilePath (Get-Command pwsh).Source -WindowStyle Hidden -PassThru -Wait `
    -ArgumentList @('-NoProfile', '-File', "`"$probePath`"") -Environment $environment `
    -RedirectStandardOutput (Join-Path $testRoot 'output.txt') -RedirectStandardError (Join-Path $testRoot 'error.txt')
  if ($probe.ExitCode -ne 0 -or (Get-Content (Join-Path $testRoot 'output.txt') -Raw).Trim() -ne 'isolated') { throw 'Child inherited excluded environment' }
  if ((Get-Content -LiteralPath $savedConfig -Raw) -ne 'tunnel: private-do-not-use') { throw 'Saved user configuration changed' }
  $emptyLog = Join-Path $testRoot 'startup.log'
  [IO.File]::WriteAllText($emptyLog, '')
  $writerPath = Join-Path $testRoot 'writer.ps1'
  [IO.File]::WriteAllText($writerPath, @'
param([string] $LogPath)
Start-Sleep -Milliseconds 500
[IO.File]::WriteAllText($LogPath, 'https://synthetic-test.trycloudflare.com')
Start-Sleep -Seconds 3
'@)
  $writer = Start-Process -FilePath (Get-Command pwsh).Source -WindowStyle Hidden -PassThru `
    -ArgumentList @('-NoProfile', '-File', "`"$writerPath`"", '-LogPath', "`"$emptyLog`"") -Environment $environment
  try {
    if ((Wait-SpeechEngineTunnel $writer $emptyLog) -ne 'https://synthetic-test.trycloudflare.com') { throw 'Tunnel startup URL missing' }
  }
  finally { $writer.Refresh(); if (-not $writer.HasExited) { $writer.Kill(); $null = $writer.WaitForExit(5000) } }
  Write-Output 'PASS actual child environment excludes provider/backend/named-tunnel configuration; explicit empty config preserves saved settings'
}
finally {
  [Environment]::SetEnvironmentVariable('TUNNEL_NAME', $originalName, 'Process')
  [Environment]::SetEnvironmentVariable('Backend__DatabasePath', $originalBackend, 'Process')
  $resolved = [IO.Path]::GetFullPath($testRoot)
  if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and
    [IO.Path]::GetFileName($resolved).StartsWith('dv-hub-isolation-') -and (Test-Path -LiteralPath $resolved)) {
    Remove-Item -LiteralPath $resolved -Recurse -Force
  }
}
