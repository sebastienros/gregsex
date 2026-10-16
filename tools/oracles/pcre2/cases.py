"""Independent, source-derived numeric oracle inputs; never consumes GregsEx output."""
from pathlib import Path
import copy,json,re,struct
ROOT=Path(__file__).resolve().parents[3]; HERE=Path(__file__).resolve().parent
UTF=0x80000; UCP=0x20000

def native(s,width,utf):
 if isinstance(s,list): return s
 if width==8: return list(s.encode('utf-8') if utf else s.encode('latin1'))
 data=s.encode('utf-16le','surrogatepass');return list(struct.unpack('<'+'H'*(len(data)//2),data))
def base(identifier,pattern,subject,width=16,utf=False,**kw):
 c={'id':identifier,'requirements':['PC-021'],'operation':'Match','width':width,
 'patternUnits':native(pattern,width,utf),'subjectUnits':native(subject,width,utf),'replacementUnits':[],
 'compileFlags':UTF if utf else 0,'extraFlags':0,'matchFlags':0,'substituteFlags':0,
 'newline':2,'bsr':1,'optimization':[True]*3,'optimizerDirective':-1,'maxVariableLookbehind':255,
 'startAt':0,'offsetLimit':None,'pairCapacity':8,'destinationCapacity':128,'characterTables':None,
 'workLimit':None,'depthLimit':None,'heapBytes':None,'callbackReturns':[],'substituteCallbackReturns':[],
 'caseCallbackOutputs':[],'caseCallbackMode':-1,'callbackEnabled':True,'substituteCallbackEnabled':False,
 'initialPairs':[],'nameQueryUnits':[110],
 'provenance':{'kind':'SourceDerivedNativeInput','source':'doc/pcre2api.3','line':1,
 'license':'BSD-3-Clause WITH PCRE2-exception','note':'Independent protocol observation; not GregsEx generated.'}}
 c.update(kw);return c

def generate():
 seeds=json.loads((ROOT/'test/GregsEx.Tests/Contracts/Pcre2/ContractVectors.json').read_text())['cases'];result=[]
 for s in seeds:
  c=base(s['id'],[],[],s['width']);c.update(copy.deepcopy(s));c.pop('expected');c['sourceExpectationId']=s['id']
  c['callbackEnabled']=True;c['substituteCallbackEnabled']=bool(c['substituteCallbackReturns']);
  if c['caseCallbackOutputs']:c['caseCallbackMode']=1
  # Character convenience UTF8 retains a complete native-to-source boundary map.
  if c['width']==8 and c['patternKind']=='Chars':
   raw=bytes().join(struct.pack('<H',u) for u in c['patternUnits']);text=raw.decode('utf-16le')
   c['sourcePatternUnits']=c['patternUnits'];c['patternUnits']=native(text,8,bool(c['compileFlags']&UTF));c['sourceMap']=[0]
   srcpos=0
   for ch in text:
    count=len(ch.encode('utf-8'));c['sourceMap']+= [srcpos]*(count-1)+[srcpos+(2 if ord(ch)>65535 else 1)];srcpos+=2 if ord(ch)>65535 else 1
  result.append(c)
  # Source-valid width counterpart, preserving native raw values where possible.
  if c['operation'] not in ['ExcludedHost','Projection'] and all(u<=127 for k in ['patternUnits','subjectUnits','replacementUnits'] for u in c[k]):
   clone=copy.deepcopy(c);clone['width']=8 if c['width']==16 else 16;clone['id']=c['id']+'-cross';result.append(clone)
 # Every public native compile bit, including shared bits, observed in each domain.
 defs=json.loads((ROOT/'tools/oracles/pcre2/source/source-observations.json').read_text())['definitions']
 compile=[d for d in defs if d['category'] in ['compile','common_compile_match']]
 extra=[d for d in defs if d['category']=='compile_extra']
 cp={
 'ALLOW_EMPTY_CLASS':('[]','a'),'ALT_BSUX':(r'\u0061','a'),'AUTO_CALLOUT':('a(b|c)','ab'),
 'CASELESS':('a','A'),'DOLLAR_ENDONLY':('a$','a\n'),'DOTALL':('.','\n'),'DUPNAMES':('(?<n>a)|(?<n>b)','b'),
 'EXTENDED':('a b','ab'),'FIRSTLINE':('b','a\nb'),'MATCH_UNSET_BACKREF':(r'(a)?\1b','b'),
 'MULTILINE':('^b','a\nb'),'NEVER_UCP':('(*UCP)a','a'),'NEVER_UTF':('(*UTF)a','a'),
 'NO_AUTO_CAPTURE':('(a)','a'),'NO_AUTO_POSSESS':('a+b','aaab'),'NO_DOTSTAR_ANCHOR':('.*a','ba'),
 'NO_START_OPTIMIZE':('(*COMMIT)ABC','DEFABC'),'UCP':(r'\w','a'),'UNGREEDY':('a+','aaa'),
 'UTF':('.','a'),'NEVER_BACKSLASH_C':(r'\C','a'),'ALT_CIRCUMFLEX':('^','a\n'),
 'ALT_VERBNAMES':(r'(*MARK:\x{61})a','a'),'USE_OFFSET_LIMIT':('a','ba'),'EXTENDED_MORE':('[ a ]','a'),
 'LITERAL':('(a)','(a)'),'MATCH_INVALID_UTF':('a','a'),'ALT_EXTENDED_CLASS':('[a-z--[aeiou]]','b'),
 'ANCHORED':('a','ba'),'NO_UTF_CHECK':('a','a'),'ENDANCHORED':('a','ab')}
 ep={
 'ALLOW_SURROGATE_ESCAPES':(r'\x{d800}','a'),'BAD_ESCAPE_IS_LITERAL':(r'\q','q'),
 'MATCH_WORD':('a','ba'),'MATCH_LINE':('a','ba'),'ESCAPED_CR_IS_LF':(r'\r','\n'),
 'ALT_BSUX':(r'\u{61}','a'),'ALLOW_LOOKAROUND_BSK':(r'(?=ab\K)','ab'),
 'CASELESS_RESTRICT':('k','k'),'ASCII_BSD':(r'\d','1'),'ASCII_BSS':(r'\s',' '),
 'ASCII_BSW':(r'\w','a'),'ASCII_POSIX':('[[:alpha:]]','a'),'ASCII_DIGIT':('[[:digit:]]','1'),
 'PYTHON_OCTAL':(r'\111','I'),'NO_BS0':(r'\0',[0]),'NEVER_CALLOUT':('a(?C1)','a'),
 'TURKISH_CASING':('I','i')}
 for width in [8,16]:
  for utf in [False,True]:
   domain=f'{width}-'+('utf' if utf else 'raw')
   for d in compile:
    name=d['name'][6:];p,s=cp[name];value=int(re.search(r'0x[0-9a-fA-F]+',d['value'])[0],16)
    for enabled in [True,False]:
     c=base(f'compile-{name}-{domain}-{enabled}',p,s,width,utf,compileFlags=(UTF if utf else 0)|(value if enabled else 0))
     c['optionName']=d['name'];c['provenance']['source']='src/pcre2.h.generic';c['provenance']['line']=d['source_line'];result.append(c)
   for d in extra:
    name=d['name'][12:];p,s=ep[name];value=int(re.search(r'0x[0-9a-fA-F]+',d['value'])[0],16)
    for enabled in [True,False]:
     c=base(f'extra-{name}-{domain}-{enabled}',p,s,width,utf,compileFlags=(UTF if utf else 0)|UCP|8,extraFlags=value if enabled else 0)
     c['optionName']=d['name'];c['provenance']['source']='src/pcre2.h.generic';c['provenance']['line']=d['source_line'];result.append(c)
   for flag in [1,2,4,8,16,32,0x2000,0x4000,0x40000,0x80000000,0x40000000,0x20000000]:
    result.append(base(f'match-{flag}-{domain}','(a)?a*','a',width,utf,matchFlags=flag,requirements=['PC-021','PC-022','PH-002','PH-008']))
   for opt in [0,1,64,65,66,67,68,69]:
    result.append(base(f'optimizer-{opt}-{domain}','(*COMMIT)ABC','DEFABC',width,utf,optimizerDirective=opt,requirements=['PC-024']))
   for nl in range(1,7):
    for bsr in [1,2]:result.append(base(f'newline-{nl}-bsr-{bsr}-{domain}',r'(^|\R)a$','\ra\n',width,utf,newline=nl,bsr=bsr,requirements=['PC-003','PC-019']))
   # Full callback views/flags/rollback/negative user-defined return are independent of short vectors.
   for p,s in [('(?<n>a)(b)?(?C0)c|a(?C"tag")d','ad'),('(?C"")a','a'),('(?<n>a)+(?C1)','aa'),('a(?C1)b|ab','ab'),('ab','ab')]:
    for returns in [[0],[1,0],[-777]]:
     result.append(base(f'call-{len(result)}-{domain}',p,s,width,utf,compileFlags=(UTF if utf else 0)|4,pairCapacity=1,callbackReturns=returns,requirements=['PC-020','PH-007']))
   for flag in [0,0x100,0x200,0x400,0x800,0x1000,0x8000,0x10000,0x20000]:
    for cap in [0,2,128]:
     result.append(base(f'sub-{flag}-{cap}-{domain}','(?<n>a)(b)?','aa',width,utf,operation='Substitute',replacementUnits=native('${n}-$2',width,utf),substituteFlags=flag,destinationCapacity=cap,requirements=['PH-006']))
   for ret in [[0],[1],[-1]]:
    for cap in [2,128]:
     result.append(base(f'subcallback-{len(result)}-{domain}','(a)','aaa',width,utf,operation='Substitute',replacementUnits=native('LONG',width,utf),substituteFlags=0x1100,destinationCapacity=cap,substituteCallbackReturns=ret,substituteCallbackEnabled=True,requirements=['PH-006']))
   for repl in [r'\L$0\E',r'\U$0\E',r'\u\L$0\E']:
    for mode in [0,1,2]:
     result.append(base(f'casecallback-{len(result)}-{domain}','(a)','aa',width,utf,operation='Substitute',replacementUnits=native(repl,width,utf),substituteFlags=0x300,caseCallbackMode=mode,caseCallbackOutputs=[native('XYZ',width,utf)] if mode==1 else [],requirements=['PH-006']))
   # Independent endpoint/unset/short output/initial sentinel/name/iteration boundaries.
   for pattern,subject in [('(?=ab\\K)','ab'),('(a)(b)?()','a'),('(*MARK:x)a','b'),('(*SKIP)(*FAIL)|a','ba'),(r'(?(R)a|a(?R)?)','aa')]:
    result.append(base(f'endpoints-{len(result)}-{domain}',pattern,subject,width,utf,extraFlags=64,pairCapacity=8,requirements=['PC-015','PC-017','PC-018','PH-002','PH-004']))
   result.append(base(f'iterate-{domain}','(?:|a)','aa',width,utf,operation='Iterate',requirements=['PH-005']))
   result.append(base(f'duplicate-name-{domain}','(?J)(?<n>a)|(?<n>b)','b',width,utf,requirements=['PC-011','PH-004']))
   # Fully specified immutable imported tables, never host locale forwarding.
   table_source=(HERE/'.artifacts/source/src/pcre2_chartables.c.dist').read_text()
   table_body=re.sub(r'/\*.*?\*/','',table_source.split('= {',1)[1].split('};',1)[0],flags=re.S)
   table=[int(v,0) for v in re.findall(r'0x[0-9a-fA-F]+|[0-9]+',table_body)]
   assert len(table)==1088
   if table:
    for unit in [0x40,0x80,0xE9]:
     custom=table.copy();custom[832+unit]|=0x10
     p=[40,63,60,unit,62,97,41] # (?<native-unit>a)
     result.append(base(f'tablename-{unit}-{domain}',p,'a',width,utf,characterTables=custom,requirements=['PC-007','PC-011','PH-004'],nameQueryUnits=[unit]))

 # Minimized independent counterpart for frozen C048: one pair cannot supply $1.
 for width in [8,16]:
  for cap in [1,2]:
   result.append(base(f'C048-reduction-{width}-{cap}','(a)','a',width,False,operation='Substitute',pairCapacity=cap,replacementUnits=[36,49,36,49],requirements=['PH-006']))
 # Native Unicode output-case behavior, name aliases and literal NUL/surrogates.
 for width,utf in [(8,False),(8,True),(16,False),(16,True)]:
  domain=f'{width}-{utf}'
  for text in (['é'] if width==8 and not utf else ['İ','ı','ǅ','K','ß','Σ','😀']):
   for repl in [r'\L$0\E',r'\U$0\E',r'\u$0']:
    result.append(base(f'outputcase-{len(result)}-{domain}','(.)',text,width,utf,operation='Substitute',replacementUnits=native(repl,width,utf),substituteFlags=0x200,requirements=['PC-007','PH-006']))
  if utf:
   for alias in [r'\p{sc=Greek}',r'\p{Script_Extensions=Greek}',r'\p{LC}',r'\p{IsGreek}']:
    result.append(base(f'alias-{len(result)}-{domain}',alias,'α',width,utf,requirements=['PC-006']))
   result.append(base(f'unicode-name-{domain}','(?<α>a)','a',width,utf,nameQueryUnits=native('α',width,utf),requirements=['PC-011','PH-004']))
  for flags in [0,4]:
   result.append(base(f'NUL-{flags}-{domain}',[0],[0],width,utf,compileFlags=(UTF if utf else 0)|flags,requirements=['PC-001','PH-001']))
 result.append(base('raw16-surrogate-name','(?<n>.)',[0xD800],16,False,requirements=['PC-001','PH-001']))
 # UTF window/check-valid-precondition/raw surrogate/NUL and version10.46 cases.
 for width in [8,16]:
  invalid=[0xFF,97] if width==8 else [0xD800,97]
  for p,start in [('a',1),(r'\ba',1),(r'(?<=.)a',1),('.',0)]:
   for flags in [UTF,0x4000000]:result.append(base(f'utfwindow-{len(result)}-{width}',p,invalid,width,True,startAt=start,compileFlags=flags,requirements=['PC-023','PH-003']))
  for valid in [native('😀a',width,True),[0,97]]:
   result.append(base(f'nocheck-valid-{len(result)}-{width}',r'\C',valid,width,True,matchFlags=0x40000000,requirements=['PC-023']))
 for width in [8,16]:
  for pattern in [r'\C.',r'\C\X',r'\C[\p{L}]',r'\C\C',r'\C']:
   for operation in ['Match','Iterate']:
    result.append(base(f'split-unit-{len(result)}-{width}',pattern,'😀a',width,True,operation=operation,requirements=['PC-001','PC-017','PH-003','PH-005']))
  for start in range(1,len(native('😀a',width,True))):
   result.append(base(f'interior-supplied-{width}-{start}','.',native('😀a',width,True),width,True,startAt=start,requirements=['PC-023','PH-003']))
  result.append(base(f'name-order-{width}','(?<𐐀>a)|(?<Ａ>b)','b',width,True,nameQueryUnits=native('𐐀',width,True),requirements=['PC-011','PH-004']))
 return result

def header(cases,width,path):
 lines=[];rows=[]
 def arr(name,kind,values):
  if not values:return 'NULL'
  lines.append(f'static const {kind} {name}[]={{'+','.join(str(x) for x in values)+'};');return name
 for i,c in enumerate(x for x in cases if x['width']==width):
  pat=arr(f'p{i}','PCRE2_UCHAR',c['patternUnits']+[0]);sub=arr(f's{i}','PCRE2_UCHAR',c['subjectUnits']+[0]);rep=arr(f'r{i}','PCRE2_UCHAR',c['replacementUnits']+[0]);name=arr(f'n{i}','PCRE2_UCHAR',c['nameQueryUnits']+[0]);tables=arr(f't{i}','uint8_t',c['characterTables'] or [])
  cr=arr(f'cr{i}','int',c['callbackReturns']);sr=arr(f'sr{i}','int',c['substituteCallbackReturns']);co=arr(f'co{i}','PCRE2_UCHAR',(c['caseCallbackOutputs'] or [[]])[0]);initial=arr(f'in{i}','size_t',[v for p in c['initialPairs'] for v in ([p['start'],p['end']] if p['participated'] else ['PCRE2_UNSET']*2)])
  op={'Match':0,'Compile':1,'Iterate':2,'Substitute':3,'Projection':5,'ExcludedHost':5}[c['operation']]
  values=[json.dumps(c['id']),op,pat,sub,rep,name,len(c['patternUnits']),len(c['subjectUnits']),len(c['replacementUnits'])]+[c[x] for x in ['compileFlags','extraFlags','matchFlags','substituteFlags','newline','bsr','maxVariableLookbehind','pairCapacity','destinationCapacity','startAt']]
  values+=['{'+','.join(str(int(v)) for v in c['optimization'])+'}',c['optimizerDirective']]+[c[x] if c[x] is not None else -1 for x in ['workLimit','depthLimit']]+[c['heapBytes']//1024 if c['heapBytes'] is not None else -1,c['offsetLimit'] if c['offsetLimit'] is not None else -1,tables,cr,sr,len(c['callbackReturns']) if c['callbackEnabled'] else 'SIZE_MAX',len(c['substituteCallbackReturns']) if c['substituteCallbackEnabled'] else 'SIZE_MAX',co,len((c['caseCallbackOutputs'] or [[]])[0]),c['caseCallbackMode'],initial,len(c['initialPairs'])*2]
  rows.append('{'+','.join(str(x) for x in values)+'}')
 lines+=['static const Case cases[]={',',\n'.join(rows),'};'];path.write_text('\n'.join(lines)+'\n')
if __name__=='__main__':
 cases=generate();(ROOT/'test/corpora/pcre2/inputs.json').write_text(json.dumps({'schemaVersion':'pcre2-native-inputs-1','sourceCommit':'b2bd4254b379b9d7dc9a3dda060a7e27009ccdff','cases':cases},indent=2)+'\n');print(len(cases),'source-native cases')
