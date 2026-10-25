# Compilation inside one library

Ship one GregsEx NuGet package with one runtime assembly, `GregsEx.dll`, containing
the interpreted/table engine and optional runtime compiler. Keep the optional
build-time source generator independent of ordinary consumer use.

## Public boundary

Parsing produces a usable immutable pattern without runtime code generation.
`Parse`, `TryParse`, workspaces, and ordinary matching remain NativeAOT-safe.
Runtime compilation is a separate explicit operation:

```csharp
[RequiresDynamicCode(
    "Regex compilation requires dynamic code. Use the parsed pattern directly under NativeAOT.")]
[RequiresUnreferencedCode(
    "FastExpressionCompiler is not supported in trimming scenarios. Use the parsed pattern directly in trimmed applications.")]
public RegexPattern Compile();
```

AOT/trim analysis emits IL3050/IL2026 at callers of `Compile()`. These attributes
do not enable runtime emission or preserve reflection metadata.

`Compile()` checks `RuntimeFeature.IsDynamicCodeSupported` and
`RuntimeFeature.IsDynamicCodeCompiled`, throwing `PlatformNotSupportedException`
when either is false. Backend ineligibility throws `NotSupportedException`.
Compilation preserves source semantics and resource ceilings, returns an immutable
pattern, and leaves the original pattern and workspaces unchanged. Matching a
compiled pattern does not itself generate code.

`RegexExecutionMode` exposes `Interpreted` and `Auto`, not a strict `Compiled`
parsing option. `ExecutionBackend` reports the actual backend. Auto compilation
requires runtime feature guards, eligibility/cost checks, and the
`GregsEx.RuntimeCompilation.Auto` switch. Package targets disable that switch
for trimmed and NativeAOT publication. Known ineligibility can select the
interpreter; unexpected compiler exceptions and exhaustion cannot be hidden as
successful fallback. Any analyzer suppression needs a precise reachability
justification and regression fixture.

## Source dependency and lowering

Use the pinned private source package (version `6.0.0` in `Directory.Packages.props`):

```xml
<PackageReference Include="FastExpressionCompiler.Internal.src"
                  PrivateAssets="all" />
```

Its compatible net8.0 Compile content assets become internal code in the
net8.0 and net10.0 `GregsEx.dll`. The netstandard2.0 asset does not reference FEC:
there `IsRuntimeCompilationAvailable` is false, explicit `Compile()` throws
`PlatformNotSupportedException` and Auto never selects the compiled backend. Preserve those source assets and inspect restore/package outputs:
no FEC DLL, transitive FEC dependency, or public FEC type may ship. Distribute the
upstream MIT notice. Do not substitute the binary or public-source package.

Lower validated plans to `System.Linq.Expressions` trees and use strongly typed
span delegates. GregsEx does not implement its own Reflection.Emit backend.
FEC itself emits dynamic code and remains subject to the same runtime guards.

Call `CompileFast<TDelegate>(ifFastFailedReturnNull: true)` to disable its
Expression.Compile fallback. Null means known ineligibility; invalid trees/IL
and resource failures remain explicit errors. Production delegates do not retain
debug expression trees.

Use separate char/byte delegates with direct span, workspace, result, and budget
parameters. No per-match DynamicInvoke, object arrays, boxing, input copies,
or captured spans/workspaces. Bound tree node count, depth, and estimated code
size before compilation. See [acceleration.md](acceleration.md) for the ABI,
lowering limits, and first-invocation pitfalls.

## Trimming and verification

FEC 5.4.1 uses reflection/IL internals and declares trimming unsupported.
Dynamic-code availability does not prove retained metadata. Explicit `Compile()`
is a trim-unsafe boundary; ordinary interpreted/Auto consumers stay warning-free.
Do not blanket-suppress IL2026 or claim arbitrary trimmed JIT compilation safe.

Keep compiler helpers, generated delegates, and reflection metadata out of shared
static initialization and unconditional registries. The linker must be able to
remove unreachable emission code from the same assembly. Keep
`IsAotCompatible=true`; published consumers, not that property alone, establish
deployment compatibility.

Verify span/ref/out calls, loops, branches, captures, budgets, unsupported
lowering, immutable conversion, concurrent separate workspaces, and disabled
fallback. Publish/run safe NativeAOT and trimmed consumers; dedicated fixtures
check explicit-call warnings and unsupported-runtime exceptions. Inspect package
and published artifacts for compiler removal and correct license/dependency
boundaries. See [verification.md](verification.md).

Measure plan-to-tree construction, FEC compilation, first invocation/JIT,
warmed matching, allocations, retained memory, and break-even reuse separately.
Upstream Expression.Compile comparisons do not establish GregsEx speedups.
Expression.Compile is permitted only as an isolated benchmark comparator.

`OnigurumaPattern.Compile()` and Oniguruma generated patterns follow the same
boundary (annotations, runtime guards, no fallback engine, the interpreter owning
every case the kernel does not model); see [oniguruma.md](oniguruma.md).

Acceptance IDs ARCH-01, API-06, PERF-001/015, and VERIFY-04/05 cover these
requirements.
