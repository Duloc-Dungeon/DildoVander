function New-SpeechEngineTunnelEnvironment([hashtable] $baseEnvironment) {
  $isolated = @{}
  foreach ($entry in $baseEnvironment.GetEnumerator()) {
    # Start-Process merges -Environment with the caller. Null tombstones remove
    # inherited values; omitting a key would silently inherit it again.
    $isolated[$entry.Key] = if ($entry.Key -match '^(?i:TUNNEL_|CLOUDFLARED_|CLOUDFLARE_|CF_)') { $null } else { $entry.Value }
  }
  return $isolated
}

function New-SpeechEngineTunnelArguments([string] $configPath) {
  # An explicit, newly created empty config excludes ~/.cloudflared/config.yml,
  # named tunnels, saved ingress rules and account credentials from this run.
  $absoluteConfig = [IO.Path]::GetFullPath($configPath)
  $config = [IO.FileStream]::new($absoluteConfig, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
  try {
    $content = [Text.Encoding]::UTF8.GetBytes("{}`n")
    $config.Write($content)
  }
  finally { $config.Dispose() }
  return @('tunnel', '--config', "`"$absoluteConfig`"", '--url', 'http://127.0.0.1:5083', '--no-autoupdate', '--protocol', 'http2')
}

function Get-LocalCloudflared([string] $toolRoot) {
  # Official pinned binary; verify both cached and downloaded bytes before use.
  $version = '2026.10.6'
  $expectedHash = 'D521B3BF68982165F0D06D76732F22D9A105EF057FF3AE4C0986F3A23190950F'
  New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
  $binary = Join-Path $toolRoot "cloudflared-$version.exe"
  if (-not (Test-Path -LiteralPath $binary)) {
    $download = "$binary.download"
    try {
      Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/download/$version/cloudflared-windows-amd64.exe" -OutFile $download
      if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'Nesouhlasí kontrolní součet oficiálního cloudflared. Tunel nebyl spuštěn.'
      }
      Move-Item -LiteralPath $download -Destination $binary
    }
    finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download } }
  }
  if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Místní cloudflared má jiný kontrolní součet. Tunel nebyl spuštěn.'
  }
  return $binary
}

function Wait-SpeechEngineTunnel([System.Diagnostics.Process] $process, [string] $path) {
  $deadline = [DateTime]::UtcNow.AddSeconds(40)
  while ([DateTime]::UtcNow -lt $deadline) {
    $process.Refresh()
    if ($process.HasExited) { throw 'Tunel pro Speech Engine se nespustil. Zkontroluj místní log tunelu.' }
    if (Test-Path -LiteralPath $path) {
      $logText = Get-Content -LiteralPath $path -Raw
      if (-not [string]::IsNullOrWhiteSpace($logText)) {
        $match = [regex]::Match($logText, 'https://[a-z0-9-]+\.trycloudflare\.com')
        if ($match.Success) { return $match.Value }
      }
    }
    Start-Sleep -Milliseconds 200
  }
  throw 'Tunel pro Speech Engine zatím nezískal veřejnou adresu.'
}
