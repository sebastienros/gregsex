# Conformance corpora

Versioned test inputs, expected outcomes, pinned independent oracle observations,
source provenance, integrity manifests, and licenses live here, grouped by flavor.
Tests consume these retained assets without requiring external regex engines.
Numeric input units and permitted result sets are part of the fixture contracts.

Generators, native probe sources, and replay/verification scripts live in
[`tools/oracles`](../../tools/oracles). Update affected manifests when changing
fixture paths or bytes. Do not replace golden expectations with GregsEx output.
Generated diagnostics and temporary output belong in ignored `artifacts/`, not
among these checked-in fixtures.
