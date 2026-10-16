"""Join frozen/native/source-audited artifacts. Cannot change a recipe or permitted result."""
import gzip,json
from common import ROOT,load,artifact,observed,comparison
from run import native_request
OUT=ROOT/'test/corpora/posix'
REPORT=ROOT/'test/corpora/posix/oracle'
lanes=[dict(id='seeds',corpus='test/GregsEx.Tests/Contracts/Posix/ContractVectors.json',count=138),dict(id='expanded',corpus='test/corpora/posix/expanded.json',count=7127)]
cases=sum((load(l['corpus'])['cases'] for l in lanes),[])
natives={f:observed(f)[2] for f in ('glibc','musl')}
source=load('test/corpora/posix/oracle/source-only.json')
def disposition(c,r,f):
 if comparison(c,r):return 'agreement',None
 cls=c['classification'];p=bytes(c['patternUnits'])
 if cls=='Undefined':return 'reviewed-difference','undefined-strict-policy'
 if cls=='Unspecified':return 'reviewed-difference','unspecified-strict-policy'
 if p==rb'\(a\(b\)*\)*\2' and c['inputUnits']==list(b'abab'):
  return 'reviewed-difference','normative-reference-parent-scope'
 if f=='glibc' and p in (b'(a|aa)(a?)',b'((a|aa)(a?))') and r['status']=='Match':
  return 'reviewed-difference','normative-subpattern-priority'
 if f=='glibc' and p in (b'((a)|b)+',b'((a)?b)+') and r['status']=='Match':
  return 'reviewed-difference','normative-report-parent-scope'
 if f=='musl' and p==rb'\(a\)*\1' and c['inputUnits']==list(b'a'):
  return 'reviewed-difference','normative-unmatched-reference'
 if f=='musl' and p in (rb'\(a\)\1',rb'\(ab\)\1') and c['compileFlags']==1 and r['status']=='NoMatch':
  return 'reviewed-difference','normative-reference-case'
 if f=='musl' and (b'[[.' in p or b'[[=' in p) and r['compile']=='InvalidCollatingElement':
  return 'reviewed-difference','provider-high-byte-disposition' if cls=='GregsEx' else 'native-singleton-collation-gap'
 raise ValueError(f'UNREVIEWED DIFFERENCE {f} {c["id"]} {r}')
reasons={
 'undefined-strict-policy':dict(clause='XBD9.1/9.3/9.4; POSIX profile classification register',classification='Undefined',resolution='The ratified named profile rejects this undefined spelling. Native extensions/acceptance/error-detection choices neither weaken the profile nor establish universal POSIX invalidity.'),
 'unspecified-strict-policy':dict(clause='XBD9.3.5; POSIX profile classification register',classification='Unspecified',resolution='The ratified strict disposition selects rejection where Issue7 leaves behavior unspecified. Native acceptance or another symbolic category is recorded, not a product golden result.'),
 'normative-subpattern-priority':dict(clause='XBD9.1 left-to-right longest subpatterns consistent with whole',classification='Defined',resolution='Keep the normative longest preceding subpattern; glibc final registers select the shorter left alternative. Native source set_regs/proceed_next_node recovers one admissible path; it is not the contract priority reference.'),
 'normative-report-parent-scope':dict(clause='XSH regexec nested subexpression in last containing match',classification='Defined',resolution='Keep nested group absent if absent in the last reported parent iteration. glibc retains an earlier nested register; regexec.c update_regs refreshes visited group slots without clearing all absent descendants.'),
 'normative-reference-parent-scope':dict(clause='XBD9.3.6 contained backreference within last containing match',classification='Defined',resolution='NoMatch remains mandatory even when both native families disagree. In abab, the purported final reference needs b from outside its final containing match; an unparticipated reference cannot fabricate the empty acceptance musl reports. Reduced exact pattern/subject and both observations retained.'),
 'normative-unmatched-reference':dict(clause='XBD9.3.6 explicit repeated-a-reference example',classification='Defined',resolution='NoMatch on a is stated by the source example. musl backreference computes eo-so=0 for absent(-1,-1) and strncmp length0 succeeds instead of rejecting absence.'),
 'normative-reference-case':dict(clause='XBD9.2 case-insensitive each-character counterparts;9.3.6',classification='Defined',resolution='REG_ICASE applies to reference equality. musl backreference path regexec.c uses raw strncmp without counterpart comparison; preserve Match and full byte endpoints under the frozen ASCII provider.'),
 'native-singleton-collation-gap':dict(clause='XBD9.3.5 single-character collating symbols/equivalence;C provider',classification='Defined',resolution='Singleton symbols and singleton primary equivalence remain admitted. musl regcomp.c parse_bracket_terms explicitly returns REG_ECOLLATE for both constructs. This is an oracle capability gap, not an approved GregsEx exclusion.'),
 'provider-high-byte-disposition':dict(clause='Ratified GregsEx.Posix.C.Byte-v1 opaque-byte provider',classification='GregsEx',resolution='Raw high-byte singleton collation is the ratified named-provider choice. musl does not implement this bracket syntax. No portable C codeset or unrestricted locale claim is made.')
}
index=[];differences=[]
for n,c in enumerate(cases):
 results={}
 for f in natives:
  if c['id'] in source:results[f]=dict(comparison='source-only',reason=source[c['id']]);continue
  r=natives[f][c['id']];kind,reason=disposition(c,r,f)
  results[f]=dict(comparison=kind,reason=reason)
  if reason:differences.append(dict(id=c['id'],family=f,rule=reason,patternUnits=c['patternUnits'],inputUnits=c['inputUnits'],compileFlags=c['compileFlags'],executionFlags=c['executionFlags'],operation=c['operation'],destinationLength=c['destinationLength'],expected=c['permitted'],observed=r))
 kind='source-only' if c['id'] in source else 'reviewed-difference' if any(r['comparison']=='reviewed-difference' for r in results.values()) else 'agreement'
 index.append(dict(id=c['id'],ordinal=n,diagnosticPolicy='exact',oracleComparison=kind,oracles=results))
(OUT/'index.json').write_text(json.dumps(dict(format='posix-conformance-index-1',cases=index),separators=(',',':'))+'\n')
(REPORT/'disagreements.json').write_text(json.dumps(dict(format='posix-disagreement-audit-1',rules=reasons,cases=differences),indent=2)+'\n')
# Retain exact original native request bytes, reconstructed deterministically without rerunning valid batches.
payload=''.join(native_request(c)[0] or '' for c in cases).encode()
with open(REPORT/'native-requests.txt.gz','wb') as target:
 with gzip.GzipFile(fileobj=target,mode='wb',mtime=0,filename='') as zipped:zipped.write(payload)
paths=[l['corpus'] for l in lanes]+[
 'test/GregsEx.Tests/Contracts/Posix/LocaleTable.json','test/GregsEx.Tests/Contracts/Posix/PosixFixtures.schema.json',
 'test/corpora/posix/index.json','test/corpora/posix/oracle/disagreements.json',
 'test/corpora/posix/oracle/native-requests.txt.gz','test/corpora/posix/oracle/glibc-results.jsonl.gz',
 'test/corpora/posix/oracle/musl-results.jsonl.gz','test/corpora/posix/oracle/source-only.json',
 'test/corpora/posix/oracle/native-identity.txt','test/corpora/posix/oracle/image-identity.json',
 'tools/oracles/posix/native/source-audit.tar.gz','tools/oracles/posix/native/oracle.c','tools/oracles/posix/native/Dockerfile','tools/oracles/posix/oracle-lock.json',
 'test/corpora/posix/oracle/source-build-identity.txt','tools/oracles/posix/generate.py']
identity=load(lanes[0]['corpus'])
manifest=dict(format='posix-conformance-manifest-1',contractVersion=identity['contractVersion'],dataId=identity['dataId'],dataDigest=identity['dataDigest'],fixtureCount=len(cases),lanes=lanes,artifacts=[artifact(p) for p in paths],native=dict(families=['glibc','musl'],requestCount=len(payload.splitlines()),sourceOnlyCount=len(source),platform='Linux/aarch64 on Docker Desktop LinuxKit6.10.14',locale='C',perCaseAlarmSeconds=2,batchTimeoutSeconds=180,addressSpaceBytes=268435456,containerMemoryBytes=536870912,patternUnitsMaximum=4096,subjectUnitsMaximum=8192,nmatchMaximum=64,workCap='Pattern/subject/nmatch/output bounds +2s per-case watchdog; native APIs expose no deterministic work counter. No managed-limit parity or timing claim.'),downstream=dict(productExecution='Required: invoke real integration callback for all recipes, even native-source-only cases. No product engine result claimed.',allocation='BDN MemoryDiagnoser is the allocation authority; no allocation counters in this audit.',nativeAot='Native oracle comparisons do not establish .NET NativeAOT compatibility.'))
(OUT/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(f'Joined {len(cases)} fixtures; {len(differences)} reviewed family differences; no unexplained cases.')
