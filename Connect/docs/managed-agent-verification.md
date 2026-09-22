# Direct Codex and managed agent verification

Implementation and release verification for version 3.0.0 on 2026-09-21. This is a breaking-major
change from 2.2.0; the checks below distinguish live implementation proof from release gates.

## Live provider proof

All generation used the repository console tester, direct ChatGPT subscription OAuth already held
by its protected connection source, and the live catalog's `gpt-5.5` model. No CLI credential was
read or imported. These tests consume subscription usage; do not put them in routine unit tests.

| Command (after `dotnet run --project tools/LlmConnect.ConsoleTester --`) | Observed result |
| --- | --- |
| `models --provider codex` | Direct authenticated model listing returned `gpt-5.5` |
| `prompt --provider codex --model gpt-5.5 --effort low`, stdin `Hello. Reply with Hello only.` | `Hello`, completed |
| Same prompt command plus `--attach-document artifacts/codex-protocol-document.txt` | Correctly returned the document fixture's verification word, `cerulean` |
| `codex-probe --provider codex --model gpt-5.5` | Required tool call, document tool-result bytes, ordered multi-turn state replay, image prompt accepted |
| `agent --provider codex --model gpt-5.5 --effort low`, request document attachment tool | Tool started/completed; model quoted the fixture text |
| Same agent command, request image attachment tool | Tool started/completed; model described the tiny image fixture |
| `managed-probe --provider codex --model gpt-5.5` | Managed document tool loop; JSON capture/reopen with bytes and no tool replay; independent fork; prospective low-cap pressure; host compaction and same-turn continuation retaining the instruction override without duplicate input |
| `usage --provider codex` | Direct quota window, plan, workspace balance and available reset count; no reset was consumed |

The live backend returned complete items through `response.output_item.done` while terminal
`response.output` was empty. The shared parser now retains those items in output-index order.
Tests pin this behavior separately from the live probe. Raw provider responses and credentials
are not stored in this document or fixtures.

## Deterministic coverage and limits

Tests cover direct headers/payloads, tools and attachments, retained reasoning/assistant phase,
account/model binding, quota nulls, tokenizer honesty, context snapshots/revisions, protected groups,
same-run continuation, steering, bounded parallelism, cancellation settlement, tool validation,
truncation/refusal, transient retries, and native adapter regressions.

Provider overflow is tested with controlled protocol fixtures, not by deliberately exhausting a
real model's context window. Live compaction uses a deliberately small host context cap. Exact
attachment/opaque-reasoning token occupancy is unavailable and remains null. The initial text
estimate uses o200k and is explicitly approximate. No claim is made that every document format
has been live-tested; the live document fixture is UTF-8 text.

Agent DS's actual store/compactor integration is consumer work. This library supplies the engine
and contract, not another durable harness. Claude/AgentCore retain their native capabilities;
their protocols were regression-tested locally, not requalified with live accounts in this pass.

The tokenizer dependency introduced an audited transitive `Microsoft.Bcl.Memory` vulnerability.
The central pin selects patched 10.0.9 alongside the project's .NET 10 dependencies; no advisory
was suppressed. See [GHSA-73j8-2gch-69rq](https://github.com/advisories/GHSA-73j8-2gch-69rq).

Version 3.0.0 uses the approved local release environment and private GitHub Packages feed,
with the immutable `v3.0.0` Git tag. GitHub Actions is not the publication pipeline.

## Completed implementation gates

- `dotnet restore`: passed.
- `dotnet build --no-restore -c Release`: passed, zero warnings/errors, including compiled examples.
- `dotnet test --no-restore -c Release`: 170 passed, zero failures/skips.
- `dotnet format --no-restore --verify-no-changes`: passed.
- `dotnet list package --vulnerable --include-transitive`: no vulnerable dependencies reported.
- `dotnet pack --no-restore -c Release --output artifacts/managed-agent-validation -p:Version=3.0.0-dev.20260921`:
  both packages created locally. This command-only development version does not change the release
  version, create a Git tag, or publish anything.
- `git diff --check`: passed; no undecided/untracked source paths remain.

## Completed 3.0.0 release gates

- Solution restore: passed.
- Release build: zero warnings and errors, including console tester and compiled examples.
- Release tests without rebuilding/restoring: 170 passed, zero failures or skips.
- Format verification without changes: passed.
- Transitive dependency vulnerability audit: no vulnerable packages reported in any project.
- Release pack: both `Mathis.LlmConnect` and `Mathis.LlmConnect.Bedrock` built at 3.0.0.

The live provider proof above was completed during implementation; release packaging does not
repeat paid provider calls. Consumer migration instructions are in the
[Agent DS handoff](agent-ds-contract-handoff.md).
