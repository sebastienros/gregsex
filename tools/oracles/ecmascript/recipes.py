"""Specification-authored semantic recipes. Expected results are independent of GregsEx.
The qualified external oracle adds observations, never overwrites golden assertions.
"""
import pathlib,json
ROOT=pathlib.Path(__file__).resolve().parents[3]
def units(s):return list(int.from_bytes(s.encode('utf-16-le','surrogatepass')[i:i+2],'little') for i in range(0,len(s.encode('utf-16-le','surrogatepass')),2))
recipes=[]; recipe_ids=set()
def add(cap,key,p,s,flags='',operation='Exec',last='0',replacement=None,split=None,golden=None,kind='positive',clause=None):
 recipe_id='ES-R-'+key
 if recipe_id in recipe_ids:raise ValueError('Duplicate recipe identity: '+recipe_id)
 recipe_ids.add(recipe_id)
 recipes.append(dict(id='ES-R-'+key,capabilities=[f'ES-{cap:03}'],clause=clause or ('22.2.1; 22.2.2' if cap<=14 else {15:'22.2.7.2',16:'22.2.6.8; 22.2.6.9; 22.2.6.13',17:'22.2.6.11; 22.1.3.19; 22.1.3.20; 22.1.3.19.1',18:'22.2.6.14'}[cap]),kind=kind,patternUnits=units(p),inputUnits=units(s),flags=flags,operation=operation,lastIndexBefore=last,replacementUnits=None if replacement is None else units(replacement),splitLimit=split,golden=golden))
# Golden ranges are [index,length] for every numbered capture; null means undefined.
features=[
(2,'ordered','a|ab','ab',[[0,1]]),(2,'concat','ab','xab',[[1,2]]),(2,'empty-branch','a|','b',[[0,0]]),
(3,'dot','.','a',[[0,1]]),(3,'anchors','^a$','a',[[0,1]]),(3,'multiline','^a$','x\na\nx',[[2,1]],'m'),
(3,'dotall','^.$','\n',[[0,1]],'s'),(3,'scoped','(?i:a)(?-i:b)','Ab',[[0,2]]),
(3,'scoped-dotall','(?s:^.$)','\n',[[0,1]]),(3,'scoped-multiline','(?m:^a$)','x\na\nx',[[2,1]]),
(4,'greedy','a+','aaa',[[0,3]]),(4,'lazy','a+?','aaa',[[0,1]]),(4,'bounded','a{2,3}','aaa',[[0,3]]),
(4,'nullable','(a?)*','a',[[0,1],[0,1]]),(4,'capture-reset','(a|(b))+','ba',[[0,2],[1,1],None]),
(5,'groups','(a)(?:b)(?<c>c)','abc',[[0,3],[0,1],[2,1]]),(5,'empty-capture','(a*)','',[[0,0],[0,0]]),
(5,'unicode-name','(?<π>a)','a',[[0,1],[0,1]]),(5,'duplicate-name','(?<x>a)|(?<x>b)','b',[[0,1],None,[0,1]]),
(6,'numeric-ref','(ab)\\1','abab',[[0,4],[0,2]]),(6,'named-ref','(?<x>a)\\k<x>','aa',[[0,2],[0,1]]),
(6,'forward-ref','\\1(a)','a',[[0,1],[0,1]]),(6,'unmatched-ref','^(a|(b))\\2$','a',[[0,1],[0,1],None]),
(6,'rollback','(a)c|a(b)','ab',[[0,2],None,[1,1]]),
(7,'lookahead','a(?=(b))','ab',[[0,1],[1,1]]),(7,'negative-ahead','a(?!b)','ac',[[0,1]]),
(7,'behind','(?<=a)b','ab',[[1,1]]),(7,'reverse-captures','(?<=([ab]+)([bc]+))$','abc',[[3,0],[0,1],[1,2]]),
(7,'negative-behind','(?<!a)b','cb',[[1,1]]),(7,'assertion-atomic','(?=(a+))a*b\\1','baaabac',[[3,3],[3,1]]),
(7,'negative-assertion-capture','(?<!(a))b','cb',[[1,1],None]),
(8,'control','\\cA','\x01',[[0,1]]),(8,'hex','\\x61','a',[[0,1]]),(8,'unicode-escape','\\u0061','a',[[0,1]]),
(8,'null','\\0','\0',[[0,1]]),(8,'pair','^.$','😀',[[0,2]],'u'),(8,'lone','^.$','\ud800',[[0,1]]),
(9,'digit','\\d+','١12',[[1,2]]),(9,'notdigit','\\D+','1ab',[[1,2]]),(9,'word','\\w+','éabc',[[1,3]]),
(9,'notword','\\W','é',[[0,1]]),(9,'space','\\s','\ufeff',[[0,1]]),(9,'notspace','\\S','a',[[0,1]]),
(9,'boundary','\\ba\\b','a',[[0,1]]),(9,'nonboundary','a\\Bb','ab',[[0,2]]),(9,'range','[a-c]+','abc',[[0,3]]),
(9,'negated','[^a]','b',[[0,1]]),(9,'empty-set','[]','',None),(9,'negated-empty-class','[^]','\n',[[0,1]]),
(10,'category','\\p{General_Category=Letter}','π',[[0,1]],'u'),(10,'script','\\p{Script=Greek}','π',[[0,1]],'u'),
(10,'scx','\\p{Script_Extensions=Hira}','ー',[[0,1]],'u'),(10,'binary','\\p{ASCII}','a',[[0,1]],'u'),
(11,'intersection','[\\p{ASCII}&&\\p{Letter}]+','1abc',[[1,3]],'v'),
(11,'subtraction','[[a-z]--[aeiou]]+','aeibcd',[[3,3]],'v'),(11,'q','[\\q{ab|cd}]','xab',[[1,2]],'v'),
(11,'q-longest','[\\q{a|ab}]','ab',[[0,2]],'v'),(11,'q-empty','[\\q{ab|}]','x',[[0,0]],'v'),
(13,'ascii-fold','a','A',[[0,1]],'i'),(13,'kelvin-u','k','K',[[0,1]],'iu'),
(13,'kelvin-legacy','k','K',None,'i'),(13,'long-s','s','ſ',[[0,1]],'iu'),
(13,'complement-u','\\P{Lowercase_Letter}','a',[[0,1]],'iu'),(13,'complement-v','\\P{Lowercase_Letter}','a',None,'iv'),
(13,'q-fold','[\\q{ab}]','AB',[[0,2]],'iv'),
(14,'octal','\\141','a',[[0,1]]),(14,'identity','\\q','q',[[0,1]]),(14,'brace','a{','a{',[[0,2]]),
(14,'quantified-ahead','(?=a)?a','a',[[0,1]]),(14,'class-control','[\\c1]','\x11',[[0,1]]),
]
for row in features:
 cap,key,p,s,g,*f=row; flags=f[0] if f else ''
 # Fixed Unicode-mode syntax stays in its admitted modes; baseline constructs run in all modes.
 modes=[flags] if cap>=10 or ('u' in flags or 'v' in flags) else [flags,flags+'u',flags+'v']
 if cap==10 and flags=='u':modes=['u','v']
 if cap==14:modes=['']
 for flag in modes:
  tag=flag or 'legacy'
  add(cap,key+'::positive::'+tag,p,s,flag,golden={'captures':g})
  add(cap,key+'::empty-input::'+tag,p,'',flag,kind='boundary')
  add(cap,key+'::negative-input::'+tag,p,'#',flag,kind='negative')
for prop,s in [('Basic_Emoji','😀'),('Emoji_Keycap_Sequence','1️⃣'),('RGI_Emoji_Modifier_Sequence','👍🏽'),('RGI_Emoji_Flag_Sequence','🇫🇷'),('RGI_Emoji_Tag_Sequence','🏴\U000e0067\U000e0062\U000e0065\U000e006e\U000e0067\U000e007f'),('RGI_Emoji_ZWJ_Sequence','👩\u200d💻'),('RGI_Emoji','👩\u200d💻')]:
 for flags in ['v','iv']:
  add(12,prop+'-'+flags,'\\p{'+prop+'}',s,flags,golden={'captures':[[0,len(units(s))]]})
  add(12,prop+'-miss-'+flags,'\\p{'+prop+'}','a',flags,golden={'captures':None},kind='negative')
  add(12,prop+'-empty-'+flags,'\\p{'+prop+'}','',flags,golden={'captures':None},kind='boundary')
for flag in ['', 'd','g','i','m','s','u','v','y','dgimsuy','dgimsvy']:
 add(1,'flag-'+(flag or 'none'),'a','a',flag,operation='Parse',golden={'status':'ParseSuccess'})
for flag in ['gg','uv','vu','x','D']:
 add(1,'bad-flag-'+str(len(recipes)),'a','a',flag,operation='Parse',golden={'status':'InvalidSyntax'},kind='negative')
# Parse errors, edition clause mapping and explicit UTF16 source spans.
invalid=[(3,'(?g:a)',''),(3,'(?ii:a)',''),(3,'(?i-i:a)',''),(4,'a{2,1}',''),(4,'*a',''),(5,'(?<x>a)(?<x>b)',''),(5,'(?<1>a)','u'),(6,'\\2(a)','u'),(6,'\\k<z>(?<x>a)','u'),(7,'(?=a)+','u'),(8,'\\x','u'),(8,'\\u{110000}','u'),(8,'\\q','u'),(9,'[z-a]',''),(9,'[\\d-a]','u'),(10,'\\p{letter}','u'),(10,'\\p{ Script=Greek}','u'),(11,'[a-z--[aeiou]]','v'),(11,'[a&&b--c]','v'),(11,'[()]','v'),(11,'[^\\q{ab}]','v'),(12,'\\P{RGI_Emoji}','v'),(12,'\\p{RGI_Emoji}','u'),(14,'\\141','u'),(14,'[\\c1]','v')]
for i,(cap,p,f) in enumerate(invalid):add(cap,'invalid-'+str(i),p,'',f,operation='Parse',golden={'status':'InvalidSyntax'},kind='negative')
for mode in ['', 'u','v']:
 tag=mode or 'legacy'
 for op in ['Exec','Test']:
  add(15,'success-'+op+'-'+tag,'(a)','a','g'+mode,operation=op,golden={'status':'Match','lastIndexAfter':'1'},kind='positive')
  add(15,'failure-'+op+'-'+tag,'(a)','b','g'+mode,operation=op,golden={'status':'NoMatch','lastIndexAfter':'0'},kind='negative')
 for flags,last,s in [('g','0','ba'),('y','0','ba'),('y','1','ba'),('g','99','a'),('','99','a'),('g','NaN','a'),('g','-0','a'),('g','-Infinity','a'),('g','+Infinity','a'),('g','1.8','ba')]:
  for operation in ['Exec','Test']:add(15,'state-'+str(len(recipes)), '(a)?',s,flags+mode,operation=operation,last=last,kind='boundary')
 add(15,'empty-exec-'+tag,'','😀','g'+mode,last='0',golden={'captures':[[0,0]],'lastIndexAfter':'0'},kind='boundary')
 for last in ['0','1','2','3']:
  add(15,'empty-next-'+tag+'-'+last,'','😀','g'+mode,operation='ExecNext',last=last,kind='boundary')
 for terminator in ['\n','\r','\u2028','\u2029']:
  add(3,'line-dot-'+tag+'-'+str(ord(terminator)),'^.$',terminator,mode,golden={'captures':None},kind='negative')
  add(3,'line-dotall-'+tag+'-'+str(ord(terminator)),'^.$',terminator,'s'+mode,golden={'captures':[[0,1]]})
 if mode:add(15,'inside-pair-'+tag,'.','😀',mode+'y',last='1',golden={'captures':[[0,2]],'lastIndexAfter':'2'},kind='boundary')
 for op in ['Match','MatchAll','Search']:
  add(16,op+'-'+tag,'(a)|(b)','aba','dg'+mode,operation=op,last='1')
  add(16,op+'-empty-'+tag,'','😀','g'+mode,operation=op,last='0',kind='boundary')
  add(16,op+'-miss-'+tag,'a','b','g'+mode,operation=op,last='7',kind='negative')
 for op in ['Replace','ReplaceAll']:
  for template in ['$$','$&',"$`", "$'",'$1$2','$12','$99','$<x>','$<missing>','$0','$','x']:
   add(17,op+'-'+str(len(recipes)),'(?<x>a)|(b)','ab','g'+mode,operation=op,last='9',replacement=template)
  add(17,op+'-empty-'+tag,'','😀','g'+mode,operation=op,replacement='-',kind='boundary')
  add(17,op+'-miss-'+tag,'x','ab','g'+mode,operation=op,replacement='-',kind='negative')
 for p,s in [('(,)|(z)','a,b'),('','😀'),('',''),('(a*)','a'),('x','a')]:
  for limit in [None,'0','1','2','-1','NaN','+Infinity','4294967297']:
   add(18,'split-'+str(len(recipes)),p,s,mode,operation='Split',last='7',split=limit,kind='negative' if p=='x' and limit is None else 'boundary' if limit else 'positive')
for op,cap in [('MatchAll',16),('ReplaceAll',17)]:add(cap,op+'-requires-global','a','a',operation=op,replacement='x' if op=='ReplaceAll' else None,golden={'status':'HostArgumentError'},kind='negative')
(ROOT/'test/corpora/ecmascript/recipes.json').write_text(json.dumps({'schemaVersion':'ecma-recipes-1','recipes':recipes},indent=2,ensure_ascii=True)+'\n')
(ROOT/'test/corpora/ecmascript/raw-flag-boundary.json').write_text(json.dumps({'schemaVersion':'ecma-raw-flag-boundary-1','id':'ES-FLAG-SURROGATE','capabilities':['ES-001'],'profileId':'EcmaScript2025','dataId':'Unicode-16.0.0+Emoji-16.0','patternUnits':[97],'rawFlagUnits':[55296],'inputUnits':[97],'operation':'Parse','expected':{'status':'InvalidSyntax','location':'Flags','offset':0,'length':1,'featureId':'ES-001'},'provenance':{'kind':'Specification','uri':'https://tc39.es/ecma262/2025/multipage/text-processing.html#sec-regexpinitialize','clause':'ECMAScript2025 22.2.3.2; ECMAScript flag-source diagnostic contract','license':'GregsEx repository license; independent recipe','oracleId':None},'oracleCorroboration':'node-24.4.1-v8-13.6.233.10-node.17-unicode16-darwin-arm64: SyntaxError','reason':'The frozen EcmaCorpus v1 Flags field is a JSON string; System.Text.Json rejects an unpaired surrogate before the fixture reaches the parser. Preserve exact numeric units separately, reconstruct a char flag span and assert the actual descriptor negative boundary. No shared-schema change or skipped flag semantic.'},indent=2)+'\n')
print('Authored',len(recipes),'recipes with',sum(x['golden'] is not None for x in recipes),'independent assertions')
