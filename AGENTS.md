# Repository instructions

- The project-specific engineering laws in `.roles/laws.md` and repository conventions in
  `.roles/design-rules.md` apply to all design, implementation, review, and release work.
- This is the canonical `Darkspyre.Inference` repository. `Connect` contains external-provider
  connection capabilities; `Console` and `Tensor` are separate product roots.
- `Darkspyre.Inference.Connect.LLM` is the canonical LLM connection implementation. Development consumers use sibling project references; release consumers use the private NuGet packages.
  Do not copy its source into an application repository.
- Preserve the provider-neutral public API. Provider-specific wire formats and credentials stay inside their adapters.
- Never log or commit credentials, OAuth tokens, secret headers, or raw provider responses containing secrets.
- Keep each active product solution at zero warnings and run restore, build, test, format
  verification, pack, and the dependency vulnerability audit before release.
- Package releases use semantic versions and immutable Git tags. Never overwrite an already-published package version.

## Local development and releases

Normal builds and tests use local source from the seven sibling workspace worktrees: agent-ds,
AgentDSPlugins, Inference, TensorSharp, RightSize, SecureStore and PersistenceStore. No source-root
flags or per-session local props files are required. Missing source must fail without a package fallback.
Do not build in primary checkouts or create interim NuGet packages. Read `~/.agent-ds/build-instructions.md`.

`DarkspyreDependencyMode=Packages` is explicit release publishing/deployment mode. Debug/Release,
signing and interim installation do not select it. Test copies retain compatible prebuilt GGML driver
hardlinks. Signed bundles may copy prebuilt drivers before signing. Native compilation always needs
an explicit request.
