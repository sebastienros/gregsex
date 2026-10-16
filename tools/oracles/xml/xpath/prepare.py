"""Fixed, deterministic corpus preparation; no command-line argument parser."""
import copy, hashlib, json, pathlib, struct, xml.etree.ElementTree as E
ROOT=pathlib.Path(__file__).resolve().parents[4]
OUT=ROOT/'test/corpora/xml/xpath'
URI='https://www.w3.org/TR/2017/REC-xpath-functions-31-20170321/'
ERR='http://www.w3.org/2005/xqt-errors'
def units(s): return list(struct.unpack('<'+'H'*(len(s.encode('utf-16-le','surrogatepass'))//2),s.encode('utf-16-le','surrogatepass')))
def text(us): return struct.pack('<'+'H'*len(us),*us).decode('utf-16-le','surrogatepass')
def expected(status='Match',boolean=None,texts=None,error=None,tree=None,matches=None):
 return dict(status=status,boolean=boolean,matches=matches or [],texts=None if texts is None else [units(x) for x in texts],tree=tree or [],error=error)
def diagnostic(code,where='Pattern',offset=0,length=1,unit='Utf16CodeUnits'):
 return dict(code=code,location=where,offset=offset,length=length,offsetUnit=unit,namespace=ERR if code.startswith('FORX') else None)
cases=[]; annotations={}
def add(cap,role,pattern,input='',flags='',operation='Matches',boolean=True,texts=None,code=None,replacement=None,**extra):
 id=f'XP-{len(cases)+1:04d}'
 error_location=extra.pop('error_location','Pattern');error_offset=extra.pop('error_offset',0);error_length=extra.pop('error_length',1)
 e=expected('FunctionError' if code else ('Match' if boolean is not False else 'NoMatch'),boolean if operation=='Matches' and not code else None,texts,
 diagnostic(code,error_location,error_offset,error_length) if code else None)
 c=dict(id=id,profileId='XPath31',capabilities=[f'XP-{cap:03d}'],provenance=dict(kind='Specification',uri=URI,clause=f'5.6 (XP-{cap:03d})',license='https://www.w3.org/copyright/document-license/',oracleId=None),patternKind='Utf16',patternUnits=units(pattern),inputKind='Utf16',inputUnits=units(input),absentInput=False,flags=flags,operation=operation,startAt=0,destinationLength=None,replacementUnits=None if replacement is None else units(replacement),limits=None,permitted=[e])
 c.update(extra);cases.append(c);annotations[id]=dict(role=role,oracle='Saxon-HE-12.5',comparison='function-value')
 return c
# Explicit positive, negative and boundary witnesses for every frozen XP inventory row.
add(1,'positive','a','ba');add(1,'negative','a','b',boolean=False);add(1,'boundary','','',boolean=True)
add(2,'positive','a$','a\n','m');add(2,'negative','a$','a\n',boolean=False);add(2,'boundary','^$','\n','m',boolean=True)
add(3,'positive','a+?','aaa',operation='Replace',replacement='x',texts=['xxx']);add(3,'negative','a+?','bbb',boolean=False);add(3,'boundary','a{1,2}?','aa',operation='Replace',replacement='x',texts=['xx'])
add(4,'positive','((a)(b))','ab',operation='Replace',replacement='$3$2$1$0',texts=['baabab']);add(4,'negative','(a)?b','b',operation='Replace',replacement='$1',texts=['']);add(4,'boundary','(a*)b','b',operation='Replace',replacement='$1X',texts=['X'])
add(5,'positive',r'(ab)\1','abab');add(5,'negative',r'\1(a)','a',code='FORX0002');add(5,'boundary',r'(a|(b))\2','a')
add(6,'positive',r'\p{IsBasicLatin}','A');add(6,'negative',r'\p{IsNoSuchBlock}','A',code='FORX0002');add(6,'boundary',r'\P{IsNoSuchBlock}','A',code='FORX0002')
add(7,'positive','^.$','\n','s');add(7,'negative','^.$','\r',boolean=False);add(7,'boundary','^.$','\r\n','s',boolean=False)
add(8,'positive','A','a','ii');add(8,'negative','a','a','z',code='FORX0001',error_location='Flags');add(8,'boundary','a','a','qmixsqq')
add(9,'positive','k','\u212a','i');add(9,'negative',r'\p{Lu}','a','i',boolean=False);add(9,'boundary','[^a]','A','i',boolean=False)
add(10,'positive','a \n b','ab','x');add(10,'negative','a\u00a0b','ab','x',boolean=False);add(10,'boundary','[ ]',' ','x')
add(11,'positive','a.b','a.b','q');add(11,'negative','a.b','axb','q',boolean=False);add(11,'boundary','x','x','q',operation='Replace',replacement='$\\',texts=['$\\'])
add(12,'positive','a','ba');add(12,'negative','a','',boolean=False,absentInput=True);add(12,'boundary','','',absentInput=True)
add(13,'positive','(z)','z',operation='Replace',replacement='$12',texts=['z2']);add(13,'negative','x','x',operation='Replace',replacement='$',code='FORX0004',error_location='Replacement');add(13,'boundary','x','xx',operation='Replace',replacement=r'\$\\$0$9',texts=['$\\x$\\x'])
add(14,'positive',',',',a,',operation='Tokenize',texts=['','a','']);add(14,'negative',',','',operation='Tokenize',texts=[]);add(14,'boundary','', ' \ta \n b\r ',operation='TokenizeOne',texts=['a','b'])
add(15,'positive','a+','aa',operation='Tokenize',texts=['','']);add(15,'negative','a*','bbb',operation='Replace',replacement='x',code='FORX0003');add(15,'boundary','a*','',operation='Tokenize',code='FORX0003')
# Analyze trees use contract enums numeric (Match=0 NonMatch=1 Group=2 Text=3).
def node(kind,parent,group,index,length): return dict(kind=kind,parentIndex=parent,groupNumber=group,range=dict(index=index,length=length))
c=add(16,'positive','((a)(b))','xabz',operation='AnalyzeString');c['permitted'][0]['tree']=[node(1,-1,0,0,1),node(0,-1,0,1,2),node(2,1,1,1,2),node(2,2,2,1,1),node(3,3,0,1,1),node(2,2,3,2,1),node(3,5,0,2,1),node(1,-1,0,3,1)]
c=add(16,'negative','a','z',operation='AnalyzeString');c['permitted'][0]['tree']=[node(1,-1,0,0,1)]
add(16,'boundary','a','',operation='AnalyzeString')
add(17,'positive','😀','x😀',inputKind='Utf8',inputUnits=list('x😀'.encode()),operation='Find',permitted=[expected(matches=[dict(range=dict(index=1,length=4),captures=[dict(participated=True,index=1,length=4)])])]);annotations[cases[-1]['id']]['comparison']='native-find'
c=add(17,'negative','a','a\x00',boolean=None,permitted=[expected('InvalidInput',error=diagnostic('InvalidInputDomain','Input',1,1))]);annotations[c['id']].update(oracle=None,comparison='source-domain',reason='Saxon XML strings cannot represent NUL; GregsEx full-subject validation policy')
c=add(17,'boundary','a','\ud800',boolean=None,permitted=[expected('InvalidInput',error=diagnostic('InvalidEncoding','Input',0,1))]);annotations[c['id']].update(oracle=None,comparison='source-domain',reason='Strict scalar UTF16 policy rejects lone surrogate; no lossy XML serialization')
# GregsEx domain/representation/native-boundary policy cases are source-derived.
for inp,kind,code,offset in [([0xc0,0xaf],'Utf8','InvalidEncoding',0),([0xf0,0x9f,0x98],'Utf8','InvalidEncoding',0),([97,0],'Utf8','InvalidInputDomain',1),([0xd83d],'Utf16','InvalidEncoding',0)]:
 c=add(17,'negative','a',inputKind=kind,inputUnits=inp,permitted=[expected('InvalidInput',error=diagnostic(code,'Input',offset,1,'Bytes' if kind=='Utf8' else 'Utf16CodeUnits'))])
 annotations[c['id']].update(oracle=None,comparison='source-domain',reason='Strict XML1.0 full-subject encoding/domain precedence; native failing unit preserved')
for inp,kind in [('😀','Utf16'),('😀','Utf8'),('\U0010ffff','Utf16')]:
 c=add(17,'positive','.',inp,inputKind=kind,inputUnits=list(inp.encode()) if kind=='Utf8' else units(inp))
for op in ['Replace','Tokenize','AnalyzeString']:
 limits=dict(maxWork=None,maxCompileWork=None,frameCapacity=None,maxResults=1,maxOutputUnits=None,maxHostMemoryBytes=None,cancelled=False)
 c=add(17,'boundary','a','aa',operation=op,replacement='x' if op=='Replace' else None,limits=limits,permitted=[expected('HostLimitExceeded',error=diagnostic('ResultLimitExceeded','Host',0,0))])
 annotations[c['id']].update(oracle=None,comparison='source-policy',reason='Ratified owned-output ceiling; external processor has no GregsEx host budget')
# Additional cross-product witnesses: inherited classes and grammar, boundaries, all flags/functions/errors.
for p,yes,no in [(r'\i\c*',':a','1'),(r'\d','١','a'),(r'\s','\t','\u00a0'),(r'\w','α','_'),(r'\W','_','α'),(r'[a-z-[aeiou]]+','bc','e'),(r'\p{Lu}','A','a'),(r'\p{IsGreek}','α','A'),(r'\p{IsPrivateUse}','\ue000','a'),(r'\p{Cn}','\u0378','a'),(r'[^a]','😀','a')]:
 for value,b in [(yes,True),(no,False)]:add(1,'positive' if b else 'negative','^'+p+'$',value,boolean=b)
for p in ['a?','a*','a+','a{2}','a{1,}','a{1,3}','a??','a*?','a+?','a{1,3}?']:
 add(3,'positive',p,'aaa');add(3,'negative','^'+p+'$','b',boolean=False)
for p in [r'(?=a)',r'(?<x>a)',r'\x41',r'\u0041',r'[z-a]',r'a{3,2}',r'[\1]',r'(a\1)',r'\p{ll}',r'\p{Is}',r'a++',r'[\]']:
 add(5,'negative',p,'',code='FORX0002')
for flags in ['s','m','i','x','q','smixq','qq','ii','ssmmxx']:
 add(8,'positive','a','a',flags)
for flags in ['I','g','u','v',' ','a']:
 add(8,'negative','a','a',flags,code='FORX0001',error_location='Flags')
for p,inp,fl,b in [('^','a\n','m',True),('^$','a\n','m',False),('$','a\n','m',True),('^b','a\rb','m',False),('b$','b\r','m',False),('a$','a\r','',False),('^.$','😀','',True),('^.$','\n','',False),('^.$','\r','',False),('a b','a b','qmixs',True),('A','a','qi',True)]:add(2,'boundary',p,inp,fl,boolean=b)
for p,inp,repl,out in [('(a)(b)','ab','$0-$1-$2',['ab-a-b']),('(a)?b','b','$1$2',['']),('(a)','a','$123',['a23']),('(a)','a','$99',['9']),('x','xx',r'\$',['$$']),('x','xx',r'\\',['\\\\'])]:add(13,'boundary',p,inp,operation='Replace',replacement=repl,texts=out)
for repl in ['$', chr(92), r'\a','$x']:
 add(13,'negative','x','',operation='Replace',replacement=repl,code='FORX0004',error_location='Replacement',error_length=1)
for inp,out in [(',', ['','']),('a,,b',['a','','b']),('abc',['abc']),('',[])]:add(14,'boundary','(,)',inp,operation='Tokenize',texts=out)
for inp,out in [(' \t\r\n ',[]),('\u00a0a\u00a0',['\u00a0a\u00a0']),('',[])]:add(14,'boundary','',inp,operation='TokenizeOne',texts=out)
for op in ['Replace','Tokenize','AnalyzeString']:
 for p in ['', '^$', 'a?', '(a*)', '.*?']:
  add(15,'negative',p,'',operation=op,replacement='bad$' if op=='Replace' else None,code='FORX0003',error_length=0 if not p else 1)
# Unicode16 audit sentinels: expected comes from ratified Unicode files, never runtime categories.
for p,inp,b in [(r'\p{Lo}','\U00011f02',True),(r'\p{IsKawi}','\U00011f02',True),(r'\d','\U0001e4f0',True),(r'\w','\U0001e4d0',True)]:
 c=add(9,'boundary',p,inp,boolean=b);annotations[c['id']]['dataDisposition']='Unicode16-source-overrides-older-oracle'
# Default full lower/upper equality is an OR relation, without transitive closure.
for p,inp,b in [('\u03d1','\u0398',True),('\u0398','\u03f4',True),('\u03d1','\u03f4',False),('i','\u0131',True),('ß','ẞ',True),('ß','SS',False),('[^\u03d1]','\u03f4',True)]:
 c=add(9,'boundary',p,inp,'i',boolean=b);annotations[c['id']]['dataDisposition']='Unicode16-full-default-lower-or-upper-without-transitive-closure'
for scalar,yes in [(0x10000,True),(0xeffff,True),(0xf0000,False),(0x10ffff,False)]:
 add(17,'boundary',r'\i',chr(scalar),boolean=yes)
for pattern_kind,input_kind,where,unit in [('Bytes','Utf16','Pattern','Bytes'),('Utf16','Bytes','Input','Bytes')]:
 c=add(17,'negative','a','a',patternKind=pattern_kind,inputKind=input_kind,permitted=[expected('UnsupportedRepresentation',error=diagnostic('UnsupportedRepresentation',where,0,1,unit))])
 annotations[c['id']].update(oracle=None,comparison='source-policy',reason='Ratified XML character patterns/strict scalar subjects reject byte patterns and raw subjects; XDM only accepts strings')
# Normalize only standalone literal/absent-input calls whose upstream assertions are
# directly representable. Original expressions/assertions remain in byte-identical suites.
import re, html
Q='{http://www.w3.org/2010/09/qt-fots-catalog}'
def arguments(expr):
 m=re.fullmatch(r'\s*(?:fn:)?(matches|replace|tokenize)\s*\((.*)\)\s*',expr,re.S)
 if not m:return None
 src=m.group(2);args=[];i=0
 while i<len(src):
  while i<len(src) and src[i].isspace():i+=1
  if src[i:i+2]=='()':args.append(None);i+=2
  elif src[i:i+1] in ['"',"'"]:
   quote=src[i];i+=1;value=''
   while i<len(src):
    if src[i]==quote:
     if src[i:i+2]==quote*2:value+=quote;i+=2;continue
     i+=1;break
    value+=src[i];i+=1
   else:return None
   args.append(html.unescape(value))
  else:return None
  while i<len(src) and src[i].isspace():i+=1
  if i==len(src):break
  if src[i]!=',':return None
  i+=1
 return m.group(1),args
for suite in ['matches','matches.re','replace','tokenize']:
 for tc in E.parse(OUT/f'qt3/fn/{suite}.xml').getroot().findall(Q+'test-case'):
  if tc.findall(Q+'dependency') or tc.find(Q+'environment') is not None:continue
  parsed=arguments(tc.find(Q+'test').text or '')
  if parsed is None:continue
  fn,args=parsed;assertions=list(tc.find(Q+'result'))
  if len(assertions)!=1:continue
  assertion=assertions[0];tag=assertion.tag.removeprefix(Q);value=assertion.text or ''
  boolvalue=True;strings=None;code=None
  if tag=='assert-true':boolvalue=True
  elif tag=='assert-false':boolvalue=False
  elif tag=='error':code=assertion.get('code')
  elif tag=='assert-string-value' and fn=='replace':strings=[value]
  elif tag=='assert-empty' and fn=='tokenize':strings=[]
  else:continue
  if fn=='matches' and len(args) not in [2,3] or fn=='replace' and len(args) not in [3,4] or fn=='tokenize' and len(args) not in [1,2,3]:continue
  if fn=='matches' and tag not in ['assert-true','assert-false','error']:continue
  op=fn.title() if fn!='tokenize' or len(args)>1 else 'TokenizeOne'
  pattern=args[1] if len(args)>1 else '';replacement=args[2] if fn=='replace' else None
  if pattern is None or fn=='replace' and replacement is None:continue
  flags=args[3] if fn=='replace' and len(args)==4 else args[2] if fn!='replace' and len(args)==3 else ''
  if flags is None:continue
  cap=12 if fn=='matches' else 13 if fn=='replace' else 14
  before=len(cases);c=add(cap,'imported',pattern,args[0] or '',flags,operation=op,boolean=boolvalue,texts=strings,code=code,replacement=replacement,absentInput=args[0] is None,error_length=0 if not pattern else 1)
  oldid=c['id'];id='QT3-'+suite+'-'+tc.get('name');c['id']=id;annotations[id]=annotations.pop(oldid)
  c['provenance']=dict(kind='Imported',uri='https://github.com/w3c/qt3tests/blob/201a6e466940cdfc727f4babfedcde5332b9f578/fn/'+suite+'.xml',clause=tc.get('name'),license='https://www.w3.org/copyright/3-clause-bsd-license-2008/',oracleId='Saxon-HE-12.5')
  if code=='FORX0001':c['permitted'][0]['error'].update(location='Flags',length=0 if not flags else 1)
  elif code=='FORX0004':c['permitted'][0]['error'].update(location='Replacement',length=0 if not replacement else 1)
# Preserve all contract XPath seeds (source recipes remain independent).
seed=json.loads((ROOT/'test/GregsEx.Tests/Contracts/Xml/ContractVectors.json').read_text())
for c in seed['cases']:
 if c['profileId']=='XPath31':
  cases.append(copy.deepcopy(c));annotations[c['id']]=dict(role='contract-seed',oracle='Saxon-HE-12.5',comparison='function-value')
# Processor FORX errors have no original UTF16 pinpoint. Preserve an honest
# whole-argument witness and explicit downstream comparison policy in the sidecar.
for c in cases:
 for e in c['permitted']:
  if e['error'] is None:continue
  a=annotations[c['id']]
  if c['id'].startswith('XML-C'):
   a['diagnosticPolicy']='exact-frozen-contract-span'
  elif e['error']['code'].startswith('FORX'):
   where=e['error']['location'];count=len(c['flags']) if where=='Flags' else len(c['replacementUnits'] or []) if where=='Replacement' else len(c['patternUnits'])
   e['error'].update(offset=0,length=count)
   a['diagnosticPolicy']='code-namespace-argument-domain-original-native-units-valid-real-span; fixture range is whole argument, not processor pinpoint'
  else:a['diagnosticPolicy']='exact-source-derived-domain-or-policy-span'
# Oracle job expressions use integer codepoints; XML transport never normalizes subject/pattern strings.
def xstr(us,kind='Utf16'):
 s=bytes(us).decode('utf-8') if kind=='Utf8' else text(us)
 return 'codepoints-to-string(('+','.join(str(ord(x)) for x in s)+'))' if s else "''"
jobs=E.Element('jobs')
for c in cases:
 a=annotations[c['id']];op=c['operation'];p=c['patternUnits']; inp=c['inputUnits']
 e=c['permitted'][0]
 if a['oracle'] is None:continue
 reason=None
 if c['limits'] is not None:reason='GregsEx resource/output policy; oracle has different budget model'
 if c['patternKind']!='Utf16' or c['inputKind']=='Bytes':reason='Rejected GregsEx representation; processor accepts XDM strings only'
 if e['status'] in ['InvalidInput','UnsupportedRepresentation','Cancelled','TimedOut','HostLimitExceeded','LimitExceeded','DestinationTooSmall']:reason='GregsEx subject/budget/representation policy outside XDM function result'
 if op in ['Find','FindCaptures'] and (not p or c['startAt']):reason='Borrowed native lower-bound/empty-capture API outside F&O processor API'
 if reason:a.update(oracle=None,comparison='source-policy',reason=reason);continue
 try:
  ps=xstr(p); ins='()' if c['absentInput'] else xstr(inp,c['inputKind']);flags=xstr(units(c['flags']))
 except (UnicodeError,ValueError):a.update(oracle=None,comparison='source-domain',reason='Invalid scalar units preserved numerically');continue
 if any(ord(ch)<32 and ord(ch) not in [9,10,13] for ch in text(p)+ (text(inp) if c['inputKind']=='Utf16' else '')):
  a.update(oracle=None,comparison='source-domain',reason='XML1.0 invalid characters are outside XDM serialization domain');continue
 if op=='Matches':q=f'matches({ins},{ps},{flags})'
 elif op=='Parse':q=f'matches("",{ps},{flags})';a['comparison']='parse'
 elif op=='Replace':q=f'replace({ins},{ps},{xstr(c["replacementUnits"] or [])},{flags})'
 elif op=='Tokenize':q=f'tokenize({ins},{ps},{flags})'
 elif op=='TokenizeOne':q=f'tokenize({ins})'
 else:q=f'analyze-string({ins},{ps},{flags})';a['comparison']='native-find' if op in ['Find','FindCaptures'] else 'analyze-tree'
 job=E.SubElement(jobs,'job',id=c['id'])
 if c['id']=='XML-C043':job.set('adapter','direct');q=f'({ps},{ins},{flags})'
 job.text=q
inventory=[]
for i in range(1,18):
 cap=f'XP-{i:03d}';members=[c for c in cases if cap in c['capabilities']]
 inventory.append(dict(capability=cap,clause='5.6.1' if i<=6 else '5.6.2' if i<=11 else '5.6.3' if i==12 else '5.6.4' if i==13 else '5.6.5' if i==14 else '5.6.4-6' if i==15 else '5.6.6/C.1' if i==16 else 'GregsEx XML domain/native offsets/resource policy',witnesses={role:[c['id'] for c in members if annotations[c['id']]['role']==role] for role in ['positive','negative','boundary','imported','contract-seed']},qt3Suites=['matches','matches.re','replace','tokenize','analyze-string'],executionBoundary='Independent processor or source-policy evidence only; GregsEx engine execution is product verification'))
(OUT/'inventory.json').write_text(json.dumps(inventory,indent=2)+'\n')
(OUT/'Corpus.json').write_text(json.dumps(dict(contractVersion='xml-fixtures-1',dataId='Unicode-16.0.0+XML-1.0-5e',cases=cases),indent=2)+'\n')
(OUT/'annotations.json').write_text(json.dumps(annotations,indent=2)+'\n')
E.indent(jobs);E.ElementTree(jobs).write(OUT/'jobs.xml',encoding='utf-8',xml_declaration=True)
print('Prepared',len(cases),'frozen fixtures;',len(jobs),'processor jobs; exact numeric source units retained.')
