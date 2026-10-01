#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Root = 'G:\AI\gpu-arbiter',
    [string]$DeployAccount = 'h3x1e\gpu-inference'
)
$ErrorActionPreference = 'Stop'
$serviceName = 'gpu-arbiter'
# The service runs as its virtual account. Windows manages that account and it has no password.
$serviceAccount = "NT SERVICE\$serviceName"
$exe = Join-Path $Root 'app\GpuArbiter.exe'
if (-not (Test-Path $exe)) { throw "Publish the Windows x64 app to $exe first." }
$config = (Get-Content (Join-Path $Root 'app\appsettings.json') -Raw | ConvertFrom-Json).Arbiter
if (-not (Test-Path $config.Llama.Exe) -or -not (Test-Path $config.Llama.Preset)) {
    throw 'Install llama.cpp and prepare the measured model presets first.'
}

function Invoke-Native([string]$What, [scriptblock]$Command) {
    $output = & $Command 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE): $output" }
}

$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
} else {
    New-Service -Name $serviceName -DisplayName 'GPU Inference Arbiter' -BinaryPathName ('"' + $exe + '"') -StartupType Automatic | Out-Null
}
# A virtual account takes no password argument. sc.exe passes none when password= is omitted.
Invoke-Native 'Setting the service account' { sc.exe config $serviceName obj= $serviceAccount start= delayed-auto }
Invoke-Native 'Enabling the service SID' { sc.exe sidtype $serviceName unrestricted }
Invoke-Native 'Configuring service recovery' { sc.exe failure $serviceName reset= 86400 actions= restart/1000/restart/5000/restart/30000 }

# The service reads the app, runtime and models, and writes only its logs. The deploy account
# replaces the app over SSH, so it keeps modify rights on the root. app-new inherits from the root.
New-Item -ItemType Directory -Force $config.LogDir | Out-Null
$grants = @(
    @($Root, "${serviceAccount}:(OI)(CI)RX"),
    @($config.LogDir, "${serviceAccount}:(OI)(CI)M"),
    @((Split-Path $config.Llama.Exe), "${serviceAccount}:(OI)(CI)RX"),
    @((Split-Path $config.Llama.Preset), "${serviceAccount}:(OI)(CI)RX"),
    @($Root, "${DeployAccount}:(OI)(CI)M")
)
foreach ($grant in $grants) {
    Invoke-Native "Granting $($grant[1]) on $($grant[0])" { icacls.exe $grant[0] /grant $grant[1] }
}

# The deploy account may query, start and stop the service, so deploy.ps1 needs no elevation.
$deploySid = ([System.Security.Principal.NTAccount]$DeployAccount).Translate([System.Security.Principal.SecurityIdentifier]).Value
$sddl = (sc.exe sdshow $serviceName | Where-Object { $_ -match '^D:' }) -join ''
if (-not $sddl) { throw 'Cannot read service permissions.' }
$ace = "(A;;CCLCSWRPWP;;;$deploySid)"
if (-not $sddl.Contains($ace)) {
    $systemAcl = $sddl.IndexOf('S:')
    if ($systemAcl -ge 0) { $sddl = $sddl.Insert($systemAcl, $ace) } else { $sddl += $ace }
    Invoke-Native 'Granting service query/start/stop access' { sc.exe sdset $serviceName $sddl }
}

foreach ($port in @(8080, 8189)) {
    $rule = @{
        DisplayName = "GPU arbiter TCP $port"; Direction = 'Inbound'; Action = 'Allow'; Protocol = 'TCP'
        LocalPort = $port; RemoteAddress = @('LocalSubnet', '100.64.0.0/10'); Profile = 'Any'
    }
    if (Get-NetFirewallRule -Name "gpu-arbiter-$port" -ErrorAction SilentlyContinue) {
        Set-NetFirewallRule -Name "gpu-arbiter-$port" @rule
    } else {
        New-NetFirewallRule -Name "gpu-arbiter-$port" @rule | Out-Null
    }
}

Start-Service $serviceName
(Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
$deadline = (Get-Date).AddSeconds(90)
do {
    try {
        $gpu = Invoke-RestMethod 'http://127.0.0.1:8080/gpu' -TimeoutSec 5
        Write-Host "gpu-arbiter is running as $serviceAccount."
        $gpu | ConvertTo-Json -Depth 5
        return
    } catch { Start-Sleep -Seconds 2 }
} while ((Get-Date) -lt $deadline -and (Get-Service $serviceName).Status -eq 'Running')

Write-Host "gpu-arbiter did not answer on http://127.0.0.1:8080/gpu. Service status: $((Get-Service $serviceName).Status)"
$log = Get-ChildItem $config.LogDir -Filter 'gpu-arbiter-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) { Get-Content $log.FullName -Tail 40 }
throw 'gpu-arbiter did not become healthy.'
