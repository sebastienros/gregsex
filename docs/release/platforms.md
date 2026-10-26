# Platform, Trimming, and NativeAOT Support Matrix

`GregsEx` ships as a single NuGet package (`GregsEx`) containing:

- `lib/net10.0/`, `lib/net8.0/` and `lib/netstandard2.0/` (`GregsEx.dll` and `GregsEx.xml`): the single runtime assembly and XML API documentation for all six supported regex profiles (`RegularCore`, `ECMAScript`, `XSD`, `XPath`, `POSIX`, `PCRE2`).
- `analyzers/dotnet/cs/GregsEx.Generator.dll`: the Roslyn incremental source generator implementing `[GeneratedPattern]`, and `GregsEx.SourceGeneration.KernelSource`, the kernel-emission entry point for third-party generators.
- `build/GregsEx.targets` and `buildTransitive/GregsEx.targets`: MSBuild targets that automatically configure the `GregsEx.RuntimeCompilation.Auto` feature switch when `<PublishTrimmed>true</PublishTrimmed>` or `<PublishAot>true</PublishAot>` is enabled.
- `licenses/`: MIT license notices for `FastExpressionCompiler` (`THIRD-PARTY-NOTICES.txt`) and data/specification notices for Unicode 16.0.0/17.0.0, XML, and PCRE2.

Each target is the same runtime assembly built from the same sources. NuGet picks the asset for the
application's target framework, so a net10.0 application gets `lib/net10.0` even when it reaches GregsEx through
a netstandard2.0-only library such as TextMateSharp. Assets differ only in how fast they run:

| Asset | Runtime compilation (`Compile()`, Auto compiled backend) | `[GeneratedPattern]` kernels | Search primitives | Dependencies |
| --- | --- | --- | --- | --- |
| `net10.0` | Supported (FastExpressionCompiler) | Emitted | BCL vectorized `SearchValues`, `IndexOfAny*` | None |
| `net8.0` | Supported (FastExpressionCompiler) | Emitted | BCL vectorized, except multi-string sets (`SearchValues<string>` is .NET 9+) use a first-character prefilter | None |
| `netstandard2.0` | Not available: `Compile()` throws `PlatformNotSupportedException`, Auto stays interpreted/accelerated | Not emitted: the generator reports `GREGEX003` and the pattern is parsed at run time | Scalar polyfills (Meziantou.Polyfill plus `Polyfills/` in the source) | `System.Memory` |

Matching results are identical on every asset. NativeAOT and trimming qualification applies to the net8.0 and net10.0
assets (`IsAotCompatible`).

ECMAScript, XSD and XPath profiles select `UnicodeVersion.Unicode16_0` (the
unchanged default) or `UnicodeVersion.Unicode17_0`. Both datasets are embedded
and AOT-safe; no runtime downloads or host Unicode tables are used.
`GeneratedPattern` accepts a matching `UnicodeVersion` named property.
PCRE2 10.46 remains tied to its own Unicode 16.0 compatibility dataset.

## Supported Runtime Identifier (RID) Matrix

| RID | OS / Architecture | Standard JIT | Trimmed JIT (`PublishTrimmed=true`) | NativeAOT (`PublishAot=true`) |
| --- | --- | --- | --- | --- |
| `linux-x64` | Linux x86-64 | Supported | Supported | Supported |
| `linux-arm64` | Linux ARM64 | Supported | Supported | Supported |
| `win-x64` | Windows x86-64 | Supported | Supported | Supported |
| `osx-x64` | macOS x86-64 | Supported | Expected to work, not CI-qualified | Expected to work, not CI-qualified |
| `osx-arm64` | macOS Apple Silicon (ARM64) | Supported | Supported | Supported |

`osx-x64` is not part of the NativeAOT and trimming qualification matrix. Its trimmed and NativeAOT builds use the same managed code and warning analysis as the qualified RIDs, but they are not published or run in CI. `linux-x64` and `win-x64` cover x86-64 code generation, and `osx-arm64` covers the macOS toolchain.

## Execution Modes Across Deployment Models

| Execution Path | Standard JIT (`PublishTrimmed` unset/`false`) | Trimmed JIT (`PublishTrimmed=true`) | NativeAOT (`PublishAot=true`) | Warnings |
| --- | --- | --- | --- | --- |
| `RegexExecutionMode.Interpreted` | Interpreted VM + SIMD prefilters (`OnePass`, `LazyDfa`, literal engines) | Interpreted VM + SIMD prefilters | Interpreted VM + SIMD prefilters | Zero (`IL2026` / `IL3050` clean) |
| `RegexExecutionMode.Auto` (default) | Compiles eligible UTF-16 patterns via internal `FastExpressionCompiler` when within budget; falls back to interpreter | Substituted to `false` via `GregsEx.RuntimeCompilation.Auto`; stays on accelerated interpreter and ILLink trims `FastExpressionCompiler` + `CoreKernelLowering` | Guarded by `RuntimeFeature.IsDynamicCodeSupported` (`false`) and `GregsEx.RuntimeCompilation.Auto` (`false`); ILC strips `FastExpressionCompiler` + `CoreKernelLowering` | Zero (`IL2026` / `IL3050` clean) |
| `[GeneratedPattern(...)]` | Build-time C# kernel emitted by `GregsEx.Generator` (`ExecutionBackend.Compiled`) | Build-time C# kernel (`ExecutionBackend.Compiled`) | Build-time C# kernel (`ExecutionBackend.Compiled`) | Zero (`IL2026` / `IL3050` clean) |
| `RegexPattern.Compile()` | Explicit runtime compilation via `FastExpressionCompiler` (`ExecutionBackend.Compiled`) | Annotated with `[RequiresUnreferencedCode]` (`IL2026`) | Annotated with `[RequiresDynamicCode]` (`IL3050`); throws `PlatformNotSupportedException` at runtime | `IL2026` + `IL3050` at caller site |

## Feature Switch Configuration

`buildTransitive/GregsEx.targets` injects:

```xml
<ItemGroup Condition="'$(PublishTrimmed)' == 'true' or '$(PublishAot)' == 'true'">
  <RuntimeHostConfigurationOption Include="GregsEx.RuntimeCompilation.Auto"
                                  Value="false"
                                  Trim="true" />
</ItemGroup>
```

When `GregsEx.RuntimeCompilation.Auto` is `false` with `Trim="true"`, ILLink and ILC fold `CompilationContract.IsAutoCompilationSwitchEnabled` (`[FeatureSwitchDefinition("GregsEx.RuntimeCompilation.Auto")]`) to `false` and eliminate `RegexPattern.TryAutoCompile`, `CoreKernelLowering`, and the embedded internal `FastExpressionCompiler` types whenever `RegexPattern.Compile()` is not explicitly called by the application.

## Build and Release Automation

The workflows follow [Parlot's automation](https://github.com/sebastienros/parlot/tree/main/.github/workflows), adapted for GregsEx's .NET 10 SDK, Microsoft.Testing.Platform runner, package feed, and deployment qualification.

- [Build](../../.github/workflows/build.yml) restores, builds, and tests on Linux and Windows for pull requests, pushes to `main`, and manual runs. A push to `main` or a manual run on `main` also packs `preview.<run-number>` packages and publishes them to the [GregsEx Feedz feed](https://f.feedz.io/sebastienros/gregsex/nuget/index.json) when the `FEEDZ_IO_API_KEY` repository secret is configured. Pull requests and manual runs on other branches do not publish packages.
- [NativeAOT and trimming qualification](../../.github/workflows/aot.yml) publishes and runs real consumers on `linux-x64`, `linux-arm64`, `win-x64`, and `osx-arm64`. Ordinary consumers treat warnings as errors; the dedicated unsuppressed diagnostic build must report both IL3050 and IL2026, and the negative runtime fixture must verify the explicit compilation guard. This workflow runs on pull requests and pushes to `main`, supports manual runs, and is reusable by the release workflow.
- [Publish](../../.github/workflows/publish.yml) runs for `v<major>.<minor>.<patch>` tags, optionally followed by a SemVer prerelease suffix. All four deployment qualification jobs must pass before the release job starts. The tag must identify a commit contained in `origin/main`. The release job then builds, tests, packs the tagged version, attests package provenance, and publishes to NuGet.org using trusted publishing.

Before publishing a release, maintainers must configure:

1. A GitHub environment named `release`, with the desired reviewers and tag deployment restrictions.
2. A NuGet.org trusted publishing policy for the `sebastienros` account, repository owner `sebastienros`, repository `gregsex`, workflow file `publish.yml`, and environment `release`. The workflow uses GitHub OIDC (`id-token: write`) and `NuGet/login` to obtain a short-lived API key; no stored NuGet.org API key secret is required.
3. The `FEEDZ_IO_API_KEY` repository secret if development previews should be published. This is separate from NuGet.org trusted publishing.

GregsEx ships a portable library package, not a standalone application or native ABI. NativeAOT binaries are built and executed as release qualification, not attached as product downloads. Parlot's Explorer desktop workflow is not applicable.
