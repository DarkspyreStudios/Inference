[CmdletBinding()]
param([string]$Root = 'G:\AI\gpu-arbiter')
$ErrorActionPreference = 'Stop'
$app = Join-Path $Root 'app'
$next = Join-Path $Root 'app-new'
$previous = Join-Path $Root 'app-previous'
if (-not (Test-Path (Join-Path $next 'GpuArbiter.exe'))) { throw 'app-new has no published application.' }
$service = Get-Service gpu-arbiter
Stop-Service $service
$service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
try {
    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
    # Preserve measured machine configuration when the published defaults are copied.
    if (Test-Path (Join-Path $app 'appsettings.json')) {
        Copy-Item (Join-Path $app 'appsettings.json') (Join-Path $next 'appsettings.json') -Force
    }
    if (Test-Path $app) { Move-Item $app $previous }
    Move-Item $next $app
    Start-Service $service
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    $deadline = (Get-Date).AddSeconds(60)
    do {
        try {
            $null = Invoke-RestMethod 'http://127.0.0.1:8080/gpu' -TimeoutSec 5
            Write-Host 'Deployment is healthy.'
            return
        } catch { Start-Sleep -Seconds 1 }
    } while ((Get-Date) -lt $deadline)
    throw 'Deployment did not become healthy.'
} catch {
    Stop-Service $service -ErrorAction SilentlyContinue
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    if (Test-Path $previous) {
        if (Test-Path $app) { Move-Item $app $next }
        Move-Item $previous $app
        Start-Service $service
    }
    throw
}
