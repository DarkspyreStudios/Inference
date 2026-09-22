# Repository instructions

- The project-specific engineering laws in `.roles/laws.md` apply to all design, implementation,
  review, and release work in this repository.
- `Darkspyre.Inference.Connect.LLM` is the single canonical implementation. Consumers reference the private NuGet packages; do not copy its source into an application repository.
- Preserve the provider-neutral public API. Provider-specific wire formats and credentials stay inside their adapters.
- Never log or commit credentials, OAuth tokens, secret headers, or raw provider responses containing secrets.
- Keep the solution at zero warnings and run restore, build, test, format verification, pack, and the dependency vulnerability audit before release.
- Package releases use semantic versions and immutable Git tags. Never overwrite an already-published package version.
