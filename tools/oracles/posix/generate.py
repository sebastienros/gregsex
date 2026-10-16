"""Fixed, source-authored corpus preparation. Never executes or imports a matcher."""
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
SEED=ROOT/'test/GregsEx.Tests/Contracts/Posix/ContractVectors.json'
SOURCE='https://pubs.opengroup.org/onlinepubs/9699919799/basedefs/V1_chap09.html'
XSH='https://pubs.opengroup.org/onlinepubs/9699919799/functions/regcomp.html'
identity=json.loads(SEED.read_text())
cases=[]
def capture(a,b):return dict(participated=a>=0,index=max(0,a),length=0 if a<0 else b-a)
def expected(status='Match',registers=None,native=None,unchanged=False,count=None):
 r=registers or [(0,1)]
 return dict(status=status,boolean=None,groupCount=count,matches=[] if status!='Match' or native is not None else [dict(range=dict(index=r[0][0],length=r[0][1]-r[0][0]),captures=[capture(a,b) for a,b in r])],nativeMatches=[] if native is None else [dict(start=a,end=b) for a,b in native],destinationUnchanged=unchanged,error=None,requiredBytes=None,errorBytes=None)
def add(key,p,s,regs=None,status='Match',caps=(2,12),bre=False,flags=0,eflags=0,classification='Defined',clause='9.1;9.3;9.4',op='FindCaptures',dest=None,start=0,native=None,unchanged=False):
 p=list(p) if isinstance(p,(bytes,list)) else list(map(ord,p));s=list(s) if isinstance(s,(bytes,list)) else list(map(ord,s))
 regs=regs or [(0,1)]
 cases.append(dict(id='POSIX-E-'+key,profileId='PosixBre2018-CByte' if bre else 'PosixEre2018-CByte',locale='C',capabilities=[f'PX-{n:03}' for n in caps],provenance=dict(kind='Specification' if classification=='Defined' else 'GregsExDisposition',uri=XSH if 'XSH' in clause else SOURCE,clause=clause,license='Original GregsEx fixture (MIT); normative text not copied.',oracleId=None),classification=classification,patternKind='Bytes',patternUnits=p,inputKind='Bytes',inputUnits=s,compileFlags=flags,executionFlags=eflags,operation=op,startAt=start,destinationLength=dest if dest is not None else len(regs) if op=='FindCaptures' else None,errorCode=None,limits=None,permitted=[expected(status,regs,native,unchanged)]))
def mask(b):
 upper=65<=b<=90;lower=97<=b<=122;digit=48<=b<=57;alpha=upper or lower;graph=33<=b<=126
 return [alpha or digit,alpha,b in (9,32),b<32 or b==127,digit,graph,lower,32<=b<=126,graph and not(alpha or digit),b in (9,10,11,12,13,32),upper,digit or 65<=b<=70 or 97<=b<=102]
names=['alnum','alpha','blank','cntrl','digit','graph','lower','print','punct','space','upper','xdigit']
# Exhaustively authored ASCII classes in both grammars, and all opaque high bytes.
for bre in (False,True):
 for k,name in enumerate(names):
  for b in range(1,256):
   hit=mask(b)[k]
   add(f'class-{int(bre)}-{name}-{b:03}',f'[[:{name}:]]',bytes([b]),status='Match' if hit else 'NoMatch',bre=bre,caps=(3,4,18),classification='Defined' if b<128 else 'GregsEx',clause='7.3.1;9.3.5; GregsEx opaque high-byte disposition' if b>=128 else '7.3.1;9.3.5')
# ICASE membership expands the positive class before complement.
for name in ('lower','upper'):
 for negative in (False,True):
  for b in range(1,128):
   hit=65<=b<=90 or 97<=b<=122
   if negative:hit=not hit
   add(f'icase-{name}-{int(negative)}-{b:03}',f'[{"^" if negative else ""}[:{name}:]]',bytes([b]),status='Match' if hit else 'NoMatch',flags=1,caps=(4,6,15),clause='9.2;9.3.5')
# Native literals preserve every byte including punctuation (escaped by grammar).
for b in range(1,256):
 p=bytes([92,b]) if b in b'.[^$*+?(){|\\' else bytes([b])
 add(f'literal-{b:03}',p,bytes([b]),caps=(2,18),classification='Defined' if b<128 else 'GregsEx',clause='9.4.2;9.4.3' if b<128 else 'GregsEx opaque high-byte literal disposition')
# Entire endpoint arrays are independently reasoned from the source, never sampled.
witnesses=[
 ('choice','(aa|a)(a?)','aa',[(0,2),(0,2),(2,2)]),
 ('choice-reversed','(a|aa)(a?)','aa',[(0,2),(0,2),(2,2)]),
 ('sibling','(a*)(a*)','aaa',[(0,3),(0,3),(3,3)]),
 ('outer','((a|aa)(a?))','aa',[(0,2),(0,2),(0,2),(2,2)]),
 ('last','(ab)+','abab',[(0,4),(2,4)]),
 ('scoped','((a)|b)+','ab',[(0,2),(1,2),(-1,-1)]),
 ('scoped-option','((a)?b)+','abb',[(0,3),(2,3),(-1,-1)]),
 ('absent','(a)?b','b',[(0,1),(-1,-1)]),
 ('empty','(a*)b','b',[(0,1),(0,0)]),
 ('nullable','(a*)*','aa',[(0,2),(0,2)]),
 ('nullable-miss','(a*)*','b',[(0,0),(0,0)]),
 ('required-null','(a*){2}','',[(0,0),(0,0)]),
 ('interval','(ab){2,3}','ababab',[(0,6),(4,6)]),
 ('many','(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)','abcdefghij',[(0,10)]+[(n,n+1) for n in range(10)]),
]
for key,p,s,r in witnesses:
 add('capture-'+key,p,s,r,caps=(8,9,12,13,14),clause='9.1;9.4.6; XSH regexec final containing participation')
 for n in sorted(set((0,1,2,len(r),len(r)+2))):
  out=(r+[(-1,-1)]*n)[:n]
  add(f'nmatch-{key}-{n}',p,s,r,caps=(13,14,17),clause='9.1; XSH regexec nmatch',op='RegExec',dest=n,native=[] if n==0 else out,unchanged=n==0)
  add(f'nosub-{key}-{n}',p,s,r,caps=(13,17),clause='XSH REG_NOSUB ignored output',op='RegExec',dest=n,flags=4,native=[],unchanged=True)
# Last scoped BRE references and partial output may not discard private registers.
for key,p,s,r,status in [
 ('closed',r'\(ab\)\1','abab',[(0,4),(0,2)],'Match'),
 ('fold',r'\(ab\)\1','abAB',[(0,4),(0,2)],'Match'),
 ('empty',r'^\(a*\)\1$','',[(0,0),(0,0)],'Match'),
 ('parent',r'\(a\(b\)*\)*\2','abab',[(0,0)],'NoMatch'),
 ('last',r'^\(ab*\)*\1$','ababbabb',[(0,8),(2,5)],'Match'),
]:
 add('bre-'+key,p,s,r,status=status,bre=True,flags=1 if key=='fold' else 0,caps=(7,11,14,15),clause='9.3.6; XSH final containing participation')
 if status=='Match':
  for n in (0,1,2,4):
   add(f'bre-nmatch-{key}-{n}',p,s,r,bre=True,flags=1 if key=='fold' else 0,caps=(7,11,17),clause='9.3.6; XSH nmatch',op='RegExec',dest=n,native=[] if n==0 else (r+[(-1,-1)]*n)[:n],unchanged=n==0)
# Exhaustive execution-flag interactions on original and internal endpoints.
for newline in (False,True):
 for ef in range(4):
  for key,p,s,hit,r in [
   ('original','^a$','a',ef==0,[(0,1)]),
   ('internal','^a$','x\na\nx',newline,[(2,3)]),
   ('trailing','a$','a\n',newline,[(0,1)]),
   ('cr','^a$','a\r',False,[(0,1)]),
  ]:
   add(f'anchor-{int(newline)}-{ef}-{key}',p,s,r,status='Match' if hit else 'NoMatch',flags=2 if newline else 0,eflags=ef,caps=(10,16),clause='9.4.9; XSH REG_NEWLINE/NOTBOL/NOTEOL',op='RegExec',dest=1,native=r if hit else [(-1,-1)],classification='Defined' if hit else 'GregsEx')
# Bracket singleton symbols, ordinal ranges, newline-positive classes and ref lengths.
for b in (1,9,10,32,45,65,93,97,127,128,255):
 for marker in ('.','='):
  add(f'collating-{ord(marker)}-{b:03}',bytes([91,91,ord(marker),b,ord(marker),93,93]),bytes([b]),caps=(5,18),classification='Defined' if b<128 else 'GregsEx',clause='9.3.5; singleton provider collation')
for p in ('[a-c]','[[.a.]-[.c.]]'):
 for b in (96,97,98,99,100):add(f'range-{len(p)}-{b}',p,bytes([b]),status='Match' if 97<=b<=99 else 'NoMatch',caps=(5,6),clause='9.3.5 C ordinal range')
for b in (9,10,13):
 add(f'newline-positive-{b}','[[:space:]]',bytes([b]),flags=2,caps=(4,16),clause='XSH REG_NEWLINE positive list')
for count in (0,1,2,254,255):
 for bre in (False,True):
  p='a'+(r'\{'+str(count)+r'\}' if bre else '{'+str(count)+'}')
  add(f'count-{int(bre)}-{count}',p,'a'*count,[(0,count)],bre=bre,caps=(7,8,9),clause='9.3.6;9.4.6 RE_DUP_MAX255')
# A root unmatched closing parenthesis is ordinary, including following alternation.
for key,p,s,hit,r in [
 ('right', 'a|)', ')', True, [(0,1)]),
 ('left', 'a|)', 'a', True, [(0,1)]),
 ('miss', 'a|)', 'z', False, [(0,1)]),
 ('pair', 'a|))', '))', True, [(0,2)]),
]:
 add('root-close-branch-'+key,p,s,r,status='Match' if hit else 'NoMatch',caps=(2,8,12),clause='9.4.3 unmatched closing parenthesis is ordinary;9.4.7 alternation')
# Approved anonymous lexical priority resolves an alternative tie not formally
# specified by an anonymous AST model in Issue7. Label the exact contract choice.
add('anonymous-vs-capture','a|(a)','a',[(0,1),(-1,-1)],caps=(8,12,13,14),classification='GregsEx',clause='9.1;9.4.7; ratified PX-013 anonymous lexical subpattern priority; no universal portable tie claim')
add('anonymous-vs-inner-capture','(a|(a))','a',[(0,1),(0,1),(-1,-1)],caps=(8,12,13,14),classification='GregsEx',clause='9.1;9.4.7; ratified PX-013 outer before anonymous lexical subpatterns; no universal portable tie claim')
assert len({c['id'] for c in cases})==len(cases)
out={k:identity[k] for k in ('contractVersion','dataId','dataDigest')};out['cases']=cases
path=ROOT/'test/corpora/posix/expanded.json'
path.write_text(json.dumps(out,separators=(',',':'))+'\n')
print(f'{len(cases)} source-authored expanded fixtures; seeds are read-only.')
