[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$release = 'b11238'
$binaryRoot = "G:\AI\llama.cpp\$release"
$modelRoot = 'G:\AI\models'
New-Item -ItemType Directory -Force $binaryRoot, "$modelRoot\qwen3.8-27b", "$modelRoot\echidna-13b" | Out-Null
$metadata = Invoke-RestMethod "https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/$release"
foreach ($name in @("llama-$release-bin-win-cuda-13.4-x64.zip", 'cudart-llama-bin-win-cuda-13.4-x64.zip')) {
    $asset = $metadata.assets | Where-Object name -eq $name
    if (-not $asset -or $asset.digest -notmatch '^sha256:([a-f0-9]{64})$') { throw "Missing SHA-256 release digest for $name" }
    $expected = $Matches[1]
    $archive = Join-Path $binaryRoot $name
    if (-not (Test-Path $archive)) {
        & curl.exe --silent --show-error --fail --location --retry 3 --output $archive $asset.browser_download_url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $name" }
    }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) { throw "SHA-256 mismatch: $name" }
    Expand-Archive $archive $binaryRoot -Force
}
$file = 'Qwen3.8-27B-Q4_K_M.gguf'
$tree = Invoke-RestMethod 'https://huggingface.co/api/models/ggml-org/Qwen3.8-27B-GGUF/tree/main'
$model = $tree | Where-Object path -eq $file
if (-not $model.lfs.oid -or $model.lfs.oid -notmatch '^[a-f0-9]{64}$') { throw 'No model SHA-256 digest is available.' }
$destination = "$modelRoot\qwen3.8-27b\$file"
if (-not (Test-Path $destination)) {
    & curl.exe --silent --show-error --fail --location --retry 3 --output $destination "https://huggingface.co/ggml-org/Qwen3.8-27B-GGUF/resolve/main/$file"
    if ($LASTEXITCODE -ne 0) { throw 'Qwen model download failed.' }
}
if ((Get-FileHash $destination -Algorithm SHA256).Hash -ne $model.lfs.oid) { throw 'Qwen model SHA-256 mismatch.' }
$echidna = 'echidna-tiefigther-25.Q8_0.gguf'
if (-not (Test-Path "$modelRoot\echidna-13b\$echidna")) {
    Copy-Item "G:\Chat\Models\$echidna" "$modelRoot\echidna-13b\$echidna"
}
# The original model remains available until end-to-end verification authorizes retiring the old layout.
Write-Host 'CUDA binaries and models are ready. Copy presets.ini, measure context and idle VRAM, then install the service.'
