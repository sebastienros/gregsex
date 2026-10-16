"""Fixed replay comparison, including full trees/native coordinates and explicit dispositions."""
import collections, hashlib, json, pathlib, struct, xml.etree.ElementTree as E
ROOT=pathlib.Path(__file__).resolve().parents[4];BASE=ROOT/'test/corpora/xml/xpath';REPORT=ROOT/'test/corpora/xml/xpath/oracle'
def units(s):return list(struct.unpack('<'+'H'*(len(s.encode('utf-16-le','surrogatepass'))//2),s.encode('utf-16-le','surrogatepass')))
def text(us):return struct.pack('<'+'H'*len(us),*us).decode('utf-16-le','surrogatepass')
def node(kind,parent,group,index,length):return dict(kind=kind,parentIndex=parent,groupNumber=group,range=dict(index=index,length=length))
def tree(value, inputkind):
 root=E.fromstring(value);nodes=[];matches=[];cursor=0
 def length(s):return len(s.encode('utf-8')) if inputkind=='Utf8' else len(units(s))
 def walk(el,parent,pos):
  name=el.tag.split('}')[-1];n=len(nodes);content=''.join(el.itertext());size=length(content)
  kind={'match':0,'non-match':1,'group':2}[name];group=int(el.get('nr','0'))
  nodes.append(node(kind,parent,group,pos,size));p=pos
  if kind!=1:
   if el.text:nodes.append(node(3,n,0,p,length(el.text)));p+=length(el.text)
   for ch in el:
    walk(ch,n,p);p+=length(''.join(ch.itertext()))
    if ch.tail:nodes.append(node(3,n,0,p,length(ch.tail)));p+=length(ch.tail)
  return size
 for ch in root:
  begin=len(nodes);size=walk(ch,-1,cursor)
  if ch.tag.endswith('}match'):
   groups=[n for n in nodes[begin:] if n['kind']==2];count=max([n['groupNumber'] for n in groups],default=0)
   captures=[dict(participated=False,index=0,length=0) for _ in range(count+1)]
   captures[0]=dict(participated=True,index=cursor,length=size)
   for n in groups:captures[n['groupNumber']]=dict(participated=True,**n['range'])
   matches.append(dict(range=dict(index=cursor,length=size),captures=captures))
  cursor+=size
 return nodes,matches
for f in json.loads((BASE/'input-lock.json').read_text())['files']:
 b=(BASE/f['path']).read_bytes();assert len(b)==f['bytes'] and hashlib.sha256(b).hexdigest()==f['sha256'], 'Changed input '+f['path']
results=E.parse(REPORT/'oracle-results.xml').getroot();actual={r.get('id'):r for r in results}
assert len(actual)==len(results), 'Duplicate result IDs'
assert results.get('processor')=='HE 12.5' and results.get('xml-version')=='10' and results.get('xsd-version')=='11', 'Wrong processor/configuration'
assert len([r for r in results if r.get('id').startswith('QT3/')])==1367, 'Incomplete QT3 replay'
cases=json.loads((BASE/'Corpus.json').read_text())['cases'];anno=json.loads((BASE/'annotations.json').read_text());dispositions=json.loads((BASE/'dispositions.json').read_text()) if (BASE/'dispositions.json').exists() else {}
checks=[];unexpected=[]
for c in cases:
 a=anno[c['id']]
 if a['oracle'] is None:
  checks.append(dict(id=c['id'],outcome='source-only',reason=a['reason']));continue
 r=actual[c['id']];items=[dict(type=i.get('type'),units=list(map(int,(i.text or '').split()))) for i in r]
 def agrees(e):
  if e['error'] and e['error']['code'].startswith('FORX'):
   return r.get('error')==e['error']['code'] and r.get('namespace')==e['error']['namespace']
  if r.get('error') or r.get('status')=='driver-error':return False
  op=c['operation']
  if op=='Parse':return True
  if op=='Matches':return len(items)==1 and items[0]['type']=='boolean' and text(items[0]['units'])==str(e['boolean']).lower()
  if op in ['Replace','Tokenize','TokenizeOne']:return [i['units'] for i in items]==e['texts']
  nodes,matches=tree(text(items[0]['units']),c['inputKind'])
  if op=='AnalyzeString':return nodes==e['tree']
  # F&O analyze-string omits unmatched groups; frozen FindCaptures retains them.
  if e['status']=='NoMatch':return not matches
  if not matches or not e['matches']:return False
  m=matches[0];target=e['matches'][0]
  while len(m['captures'])<len(target['captures']):m['captures'].append(dict(participated=False,index=0,length=0))
  return m==target
 ok=any(agrees(e) for e in c['permitted']);d=dispositions.get(c['id'])
 if d and (d['observed']!=dict(status=r.get('status'),error=r.get('error'),namespace=r.get('namespace'),items=items) or d['retainedExpectation']!=c['permitted']):d=None
 outcome='passed' if ok else 'disposition' if d else 'failed'
 checks.append(dict(id=c['id'],outcome=outcome,disposition=d,actualError=r.get('error')))
 if not ok and not d:unexpected.append(c['id'])
qt=[]
for r in results:
 if not r.get('id').startswith('QT3/'):continue
 status=r.get('status');d=dispositions.get(r.get('id'))
 qt.append(dict(id=r.get('id'),outcome=status,reason=r.get('reason'),disposition=d))
 if status in ['failed','driver-error'] and not d:unexpected.append(r.get('id'))
summary=dict(processor=results.attrib,fixtures=checks,qt3=qt,counts=dict(fixtures=dict(collections.Counter(x['outcome'] for x in checks)),qt3=dict(collections.Counter(x['outcome'] for x in qt))),unexpected=unexpected)
(REPORT/'comparison.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps(summary['counts']));print('Unexplained',unexpected)
raise SystemExit(bool(unexpected))
