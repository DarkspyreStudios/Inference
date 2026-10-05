# Laya test fixtures

`tokenizer.json` is unmodified from `convaiinnovations/laya`, revision
`1c5edc17a7acd8701df6fc341c0d179f1c62c982`, `tokenizer/tokenizer.json`:
https://huggingface.co/convaiinnovations/laya/blob/1c5edc17a7acd8701df6fc341c0d179f1c62c982/tokenizer/tokenizer.json

Upstream declares Apache-2.0; see `LICENSE.txt`. SHA-256:
`6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30`.
This also matches the `convaiinnovations/laya-typed-decisions` tokenizer artifact.
No model weights are included or downloaded by tests.

`laya-tokenizer-golden.jsonl` is the 24-row reference supplied with the RightSize
serving-parity handoff. Its reported generator used transformers 5.17.0 and
tokenizers 0.23.2 against the revision above. Tests call the production C# tokenizer
offline; the broader RightSize checkpoint probability evaluation remains owned by
RightSize and is not replaced by these token-level tests.

The two agent configuration fixtures are `rl_agent_config.json` from:

- `convaiinnovations/laya` at the tokenizer revision above (`max_len:512`, `head_max_len:192`).
- `convaiinnovations/laya-typed-decisions` at revision
  `f9ab0b228f0fc0f14d873dbc99038f135c2da1b2` (`max_len:1024`, `head_max_len:256`).

Both upstream repositories declare Apache-2.0. JSON values are unchanged; only a final newline
is added for repository formatting. These fixtures prevent substituting either checkpoint's
serving defaults for another's.
