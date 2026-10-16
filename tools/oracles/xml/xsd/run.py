"""Actual fixed XSD1.1 processor execution; expected values are never written here."""
from common import *
import sys, zipfile, platform, unicodedata, os, pyexpat, re
# Wheels are retained independently, unpacked installation is disposable.
for item in load(DATA/'processor-pin.json')['wheels']:
 p=TOOLS/'vendor'/item['file']; assert sha(p)==item['sha256']
 with zipfile.ZipFile(p) as z: z.extractall(TOOLS/'vendor/installed')
sys.path.insert(0,str(TOOLS/'vendor/installed'))
import xmlschema, elementpath
from elementpath.regex import install_unicode_data, unicode_version
from xml.etree.ElementTree import Element, SubElement, tostring, register_namespace
assert xmlschema.__version__=='4.3.2' and elementpath.__version__=='5.1.3'
# Explicit static upstream Unicode16 selection, no ambient tables in source expectations.
install_unicode_data('16.0.0'); assert unicode_version()=='16.0.0'
NS='{http://www.w3.org/2001/XMLSchema}'
register_namespace('xs',NS[1:-1])
def schema(p,base='string',second=None):
 root=Element(NS+'schema'); t=SubElement(root,NS+'simpleType',name='T'); r=SubElement(t,NS+'restriction',base='xs:'+base)
 SubElement(r,NS+'pattern',value=p)
 if second is not None: SubElement(r,NS+'pattern',value=second)
 # etree avoids schema attribute whitespace normalization. Binding xs is explicit.
 built=xmlschema.XMLSchema11(tostring(root,encoding='unicode'),validation='strict',use_xpath3=False)
 assert built.types['T'].facets[NS+'pattern'][0].attrib['value']==p, 'schema pattern changed in serialization'
 return built
cache={}; results=[]
for c in load(DATA/'corpus.json')['cases']:
 expected_status=c['permitted'][0]['status']
 # Processors cannot reproduce library representation errors, limits, group0 or diagnostics.
 if c['patternKind']!='Utf16' or c['inputKind']=='Bytes' or c['limits'] is not None:
  results.append(dict(id=c['id'],outcome='not-applicable',reason='GregsEx representation/resource contract, source-only'))
  continue
 try:
  p=text(c['patternUnits']); s=text(c['inputUnits']) if c['inputKind']=='Utf16' else bytes(c['inputUnits']).decode('utf-8','strict')
 except UnicodeError:
  results.append(dict(id=c['id'],outcome='not-applicable',reason='malformed native encoding cannot be supplied as processor scalar string'))
  continue
 if expected_status=='InvalidInput' or not all(xmlchar(ord(x)) for x in p+s):
  results.append(dict(id=c['id'],outcome='not-applicable',reason='native/XML domain diagnostic belongs to GregsEx; schema serialization is not domain oracle'))
  continue
 if p not in cache:
  try:
   st=schema(p).types['T']; facet=st.facets[NS+'pattern']; cache[p]=(st,facet,None)
  except xmlschema.XMLSchemaParseError as e: cache[p]=(None,None,str(e).splitlines()[0])
 st,facet,err=cache[p]
 if err is not None: row=dict(id=c['id'],outcome='invalid-pattern',error=err)
 elif c['operation']=='Parse': row=dict(id=c['id'],outcome='valid-pattern')
 else:
  try: facet(s); member=True
  except xmlschema.XMLSchemaValidationError: member=False
  # This independently checks the absence of other facets/whitespace processing.
  typed=st.is_valid(s); assert typed==member, (c['id'],'unexpected schema host influence')
  row=dict(id=c['id'],outcome='membership',boolean=member,typedStringBoolean=typed,
    translatedPattern=facet.patterns[0].pattern)
 results.append(row)
# XSD1.1-specific assertion qualification (not just a translator).
r=Element(NS+'schema'); t=SubElement(r,NS+'simpleType',name='A'); restriction=SubElement(t,NS+'restriction',base='xs:string')
SubElement(restriction,NS+'assertion',test="$value eq 'a'")
q=xmlschema.XMLSchema11(tostring(r,encoding='unicode'))
assert q.types['A'].is_valid('a') and not q.types['A'].is_valid('b')
# Separately demonstrate host whitespace/facet behavior, excluded from membership expectations.
string=schema('a b').types['T']; token=schema('a b','token').types['T']; union=schema('a',second='b').types['T']
host=dict(stringPreservesWhitespace=[string.is_valid('a b'),string.is_valid('a  b')],
 tokenCollapsesWhitespace=[token.is_valid('a b'),token.is_valid('a  b')],sameRestrictionPatternsAreUnion=[union.is_valid('a'),union.is_valid('b'),union.is_valid('c')],
 literalEntityText=[schema('&').types['T'].is_valid('&'),schema('&').types['T'].is_valid('&amp;')],
 supplementaryName=[schema('\\i').types['T'].is_valid(chr(0x10000)),schema('\\c').types['T'].is_valid(chr(0xeffff))],
 unknownBlockMax=[schema('\\p{IsUnknown}').types['T'].is_valid(chr(0x10ffff)),schema('\\P{IsUnknown}').types['T'].is_valid(chr(0x10ffff))])
assert host['stringPreservesWhitespace']==[True,False] and host['tokenCollapsesWhitespace']==[True,True]
assert host['sameRestrictionPatternsAreUnion']==[True,True,False] and host['literalEntityText']==[True,False]
output=dict(format='xsd-processor-results-1',oracleId=ORACLE_ID,corpusSha256=sha(DATA/'corpus.json'),
 python=sys.version,pythonExecutable=str(pathlib.Path(sys.executable).resolve()),pythonSha256=sha(pathlib.Path(sys.executable).resolve()),
 platform=platform.platform(),machine=platform.machine(),xmlParser=pyexpat.EXPAT_VERSION,
 runtimeModuleSha256={name:sha(pathlib.Path(module.__file__)) for name,module in [('unicodedata',unicodedata),('pyexpat',pyexpat),('python-re',re)]},
 unicodeTableSha256={name:sha(TOOLS/'vendor/installed/elementpath/regex'/name) for name in ['unicode_categories.py','unicode_blocks.py','character_classes.py','patterns.py']},processorUnicode=unicode_version(),ambientUnicode=unicodedata.unidata_version,
 xmlNamePolicy='BMP-only shortcuts audited in elementpath5.1.3 character_classes.py; differs from XML Fifth Edition supplementary names',
 configuration=dict(processor='XMLSchema11',schemaValidation='strict',useXPath3=False,baseType='xs:string',schemaConstruction='ElementTree serialized with namespace and numeric whitespace refs; exact pattern roundtrip asserted',
  facetCount=1,whitespace='preserve',backReferences=False,lazyQuantifiers=False,anchors=False,unicodeSelection='explicit static elementpath16.0.0'),
 xsd11AssertionQualification=True,hostAudit=host,results=results)
path=pathlib.Path(os.environ.get('GREGSEX_XSD_RESULT',str(DATA/'processor-results.json')))
save(path,output)
print(f'Actual XMLSchema11 oracle: {len(results)} rows, {len(cache)} distinct schemas; XSD1.1 assertion and host isolation passed; output {path}.')
