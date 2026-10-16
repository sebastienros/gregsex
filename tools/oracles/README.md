# Independent oracle tools

Development-only adapters, corpus generators, native probe sources, and
replay/verification scripts live here, grouped by flavor. They are not production
matching backends or runtime dependencies.

Their versioned inputs, expected outcomes, retained observations, provenance, and
licenses live in [`test/corpora`](../../test/corpora). Flavor-specific procedures
document pinned engines and reproducibility requirements. Regeneration is an
explicit maintenance operation; ordinary tests read retained data without
rerunning these tools. Generated diagnostics and temporary output belong in
ignored `artifacts/`.
