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
project=../Tensor/src/Darkspyre.Inference.Tensor/Darkspyre.Inference.Tensor.csproj
dotnet msbuild "$project" -t:DoNotRepackWhisperRuntime -getItem:None -nologo |
    jq -e '[.Items.None[] | select(.Identity | endswith("/ggml-metal.metal"))] |
        length == 1 and all(.[]; .Pack == "false" and .CopyToOutputDirectory == "PreserveNewest" and .TargetPath == "ggml-metal.metal")' >/dev/null
# _GetPackageFiles only collects the future archive inputs; it does not execute Pack.
dotnet msbuild "$project" -t:_GetPackageFiles -getItem:_PackageFiles -p:Configuration=Release -nologo |
    jq -e '(.Items._PackageFiles | length > 0) and
        ([.Items._PackageFiles[] | select(.Identity | test("\\.(metal|metallib|dylib|so|a)$"; "i"))] | length == 0) and
        ([.Items._PackageFiles[] | select(.PackagePath == "lib/net11.0" and (.Identity |
            test("/Darkspyre\\.Inference\\.Tensor\\.Registry\\.(dll|xml)$")))] | length == 2)' >/dev/null
printf 'Package asset checks passed: Registry assembly and XML packed; no repacked native assets; dependency shader copying preserved; output collisions rejected.\n'
