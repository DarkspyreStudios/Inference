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
for project in ../Tensor/src/*/*/*.csproj ../Connect/src/{Common,Codex,Claude,Antigravity,OpenAiApi,SelfHost}/*/*.csproj; do
    dotnet msbuild "$project" -t:_GetPackageFiles -getItem:_PackageFiles -p:Configuration=Release -nologo |
        jq -e '(.Items._PackageFiles | length > 0) and
            ([.Items._PackageFiles[] | select(.Identity | test("\\.(dll|metal|metallib|dylib|so|a)$"; "i"))] | length == 0)' >/dev/null
done
# A provider selected only through discovery retains its assembly attribute in trimmed builds.
for provider in Codex Claude Antigravity OpenAiApi SelfHost; do
    project="src/$provider/Darkspyre.Inference.Connect.$provider/Darkspyre.Inference.Connect.$provider.csproj"
    dotnet msbuild "$project" -t:_GetPackageFiles -getItem:_PackageFiles -nologo |
        jq -e --arg target "Darkspyre.Inference.Connect.$provider.targets" \
            '[.Items._PackageFiles[] | select(.Filename + .Extension == $target and .PackagePath == "buildTransitive/")] | length == 1' >/dev/null
done
dotnet msbuild tests/LLM/Darkspyre.Inference.Connect.LLM.Tests/Darkspyre.Inference.Connect.LLM.Tests.csproj \
    -t:RootConnectDiscoveryProviders -getItem:TrimmerRootAssembly -nologo |
    jq -e '[.Items.TrimmerRootAssembly[].Identity] | sort ==
        ["Darkspyre.Inference.Connect.Antigravity", "Darkspyre.Inference.Connect.Claude", "Darkspyre.Inference.Connect.Codex", "Darkspyre.Inference.Connect.OpenAiApi", "Darkspyre.Inference.Connect.SelfHost"]' >/dev/null
printf 'Package asset checks passed: independently packed libraries; no repacked native assets; output collisions rejected.\n'
