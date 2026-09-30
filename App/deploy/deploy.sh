#!/usr/bin/env bash
set -euo pipefail
app_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo_root="$(cd "$app_root/.." && pwd)"
target="${1:-gpu-box}"
output="$repo_root/tmp/gpu-arbiter-publish"
if [[ "$target" == -* || "$target" == *[[:space:]]* ]]; then
    echo 'Supply one SSH host alias.' >&2
    exit 2
fi
cd "$app_root"
dotnet publish src/GpuArbiter/GpuArbiter.csproj -c Release -r win-x64 --self-contained false -o "$output"
ssh "$target" 'powershell -NoProfile -Command "if (Test-Path G:\AI\gpu-arbiter\app-new) { throw '\''app-new already exists; inspect the previous deployment first'\'' }; New-Item -ItemType Directory G:\AI\gpu-arbiter\app-new | Out-Null"'
scp -r "$output/." "$target:G:/AI/gpu-arbiter/app-new/"
scp "$app_root/deploy/deploy.ps1" "$target:G:/AI/gpu-arbiter/deploy.ps1"
ssh "$target" 'powershell -NoProfile -ExecutionPolicy Bypass -File G:\AI\gpu-arbiter\deploy.ps1'
