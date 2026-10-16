# Product specifications

| Specification | Scope |
| --- | --- |
| [API](api.md) | Patterns, workspaces, results, ownership, offsets, and failures |
| [Architecture](architecture.md) | Shared plans, execution backends, resource accounting, and safety |
| [Flavors](flavors.md) | Compatibility profiles, semantic requirements, and oracle coverage |
| [RegularCore](regular-core.md) | Experimental regular subset and core failure precedence |
| [ECMAScript](ecmascript.md) | Edition 16 grammar, Unicode data, and typed host operations |
| [XML](xml.md) | XSD 1.1 and XPath/XQuery F&O 3.1 regex semantics |
| [POSIX](posix.md) | Issue 7 C-byte BRE/ERE profiles and submatch selection |
| [PCRE2](pcre2.md) | Managed 10.46 standard8/16 profile and native-width host operations |
| [Acceleration](acceleration.md) | Eligibility, dispatch, fallback, and lowering interfaces |
| [Compilation](compilation.md) | Optional FEC compilation, annotations, trimming, and package boundaries |
| [Performance](performance.md) | Workloads, controlled allocations, reproducibility, and thresholds |
| [Verification](verification.md) | Semantic/resource tests, deployment checks, and build commands |

Versioned conformance data and pinned oracle observations are in
[`test/corpora`](../../test/corpora). Development-only adapters are in
[`tools/oracles`](../../tools/oracles).
