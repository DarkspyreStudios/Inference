#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Root = 'G:\AI\gpu-arbiter',
    [string]$Account = 'h3x1e\gpu-inference'
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Root 'app\GpuArbiter.exe'
if (-not (Test-Path $exe)) { throw "Publish the Windows x64 app to $exe first." }
$configPath = Join-Path $Root 'app\appsettings.json'
$config = (Get-Content $configPath -Raw | ConvertFrom-Json).Arbiter
if (-not (Test-Path $config.Llama.Exe) -or -not (Test-Path $config.Llama.Preset)) {
    throw 'Install llama.cpp and prepare the measured model presets first.'
}
$sid = ([System.Security.Principal.NTAccount]$Account).Translate([System.Security.Principal.SecurityIdentifier])

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class ServiceLogonRight {
    [StructLayout(LayoutKind.Sequential)] private struct Attributes {
        public uint Length; public IntPtr RootDirectory, ObjectName; public uint Flags;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString {
        public ushort Length, MaximumLength; public IntPtr Buffer;
    }
    [DllImport("advapi32.dll")] private static extern uint LsaOpenPolicy(IntPtr system, ref Attributes attributes, uint access, out IntPtr handle);
    [DllImport("advapi32.dll")] private static extern uint LsaAddAccountRights(IntPtr handle, byte[] sid, UnicodeString[] rights, uint count);
    [DllImport("advapi32.dll")] private static extern uint LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern uint LsaNtStatusToWinError(uint status);
    public static void Grant(byte[] sid) {
        var attributes = new Attributes { Length = (uint)Marshal.SizeOf<Attributes>() };
        IntPtr policy;
        Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x810, out policy));
        var name = "SeServiceLogonRight";
        var buffer = Marshal.StringToHGlobalUni(name);
        try {
            var right = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)((name.Length + 1) * 2), Buffer = buffer };
            Check(LsaAddAccountRights(policy, sid, new[] { right }, 1));
        } finally { Marshal.FreeHGlobal(buffer); LsaClose(policy); }
    }
    private static void Check(uint status) {
        if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status));
    }
}
'@
$bytes = New-Object byte[] $sid.BinaryLength
$sid.GetBinaryForm($bytes, 0)
[ServiceLogonRight]::Grant($bytes)
New-Item -ItemType Directory -Force $config.LogDir | Out-Null
# The service account owns later deployment swaps and writes logs. Model and runtime paths stay read-only.
& icacls.exe $Root /grant "${Account}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot grant app directory access.' }
foreach ($path in @((Split-Path $config.Llama.Exe), (Split-Path $config.Llama.Preset))) {
    & icacls.exe $path /grant "${Account}:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot grant runtime/model access: $path" }
}
$existing = Get-Service gpu-arbiter -ErrorAction SilentlyContinue
if ($existing) { throw 'gpu-arbiter is already registered. Use deploy.sh to update it.' }
$credential = Get-Credential -UserName $Account -Message 'Password for the GPU inference service account'
if (-not $credential -or $credential.UserName -ne $Account) { throw 'The requested service credential is required.' }
New-Service -Name gpu-arbiter -DisplayName 'GPU Inference Arbiter' -BinaryPathName ('"' + $exe + '"') -StartupType Automatic -Credential $credential | Out-Null
& sc.exe config gpu-arbiter start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot set delayed automatic start.' }
$sddl = (& sc.exe sdshow gpu-arbiter | Where-Object { $_ -match '^D:' }) -join ''
if ($LASTEXITCODE -ne 0 -or -not $sddl) { throw 'Cannot read service permissions.' }
$ace = "(A;;CCLCSWRPWP;;;$($sid.Value))"
$systemAcl = $sddl.IndexOf('S:')
if ($systemAcl -ge 0) { $sddl = $sddl.Insert($systemAcl, $ace) } else { $sddl += $ace }
& sc.exe sdset gpu-arbiter $sddl | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot grant service query/start/stop access.' }
& sc.exe failure gpu-arbiter reset= 86400 actions= restart/1000/restart/5000/restart/30000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot configure service recovery.' }
foreach ($port in @(8080, 8189)) {
    New-NetFirewallRule -Name "gpu-arbiter-$port" -DisplayName "GPU arbiter TCP $port" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -RemoteAddress @('LocalSubnet', '100.64.0.0/10') -Profile Any | Out-Null
}
Start-Service gpu-arbiter
(Get-Service gpu-arbiter).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host 'gpu-arbiter is registered and running.'
