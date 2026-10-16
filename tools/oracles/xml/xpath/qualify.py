"""Fixed processor/source qualification; no runtime Unicode categories or arguments."""
import hashlib,json,os,pathlib,subprocess,zipfile,xml.etree.ElementTree as E
ROOT=pathlib.Path(__file__).resolve().parents[4];BASE=ROOT/'test/corpora/xml/xpath';REPORT=ROOT/'test/corpora/xml/xpath/oracle'
CACHE=pathlib.Path(os.environ.get('GREGSEX_XPATH_ORACLE_CACHE','/private/tmp/gregsex-xpath-intake'))
lock=json.loads((BASE/'processor-lock.json').read_text())
for f in lock['artifacts']:
 p=CACHE/f['name'];b=p.read_bytes();assert len(b)==f['bytes'] and hashlib.sha256(b).hexdigest()==f['sha256'],f['name']
source_lock=json.loads((BASE/'source-lock.json').read_text())
contract=json.loads((ROOT/'test/corpora/xml/sources/source-manifest.json').read_text())
for name,key in [('functions-31.html','fo-rec'),('UnicodeData-16.txt','ucd-unicodedata'),('Blocks-16.txt','ucd-blocks'),('SpecialCasing-16.txt','ucd-specialcasing')]:
 expected=next(f['sha256'] for f in contract['sources'] if f['id']==key)
 assert hashlib.sha256((BASE/'sources'/name).read_bytes()).hexdigest()==expected, 'Contract source mismatch '+name
for f in source_lock['files']:
 b=(BASE/f['path']).read_bytes();assert len(b)==f['bytes'] and hashlib.sha256(b).hexdigest()==f['sha256'],f['path']
ucd={int(line.split(';')[0],16):line.strip().split(';') for line in (BASE/'sources/UnicodeData-16.txt').read_text().splitlines()}
with zipfile.ZipFile(CACHE/'Saxon-HE-12.5.jar') as z:
 categories=E.fromstring(z.read('net/sf/saxon/data/categories.xml'))
 variants=E.fromstring(z.read('net/sf/saxon/data/casevariants.xml'))
 assert not any(int(c.get('n'),16) in [0xdf,0x1e9e] for c in variants), 'Sharp-S resource changed'
 def saxon_category(cp):
  for cat in categories:
   for r in cat:
    if int(r.get('f'),16)<=cp<=int(r.get('t'),16):return cat.get('name')
  return 'implicit/unlisted'
 probes=[]
 for cp in [0x11f02,0x1e4f0,0x1e4d0]:
  row=ucd[cp];actual=saxon_category(cp);assert row[2] in ['Lo','Nd'] and actual=='Cn'
  probes.append(dict(scalar=f'U+{cp:04X}',unicode16=row,saxonResourceCategory=actual))
 resources=[dict(name=n,bytes=len(z.read(n)),sha256=hashlib.sha256(z.read(n)).hexdigest()) for n in ['net/sf/saxon/data/categories.xml','net/sf/saxon/data/unicodeBlocks.xml','net/sf/saxon/data/casevariants.xml']]
 with zipfile.ZipFile(CACHE/'Saxon-HE-12.5-sources.jar') as src:
  files=['net/sf/saxon/functions/Tokenize_3.java','net/sf/saxon/functions/RegexFunction.java','net/sf/saxon/regex/CaseVariants.java','net/sf/saxon/regex/UnicodeBlocks.java']
  inspected=[dict(name=n,bytes=len(src.read(n)),sha256=hashlib.sha256(src.read(n)).hexdigest()) for n in files]
caseRelations=[]
for first,second,expected in [(0x03d1,0x0398,True),(0x0398,0x03f4,True),(0x03d1,0x03f4,False),(0x69,0x131,True),(0xdf,0x1e9e,True)]:
 def key(cp,field):return ucd[cp][field] or f'{cp:04X}'
 # These pairs have singleton lower/upper; ß full uppercase SS differs but
 # equality of lowercase establishes ß~ẞ without using simple uppercase.
 equal=key(first,13)==key(second,13) or key(first,12)==key(second,12)
 assert equal==expected
 caseRelations.append(dict(first=f'U+{first:04X}',second=f'U+{second:04X}',equalFullLowerOrUpper=equal,unicode16First=ucd[first],unicode16Second=ucd[second]))
java=pathlib.Path(os.environ.get('GREGSEX_XPATH_JAVA_HOME','/Library/Java/JavaVirtualMachines/temurin-25.jdk/Contents/Home'))
info=subprocess.run([str(java/'bin/java'),'-version'],capture_output=True,text=True,check=True)
result=dict(processorLock=lock,sourceLock=source_lock,javaVersion=info.stderr,javaBinarySha256=hashlib.sha256((java/'bin/java').read_bytes()).hexdigest(),os=subprocess.check_output(['uname','-a'],text=True).strip(),resources=resources,inspectedSourceFiles=inspected,unicodeProbes=probes,caseRelations=caseRelations,xmlPolicy='Explicit Saxon XML1.0; exact fifth-edition NameChar/Char boundary probes retained in source corpus; no claim of processor Unicode16 identity',casePolicy='Saxon embedded casevariants.xml, not Java Pattern/ambient runtime Unicode; ratified full default Unicode16 lower/upper remains target',tokenizePolicy='Tokenize_3.call returns on absent/empty input before getRegularExpression; F&O1.4 prioritizes errors while XPath2.3.4 allows skipped argument error evaluation; GregsEx ratified deterministic eager validation retained')
(REPORT/'qualification.json').write_text(json.dumps(result,indent=2)+'\n')
print('Verified four pinned artifacts, all retained source bytes and three reduced Unicode-version disagreements.')
