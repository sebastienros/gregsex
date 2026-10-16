"""Fixed internal helpers; no CLI parsing and no GregsEx implementation dependency."""
import hashlib, json, pathlib, struct
ROOT = pathlib.Path(__file__).resolve().parents[4]
DATA = ROOT / 'test/corpora/xml/xsd'
TOOLS = pathlib.Path(__file__).resolve().parent
ORACLE_ID = 'xmlschema-4.3.2+elementpath-5.1.3-unicode16'
XSD = 'https://www.w3.org/TR/2012/REC-xmlschema11-2-20120405/'
XML = 'https://www.w3.org/TR/2008/REC-xml-20081126/'
MIT = 'MIT; original GregsEx fixtures, see LICENSE'
def units(s):
    b=s.encode('utf-16-le', 'surrogatepass')
    return list(struct.unpack('<'+'H'*(len(b)//2), b))
def text(u):
    return struct.pack('<'+'H'*len(u), *u).decode('utf-16-le','surrogatepass')
def xmlchar(c):
    return c in (9,10,13) or 0x20<=c<=0xd7ff or 0xe000<=c<=0xfffd or 0x10000<=c<=0x10ffff
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def load(p): return json.loads(p.read_text())
def save(p,o): p.write_text(json.dumps(o,ensure_ascii=True,indent=2)+'\n')
def expected(status, length=0, error=None):
    return dict(status=status, boolean={'Match':True,'NoMatch':False}.get(status),
      matches=[dict(range=dict(index=0,length=length),captures=[dict(participated=True,index=0,length=length)])] if status=='Match' else [],
      texts=None,tree=[],error=error)
def failure(code,loc='Pattern',offset=0,length=1,kind='Utf16'):
    return dict(code=code,location=loc,offset=offset,length=length,
      offsetUnit='Bytes' if kind!='Utf16' else 'Utf16CodeUnits',namespace=None)
