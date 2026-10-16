"""Metadata completion only; prove all native numeric input headers unchanged."""
import hashlib,json,tempfile
from pathlib import Path
from cases import ROOT,HERE,generate,header
CORPUS=ROOT/'test/corpora/pcre2';OUT=ROOT/'test/corpora/pcre2/oracle'
def main():
 cases=generate();old=json.loads((CORPUS/'observations.json').read_text());by={c['id']:c for c in cases}
 assert len(by)==len(cases)==len(old['cases'])
 with tempfile.TemporaryDirectory(prefix='pcre2-header-proof-') as tmp:
  for width in [8,16]:
   p=Path(tmp)/f'{width}.h';header(cases,width,p)
   prior=HERE/f'.artifacts/build/observer-{width}/cases.h'
   assert p.read_bytes()==prior.read_bytes(),'Numeric native input changed; observe only invalidated inputs before normalization.'
 for c in cases:
  c['pairCapacityRole']='explicitly-supplied-native-match-data' if c['operation']=='Substitute' else 'explicit-native-match-data-and-publication-capacity'
  if c.get('sourceExpectationId')=='PCRE-C048':c['publishedInputErratum']={'source':'test/corpora/pcre2/C048-erratum.md','originalInvocation':'Retained explicit1pair native -54; no successful output asserted','effectiveSuccess':'Change only pairCapacity1 to2; C048-reduction-16-2 observed rc1/outputaa','additionalCounterpart':'C048-reduction.json NULLauto-full independent native outcome'}
  if c['id'].startswith('split-unit'):
   pattern=''.join(chr(v) for v in c['patternUnits'])
   if pattern in [r'\C.',r'\C\X',r'\C[\p{L}]']:
    c['nativeDisposition']='SourceUndefinedMalformedUtfContinuation'
    c['dispositionSource']='doc/pcre2pattern.3:1380–1386'
   else:c['nativeDisposition']='DefinedCodeUnitEndpoint; subsequent supplied UTF start validated independently'
  else:c['nativeDisposition']='ApprovedSourceBoundary' if c['operation'] in ['Projection','ExcludedHost'] else 'DefinedNativeInvocation'
 for result in old['cases']:
  c=by[result['id']];result['nativeDisposition']=c['nativeDisposition']
  if c['operation'] in ['Projection','ExcludedHost']:result['provenance']['kind']='ApprovedSourceBoundaryNoNativeEquivalent'
  for e in result['events']:
   if e['event']=='compile':e['sourceOffset']=None if e['accepted'] else (c.get('sourceMap') or list(range(len(c['patternUnits'])+1)))[e['nativeOffset']]
 (CORPUS/'inputs.json').write_text(json.dumps({'schemaVersion':'pcre2-native-inputs-1','sourceCommit':'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','cases':cases},indent=2)+'\n')
 (CORPUS/'observations.json').write_text(json.dumps(old,indent=2)+'\n')
 print('Metadata completed; both native generated headers byte-identical; no native rerun.')
if __name__=='__main__':main()
