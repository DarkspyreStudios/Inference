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
for project in ../Tensor/src/*/*/*.csproj ../Connect/src/{Common,Codex,Claude,Antigravity,Grok,OpenAiApi,SelfHost}/*/*.csproj; do
    dotnet msbuild "$project" -t:_GetPackageFiles -getItem:_PackageFiles -p:Configuration=Release -nologo |
        jq -e '(.Items._PackageFiles | length > 0) and
            ([.Items._PackageFiles[] | select(.Identity | test("\\.(dll|metal|metallib|dylib|so|a)$"; "i"))] | length == 0)' >/dev/null
done
# A provider selected only through discovery retains its assembly attribute in trimmed builds.
for provider in Codex Claude Antigravity Grok OpenAiApi SelfHost; do
    project="src/$provider/Darkspyre.Inference.Connect.$provider/Darkspyre.Inference.Connect.$provider.csproj"
    dotnet msbuild "$project" -t:_GetPackageFiles -getItem:_PackageFiles -nologo |
        jq -e --arg target "Darkspyre.Inference.Connect.$provider.targets" \
            '[.Items._PackageFiles[] | select(.Filename + .Extension == $target and .PackagePath == "buildTransitive/")] | length == 1' >/dev/null
done
dotnet msbuild tests/LLM/Darkspyre.Inference.Connect.LLM.Tests/Darkspyre.Inference.Connect.LLM.Tests.csproj \
    -t:RootConnectDiscoveryProviders -getItem:TrimmerRootAssembly -nologo |
    jq -e '[.Items.TrimmerRootAssembly[].Identity] | sort ==
        ["Darkspyre.Inference.Connect.Antigravity", "Darkspyre.Inference.Connect.Claude", "Darkspyre.Inference.Connect.Codex", "Darkspyre.Inference.Connect.Grok", "Darkspyre.Inference.Connect.OpenAiApi", "Darkspyre.Inference.Connect.SelfHost"]' >/dev/null
# Each package's README.md is obj/<configuration>/<tfm>/package/README.md: its PACKAGE.md and eng/PackageFamily.md, each
# with trailing white space removed, one blank line between them and one final line break.
trim_end() {
    local text
    text="$(<"$1")"
    text="${text#$'\xEF\xBB\xBF'}"
    printf '%s' "${text%"${text##*[![:space:]]}"}"
}
family="$(trim_end ../eng/PackageFamily.md)"
packages=0
while read -r package; do
    project="../$package"
    readme="$(dotnet msbuild "$project" -t:_GetPackageFiles -getItem:_PackageFiles -p:Configuration=Release -nologo |
        jq -er '[.Items._PackageFiles[] | select(.PackagePath == "README.md")] |
            if length == 1 then .[0].FullPath else error("expected one README.md package file") end')"
    [[ "$readme" == */obj/*/package/README.md ]]
    cmp -s "$readme" <(printf '%s\n\n%s\n' "$(trim_end "$(dirname "$project")/PACKAGE.md")" "$family")
    packages=$((packages + 1))
done < <(sed -n 's/^    \(.*\.csproj\)$/\1/p' ../eng/pack.sh)
[[ $packages == 22 ]]
# A page holding a ticket key fails composition with the work-note error.
if result="$(dotnet msbuild ../eng/VerifyConsumer.Tests/PackagePageFixture.proj -t:ComposeFixturePage -nologo 2>&1)"; then
    printf 'Expected the work-note failure for a package page holding a ticket key\n' >&2; exit 1
fi
[[ "$result" == *"Fixture.PackagePage: the package page contains a work note ('DSA-1'); package pages hold consumer documentation only."* ]]
printf 'Package asset checks passed: independently packed libraries; no repacked native assets; output collisions rejected; each package packs its own page.\n'
