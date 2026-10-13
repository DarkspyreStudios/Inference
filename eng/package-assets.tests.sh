#!/usr/bin/env bash
# Checks evaluated pack/copy inputs without creating a NuGet archive or publishing anything.
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root/Connect"
for fixture in Single NotCopied SeparatePaths; do
    dotnet msbuild ../eng/VerifyConsumer.Tests/OutputPathsFixture.proj -t:VerifyConsumerOutputPaths -p:Fixture="$fixture" -nologo
done
for fixture in Duplicate MixedDuplicate; do
    if result="$(dotnet msbuild ../eng/VerifyConsumer.Tests/OutputPathsFixture.proj -t:VerifyConsumerOutputPaths -p:Fixture="$fixture" -nologo 2>&1)"; then
        printf 'Expected duplicate-output failure for %s\n' "$fixture" >&2; exit 1
    fi
    [[ "$result" == *INFERENCEPKG001* && "$result" == *ggml-metal.metal* ]]
done
project=../Tensor/src/STT/Darkspyre.Inference.Tensor.STT.Whisper/Darkspyre.Inference.Tensor.STT.Whisper.csproj
dotnet msbuild "$project" -t:DoNotRepackWhisperRuntime -getItem:None -nologo |
    jq -e '[.Items.None[] | select(.DefiningProjectName == "Whisper.net.Runtime")] |
        length > 0 and all(.[]; .Pack == "false")' >/dev/null
# _GetPackageFiles collects archive inputs without packing or publishing.
for project in ../Tensor/src/*/*/*.csproj; do
    dotnet msbuild "$project" -t:_GetPackageFiles -getItem:_PackageFiles -p:Configuration=Release -nologo |
        jq -e '(.Items._PackageFiles | length > 0) and
            ([.Items._PackageFiles[] | select(.Identity | test("\\.(dll|metal|metallib|dylib|so|a)$"; "i"))] | length == 0)' >/dev/null
done
printf 'Package asset checks passed: independently packed libraries; no repacked native assets; output collisions rejected.\n'
