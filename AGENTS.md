# GregsEx repository instructions

- Read the [API](docs/specs/api.md), [architecture](docs/specs/architecture.md), relevant [flavor contracts](docs/specs/flavors.md), [performance specification](docs/specs/performance.md), and [verification requirements](docs/specs/verification.md). Hot-path changes also follow the [performance guide and its applicability note](docs/performance-coding.md).
- Preserve versioned flavor semantics, native input offsets, explicit diagnostics, finite resource limits, and immutable patterns with exclusively owned mutable workspaces. Never silently switch flavors or forward production matching to another regex engine.
- New CLI argument parsing uses System.CommandLine. BenchmarkDotNet classes use `[MemoryDiagnoser]` and `[ShortRunJob]`. BenchmarkDotNet MemoryDiagnoser is the allocation authority; assess controlled costs and stable reserved storage without pursuing absolute zero at the expense of performance.
- Ship one GregsEx package/runtime assembly. The optional source generator must not be required for ordinary use. Keep `IsAotCompatible` enabled and default engine paths NativeAOT-compatible.
- Follow the [compilation boundary](docs/specs/compilation.md): explicit `Compile()` annotations and runtime guards, warning-free ordinary APIs/Auto, and isolated trimmable compiler code.
- Use pinned, private FastExpressionCompiler.Internal.src sources for optional runtime compilation, with no shipped FEC DLL or public FEC types. Lower validated plans to expression trees; do not build a project-owned Reflection.Emit backend. Disable CompileFast's Expression.Compile fallback.
- Run focused checks for changes; use the [verification commands](docs/specs/verification.md) for build, tests, packaging, and applicable deployment checks. Never describe skipped or unrun checks as passing.
- Preserve fixture provenance, pinned oracle versions, exact numeric input representations, source archives, and license notices. Update paths and integrity manifests together when relocating test data. Keep generated output in ignored `artifacts/`, not among checked-in fixtures.
- Run contention-sensitive measurements on a prepared, bounded command sequence, record actual host conditions, and preserve completed results if a later phase fails.
- Do not push, publish, or deploy without explicit user authorization. Existing CI can publish on push or tag.
