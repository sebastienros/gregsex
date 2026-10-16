"""Deterministic joined index. Original child sources, observations and expectations are read-only."""
import collections
import hashlib
import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[4]
BASE = 'test/corpora/xml/'
INDEX = 'tools/oracles/xml/integration/index.json'
MANIFEST = BASE + 'manifest.json'


def read(path):
    return json.loads((ROOT / path).read_text())


def encoded(value):
    return (json.dumps(value, indent=2, ensure_ascii=True) + '\n').encode()


def identity(path, data=None):
    data = (ROOT / path).read_bytes() if data is None else data
    return dict(path=path, bytes=len(data), sha256=hashlib.sha256(data).hexdigest())


def outputs():
    lanes = []
    entries = []
    for lane, filename, profile, count, revision in [
        ('xsd', 'corpus.json', 'Xsd11', 4811, '0c5647fbf57bca9c782e7d04247bf8fbefda4ca4'),
        ('xpath', 'Corpus.json', 'XPath31', 735, 'f6f218812a49b067894702e869aeed15b937710e')]:
        path = BASE + lane + '/' + filename
        corpus = read(path)
        assert corpus['contractVersion'] == 'xml-fixtures-1'
        assert corpus['dataId'] == 'Unicode-16.0.0+XML-1.0-5e'
        assert len(corpus['cases']) == count
        if lane == 'xsd':
            diagnostics = {d['id']: d for d in read(BASE + lane + '/diagnostic-provenance.json')['cases']}
            results = {r['id']: r for r in read(BASE + lane + '/processor-results.json')['results']}
            differences = {d['id']: d for d in read(BASE + lane + '/disagreements.json')['cases']}
            assert len(differences) == 49
        else:
            diagnostics = read(BASE + lane + '/annotations.json')
            results = {r['id']: r for r in read('test/corpora/xml/xpath/oracle/comparison.json')['fixtures']}
            differences = read(BASE + lane + '/dispositions.json')
            assert len(differences) == 11
        assert len(results) == count
        for ordinal, c in enumerate(corpus['cases']):
            assert c['profileId'] == profile
            outcome = results[c['id']]['outcome']
            category = ('source-only' if outcome in ('not-applicable', 'source-only') else
                        'reviewed-difference' if c['id'] in differences else 'agreement')
            policy = diagnostics.get(c['id'], {})
            entries.append(dict(id=c['id'], lane=lane, ordinal=ordinal,
                                oracleComparison=category, diagnosticPolicy=policy.get('coordinateAuthority') if lane == 'xsd'
                                else policy.get('diagnosticPolicy')))
        counts = dict(collections.Counter(e['oracleComparison'] for e in entries if e['lane'] == lane))
        expected = dict(agreement=4722 if lane == 'xsd' else 712,
                        **{'reviewed-difference': 49 if lane == 'xsd' else 11, 'source-only': 40 if lane == 'xsd' else 12})
        assert counts == expected
        lanes.append(dict(id=lane, profileId=profile, corpus=path, fixtureCount=count,
                          integratedRevision=revision, counts=counts,
                          inventory=BASE + lane + ('/coverage.json' if lane == 'xsd' else '/inventory.json'),
                          diagnostics=BASE + lane + ('/diagnostic-provenance.json' if lane == 'xsd' else '/annotations.json'),
                          dispositions=BASE + lane + ('/disagreements.json' if lane == 'xsd' else '/dispositions.json'),
                          processor=BASE + lane + ('/processor-pin.json' if lane == 'xsd' else '/processor-lock.json'),
                          results=BASE + lane + '/processor-results.json' if lane == 'xsd'
                          else 'test/corpora/xml/xpath/oracle/oracle-results.xml'))
    assert len(entries) == len({e['id'] for e in entries}) == 5546
    index = encoded(dict(format='xml-joined-index-1', cases=entries))
    paths = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '--', BASE + 'xsd/', BASE + 'xpath/',
        'tools/oracles/xml/xsd/', 'tools/oracles/xml/xpath/',
        'test/corpora/xml/xsd/oracle/', 'test/corpora/xml/xpath/oracle/'], cwd=ROOT, text=True, timeout=30).splitlines()
    paths = [p for p in paths if (ROOT / p).is_file()]
    paths += ['LICENSE', 'docs/specs/xml.md', 'test/corpora/xml/sources/source-manifest.json',
              'test/GregsEx.Tests/Contracts/Xml/XmlFixtures.schema.json',
              'test/GregsEx.Tests/Contracts/Xml/ContractVectors.json']
    original = read('test/corpora/xml/xpath/oracle/comparison.json')
    assert original['counts']['qt3'] == dict(passed=1355, skipped=12) and not original['unexpected']
    skips = [r for r in original['qt3'] if r['outcome'] == 'skipped']
    assert all(r['reason'] for r in skips)
    manifest = dict(format='xml-joined-manifest-1', contractVersion='xml-fixtures-1',
        dataId='Unicode-16.0.0+XML-1.0-5e', fixtureCount=5546, lanes=lanes,
        index=identity(INDEX, index), artifacts=[identity(p) for p in sorted(set(paths))],
        originalQt3=dict(total=1367, passed=1355, skipped=12, skips=skips,
            normalizedImportedFixtures=509,
            accounting='Normalized calls overlap original query replay; do not add counts as distinct coverage.'),
        policy=dict(expectations='Original source-derived permitted outcomes remain authoritative, including all oracle differences and both repeated-empty capture outcomes.',
            editions='XSD1.1 REC2012-04-05 AppendixG; F&O3.1 REC2017-03-21 5.6/C.1; XML1.0 FifthEdition; Unicode16.0.0',
            errata='Retain human-ratified production82 and scalar w/W interpretations. W3C archived29443/30268 remain NEW; no postREC approved regex correction identified.',
            tokenize='Retain deterministic eager validation, including empty input. Saxon host short circuits are qualified observations, not target passes.',
            diagnostics='Compare standard code/namespace/argument domain and actual native units/bounds. Exact frozen seeds and first-offending-native policies remain exact; source witnesses/whole-argument envelopes are not external processor pinpoints.',
            product='Execute all 5546 recipes, every permitted set, parser source-coordinate tests, and encoding/domain/resource/lifetime/allocation/AOT checks. Preparing this join runs no product XML engine or measurements.'))
    return {INDEX: index, MANIFEST: encoded(manifest)}


if __name__ == '__main__':
    if len(sys.argv) != 1:
        raise ValueError('Fixed no-argument procedure')
    for path, data in outputs().items():
        (ROOT / path).write_bytes(data)
    print('Prepared immutable references to 5546 recipes; no child file changed.')
