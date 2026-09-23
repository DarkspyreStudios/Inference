#!/usr/bin/env bash
# Offline checks: real disposable Git repositories, fake gh/dotnet. No network or publications.
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/eng/deploy.sh"
for case_value in '11.5.1-rc.1 major 11.6.0-rc.1' '11.5.1-rc.1 minor 11.5.2-rc.1' \
    '11.9.9 major 11.10.0' '12.0.0 minor 12.0.1' '11.5.9-preview.2 minor 11.5.10-preview.2'; do
    read -r before mode expected <<< "$case_value"
    [[ "$(next_version "$before" "$mode")" == "$expected" ]]
done
if next_version invalid minor >/dev/null 2>&1; then exit 1; fi
if next_version 11.5.1 invalid >/dev/null 2>&1; then exit 1; fi

test_directory="$(mktemp -d "${TMPDIR:-/tmp}/inference-deploy-tests.XXXXXX")"
export REAL_GIT="$(command -v git)"
export TEST_STATE="$test_directory/state"
mkdir -p "$test_directory/bin" "$TEST_STATE"
cat > "$test_directory/bin/git" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == remote && "${2:-}" == get-url ]]; then
    printf 'https://github.com/DarkspyreStudios/Inference.git\n'; exit 0
fi
exec "$REAL_GIT" "$@"
SH
cat > "$test_directory/bin/gh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
case "$1 $2" in
    'auth token') printf 'offline-test-token\n' ;;
    'repo view') printf 'PRIVATE\n' ;;
    'api user') printf 'offline-user\n' ;;
    api*)
        if [[ "$*" == *'/versions?'* ]]; then
            if [[ -f "$TEST_STATE/published" || "${TEST_EXISTING:-}" == 1 ]]; then printf '11.5.2-rc.1\n'; fi
        else printf 'private\n'; fi ;;
    *) exit 1 ;;
esac
SH
cat > "$test_directory/bin/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$1" >> "$TEST_STATE/commands"
if [[ "$1" == --version ]]; then printf '11.0.100-rc.1\n'; exit 0; fi
if [[ "$1" == build && "${TEST_BUILD_FAIL:-}" == 1 ]]; then exit 7; fi
if [[ "$1" == list ]]; then
    if [[ "${TEST_AUDIT_FAIL:-}" == 1 ]]; then
        printf '{"projects":[{"vulnerabilities":[{"severity":"High"}]}]}\n'
    else printf '{"projects":[{}]}\n'; fi
fi
if [[ "$1" == pack ]]; then
    mkdir -p "$TEST_REPO/artifacts/packages"
    for package in Common Connect Tensor UI.Settings; do
        printf '%s\n' "$package" > "$TEST_REPO/artifacts/packages/Darkspyre.Inference.$package.11.5.2-rc.1.nupkg"
    done
fi
if [[ "$1 $2" == 'nuget push' ]]; then
    printf 'push\n' >> "$TEST_STATE/pushes"
    touch "$TEST_STATE/published"
fi
if [[ "$1" == restore && -n "${NUGET_PACKAGES:-}" ]]; then
    for package in Common Connect Tensor UI.Settings; do
        lower="$(printf '%s' "Darkspyre.Inference.$package" | tr '[:upper:]' '[:lower:]')"
        mkdir -p "$NUGET_PACKAGES/$lower/11.5.2-rc.1"
        cp "$TEST_REPO/artifacts/packages/Darkspyre.Inference.$package.11.5.2-rc.1.nupkg" "$NUGET_PACKAGES/$lower/11.5.2-rc.1/$lower.11.5.2-rc.1.nupkg"
    done
fi
SH
chmod +x "$test_directory/bin/git" "$test_directory/bin/gh" "$test_directory/bin/dotnet"
export PATH="$test_directory/bin:$PATH"
unset NUGET_PACKAGES

fixture() {
    export TEST_REPO="$test_directory/$1"
    export TEST_STATE="$test_directory/$1-state"
    mkdir -p "$TEST_REPO/.roles" "$TEST_REPO/eng" "$TEST_REPO/Common" "$TEST_REPO/Connect" "$TEST_REPO/Tensor" "$TEST_REPO/UI" "$TEST_STATE"
    cp "$root/eng/deploy.sh" "$TEST_REPO/eng/"
    cp "$root/deploy-minor.sh" "$TEST_REPO/"
    printf 'artifacts/\n' > "$TEST_REPO/.gitignore"
    printf '# Project\n\n| Lifecycle stage | Prior release |\n' > "$TEST_REPO/.roles/project.md"
    printf '<Project>\n  <Version>11.5.1-rc.1</Version>\n</Project>\n' > "$TEST_REPO/Directory.Build.props"
    for document in README.md Common/README.md Connect/README.md Tensor/README.md UI/README.md; do
        printf 'Version 11.5.1-rc.1\n' > "$TEST_REPO/$document"
    done
    "$REAL_GIT" init -q --initial-branch=master "$TEST_REPO"
    "$REAL_GIT" -C "$TEST_REPO" config user.name 'Offline deploy test'
    "$REAL_GIT" -C "$TEST_REPO" config user.email 'test@example.invalid'
    "$REAL_GIT" -C "$TEST_REPO" add -- .gitignore .roles/project.md Directory.Build.props README.md Common/README.md Connect/README.md Tensor/README.md UI/README.md eng/deploy.sh deploy-minor.sh
    "$REAL_GIT" -C "$TEST_REPO" commit -qm fixture
    "$REAL_GIT" init -q --bare --initial-branch=master "$TEST_REPO-remote.git"
    "$REAL_GIT" -C "$TEST_REPO" remote add origin "$TEST_REPO-remote.git"
    "$REAL_GIT" -C "$TEST_REPO" push -q -u origin master
}

fixture dry-run
bash "$TEST_REPO/deploy-minor.sh" --dry-run > "$TEST_STATE/result"
[[ -z "$("$REAL_GIT" -C "$TEST_REPO" status --porcelain)" ]]
[[ ! -f "$TEST_STATE/commands" ]]

fixture dirty
printf 'uncommitted\n' >> "$TEST_REPO/README.md"
if bash "$TEST_REPO/deploy-minor.sh" > "$TEST_STATE/result" 2>&1; then exit 1; fi
[[ ! -f "$TEST_STATE/pushes" ]]

fixture existing
if TEST_EXISTING=1 bash "$TEST_REPO/deploy-minor.sh" > "$TEST_STATE/result" 2>&1; then exit 1; fi
[[ -z "$("$REAL_GIT" -C "$TEST_REPO" status --porcelain)" ]]
[[ ! -f "$TEST_STATE/pushes" ]]

fixture build-failure
if TEST_BUILD_FAIL=1 bash "$TEST_REPO/deploy-minor.sh" > "$TEST_STATE/result" 2>&1; then exit 1; fi
[[ ! -f "$TEST_STATE/pushes" ]]

fixture audit-failure
if TEST_AUDIT_FAIL=1 bash "$TEST_REPO/deploy-minor.sh" > "$TEST_STATE/result" 2>&1; then exit 1; fi
[[ ! -f "$TEST_STATE/pushes" ]]

fixture successful
bash "$TEST_REPO/deploy-minor.sh" > "$TEST_STATE/result" 2>&1
[[ "$(wc -l < "$TEST_STATE/pushes" | tr -d ' ')" == 4 ]]
[[ -z "$("$REAL_GIT" -C "$TEST_REPO" status --porcelain)" ]]
[[ "$("$REAL_GIT" -C "$TEST_REPO" rev-parse HEAD)" == "$("$REAL_GIT" -C "$TEST_REPO" rev-parse origin/master)" ]]
"$REAL_GIT" -C "$TEST_REPO-remote.git" show-ref --verify --quiet refs/tags/v11.5.2-rc.1
printf 'Offline deployment tests passed. Fixtures retained at %s\n' "$test_directory"
