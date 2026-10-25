#!/usr/bin/env bash
# Shared implementation for the two root deployment commands. Never enable xtrace: credentials
# are process-scoped environment variables and must not appear in logs or tracked files.
set +x
set -eEuo pipefail

export DarkspyreDependencyMode=Packages

next_version() {
    local current="$1" mode="$2" net release minor suffix
    if [[ ! "$current" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-[0-9A-Za-z.-]+)?$ ]]; then
        printf 'Unsupported family version: %s\n' "$current" >&2
        return 1
    fi
    net="${BASH_REMATCH[1]}"
    release="${BASH_REMATCH[2]}"
    minor="${BASH_REMATCH[3]}"
    suffix="${BASH_REMATCH[4]:-}"
    case "$mode" in
        major) release=$((10#$release + 1)); minor=0 ;;
        minor) minor=$((10#$minor + 1)) ;;
        *) printf 'Expected major or minor.\n' >&2; return 1 ;;
    esac
    printf '%s.%s.%s%s\n' "$net" "$release" "$minor" "$suffix"
}

fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

main() {
    local mode="${1:-}" option="${2:-}" root current version source_commit family configuration
    local release_token id code package artifact
    local stage='preflight'
    local -a packages=(Common Connect Connect.Common Connect.Codex Connect.Claude Connect.HostTools Connect.Antigravity Connect.Grok Connect.OpenAiApi Connect.SelfHost Tensor.Common Tensor.Decision.Laya Tensor.Embedding Tensor.Embedding.Gemma Tensor.Ggml Tensor.Image.Supra Tensor.LLM.Qwen Tensor.Registry Tensor.STT.Whisper UI.Settings.Core UI.Settings.Avalonia UI.Settings.Blazor)
    local -a version_files=(Directory.Build.props README.md Common/README.md Connect/README.md Tensor/README.md UI/README.md)
    [[ "$mode" == major || "$mode" == minor ]] || fail 'Use deploy-major.sh or deploy-minor.sh.'
    if [[ "$option" == --help ]]; then
        printf 'Usage: deploy-%s.sh [--dry-run]\n' "$mode"
        printf 'Bumps <net>.<release>.<minor>, preserving the suffix; validates and packs the library packages.\n'
        printf 'Requires a clean master checkout, origin, a Darkspyre.Common checkout beside it, the pinned .NET SDK, and a gh login.\n'
        printf 'Commits the version update and pushes master plus the release tag; the Publish workflow then publishes the tag to nuget.org.\n'
        printf 'INFERENCE_GITHUB_USER optionally selects an existing gh account without switching the active login.\n'
        return
    fi
    [[ $# -le 2 && ( -z "$option" || "$option" == --dry-run ) ]] || fail 'Only --dry-run or --help is supported.'
    root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
    cd "$root"
    current="$(sed -n 's/^[[:space:]]*<Version>\([^<]*\)<\/Version>[[:space:]]*$/\1/p' Directory.Build.props)"
    version="$(next_version "$current" "$mode")"
    printf 'Family release: %s -> %s\nPublished by the Publish workflow to nuget.org from tag v%s.\n' "$current" "$version" "$version"
    [[ "$option" != --dry-run ]] || return 0

    # Do not hide failures with retries, overwrite switches, automatic rollback, or a new version.
    trap 'printf "Deployment stopped during %s. No rollback was attempted. Inspect Git before retrying; rerunning bumps again.\n" "$stage" >&2' ERR
    for tool in git gh dotnet sed awk jq curl; do command -v "$tool" >/dev/null || fail "Install $tool before deploying."; done
    [[ -z "$(git status --porcelain --untracked-files=normal)" ]] || fail 'Commit or remove pending changes first; deployment will not stage unrelated work.'
    [[ "$(git branch --show-current)" == master ]] || fail 'Deploy from master.'
    [[ "$(git remote get-url origin)" == 'https://github.com/DarkspyreStudios/Inference.git' ||
       "$(git remote get-url origin)" == 'git@github.com:DarkspyreStudios/Inference.git' ]] || fail 'origin must be DarkspyreStudios/Inference.'
    git check-ref-format "refs/tags/v$version"
    ! git show-ref --verify --quiet "refs/tags/v$version" || fail "Tag v$version already exists."
    (cd Connect && dotnet --version) # Resolve the pinned SDK before changing files.

    if [[ -n "${INFERENCE_GITHUB_USER:-}" ]]; then
        release_token="$(gh auth token --hostname github.com --user "$INFERENCE_GITHUB_USER")"
    elif [[ -n "${GH_TOKEN:-}" ]]; then
        release_token="$GH_TOKEN"
    else
        release_token="$(gh auth token --hostname github.com)"
    fi
    export GH_TOKEN="$release_token"
    git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch origin master --tags
    git merge-base --is-ancestor origin/master HEAD || fail 'Local master is behind or diverged from origin/master; reconcile before deploying.'
    ! git show-ref --verify --quiet "refs/tags/v$version" || fail "Remote tag v$version already exists."
    for package in "${packages[@]}"; do
        id="$(printf '%s' "darkspyre.inference.$package" | tr '[:upper:]' '[:lower:]')"
        code="$(curl -s -o /dev/null -w '%{http_code}' "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.nuspec")"
        [[ "$code" == 404 ]] || fail "$package $version is already on nuget.org (HTTP $code); versions are immutable."
    done

    stage='version update'
    # Only current product entry points are updated. Historical release/design records stay intact.
    for artifact in "${version_files[@]}"; do
        awk -v before="$current" -v after="$version" '
            { line=$0; out=""; while ((position=index(line,before))>0) {
                out=out substr(line,1,position-1) after; line=substr(line,position+length(before));
              } print out line; }
        ' "$artifact" > "$artifact.deploy-tmp"
        mv -- "$artifact.deploy-tmp" "$artifact"
    done
    git add -- "${version_files[@]}"
    git diff --cached --check
    git commit -m "Prepare coordinated $version release" -- "${version_files[@]}"
    source_commit="$(git rev-parse HEAD)"

    stage='restore/build/test/audit'
    for family in Connect Tensor UI; do
        cd "$root/$family"
        dotnet restore "Darkspyre.Inference.$family.slnx"
        for configuration in Debug Release; do
            dotnet build "Darkspyre.Inference.$family.slnx" -c "$configuration" --no-restore
            dotnet test "Darkspyre.Inference.$family.slnx" -c "$configuration" --no-build --no-restore
        done
        mkdir -p "$root/artifacts/deploy"
        dotnet list "Darkspyre.Inference.$family.slnx" package --vulnerable --include-transitive --no-restore --format json > "$root/artifacts/deploy/$family-audit.json"
        jq -e '(.projects | type == "array" and length > 0) and
            ([.. | objects | select(has("vulnerabilities")) | .vulnerabilities[]?] | length == 0) and
            ([.. | objects | select(.level? == "error" or .level? == "warning")] | length == 0)' \
            "$root/artifacts/deploy/$family-audit.json" >/dev/null || fail "Dependency audit failed; inspect artifacts/deploy/$family-audit.json."
    done
    stage='pack and local consumer verification'
    bash "$root/eng/pack.sh" "$version" "$source_commit"
    cd "$root/Connect"
    dotnet restore ../eng/VerifyConsumer --configfile ../eng/VerifyConsumer/NuGet.Preflight.Config
    dotnet run --project ../eng/VerifyConsumer -c Release --no-restore -- "$version" "$source_commit"
    [[ -z "$(git status --porcelain --untracked-files=normal)" && "$(git rev-parse HEAD)" == "$source_commit" ]] || fail 'Source changed during verification; nothing will be tagged.'

    stage='tag and source push'
    git tag -a "v$version" "$source_commit" -m "Verified library $version release"
    git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push origin HEAD:refs/heads/master "refs/tags/v$version"
    printf 'Tagged v%s at %s. Run the Publish workflow on tag v%s to publish it to nuget.org.\n' "$version" "$source_commit" "$version"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
