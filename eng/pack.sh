#!/usr/bin/env bash
# Packs every Inference library from an existing Release build and verifies the archives.
# Usage: eng/pack.sh <version> <source-commit>. eng/deploy.sh and .github/workflows/publish.yml both run it.
set -euo pipefail

version="${1:?version required}"
source_commit="${2:?source commit required}"
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
projects=(
    Common/src/Darkspyre.Inference.Common/Darkspyre.Inference.Common.csproj
    Connect/src/Darkspyre.Inference.Connect/Darkspyre.Inference.Connect.csproj
    Connect/src/Common/Darkspyre.Inference.Connect.Common/Darkspyre.Inference.Connect.Common.csproj
    Connect/src/Codex/Darkspyre.Inference.Connect.Codex/Darkspyre.Inference.Connect.Codex.csproj
    Connect/src/Claude/Darkspyre.Inference.Connect.Claude/Darkspyre.Inference.Connect.Claude.csproj
    Connect/src/HostTools/Darkspyre.Inference.Connect.HostTools/Darkspyre.Inference.Connect.HostTools.csproj
    Connect/src/Antigravity/Darkspyre.Inference.Connect.Antigravity/Darkspyre.Inference.Connect.Antigravity.csproj
    Connect/src/Grok/Darkspyre.Inference.Connect.Grok/Darkspyre.Inference.Connect.Grok.csproj
    Connect/src/OpenAiApi/Darkspyre.Inference.Connect.OpenAiApi/Darkspyre.Inference.Connect.OpenAiApi.csproj
    Connect/src/SelfHost/Darkspyre.Inference.Connect.SelfHost/Darkspyre.Inference.Connect.SelfHost.csproj
    Tensor/src/Common/Darkspyre.Inference.Tensor.Common/Darkspyre.Inference.Tensor.Common.csproj
    Tensor/src/Decision/Darkspyre.Inference.Tensor.Decision.Laya/Darkspyre.Inference.Tensor.Decision.Laya.csproj
    Tensor/src/Embedding/Darkspyre.Inference.Tensor.Embedding/Darkspyre.Inference.Tensor.Embedding.csproj
    Tensor/src/Embedding/Darkspyre.Inference.Tensor.Embedding.Gemma/Darkspyre.Inference.Tensor.Embedding.Gemma.csproj
    Tensor/src/Ggml/Darkspyre.Inference.Tensor.Ggml/Darkspyre.Inference.Tensor.Ggml.csproj
    Tensor/src/Image/Darkspyre.Inference.Tensor.Image.Supra/Darkspyre.Inference.Tensor.Image.Supra.csproj
    Tensor/src/LLM/Darkspyre.Inference.Tensor.LLM.Qwen/Darkspyre.Inference.Tensor.LLM.Qwen.csproj
    Tensor/src/Registry/Darkspyre.Inference.Tensor.Registry/Darkspyre.Inference.Tensor.Registry.csproj
    Tensor/src/STT/Darkspyre.Inference.Tensor.STT.Whisper/Darkspyre.Inference.Tensor.STT.Whisper.csproj
    UI/src/Settings/Darkspyre.Inference.UI.Settings.Core/Darkspyre.Inference.UI.Settings.Core.csproj
    UI/src/Settings/Darkspyre.Inference.UI.Settings.Avalonia/Darkspyre.Inference.UI.Settings.Avalonia.csproj
    UI/src/Settings/Darkspyre.Inference.UI.Settings.Blazor/Darkspyre.Inference.UI.Settings.Blazor.csproj
)

# Connect's global.json selects the pinned SDK for every pack.
cd "$root/Connect"
for project in "${projects[@]}"; do
    dotnet pack "../$project" -c Release --no-build --no-restore
done
dotnet restore ../eng/VerifyPackages
dotnet run --project ../eng/VerifyPackages --no-restore -- ../artifacts/packages "$version" "$source_commit"
