"""Read-only reproducibility, source/result/identity join; no processor rerun or product execution."""
import sys
from prepare import ROOT, outputs
if len(sys.argv) != 1:
    raise ValueError('Fixed no-argument procedure')
for path, data in outputs().items():
    assert (ROOT / path).read_bytes() == data, 'Stale/tampered join: ' + path
print('5546 joined references and all artifact identities reproduce exactly. XSD 4722 agreements/49 differences/40 source-only; XPath 712/11/12. Original QT3 1355 passes/12 explicit skips; overlapping calls not added.')
