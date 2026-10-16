import pathlib,gzip,json,hashlib,collections,shutil
root=pathlib.Path(__file__).resolve().parents[4];base=pathlib.Path('/private/tmp/m5-complete-native');reports=root/'test/corpora/pcre2/parity';recipes=[json.loads(l) for l in gzip.open(root/'test/corpora/pcre2/upstream-recipes.jsonl.gz','rt')];lookup={(r.get('width'),r.get('source'),r.get('inputEnd'),r.get('kind')):r for r in recipes};plans={};cases=[];ledger=[];matchContexts=[];helperCases=[];lastMatch=None;lastContext=None;lastResult=None
for path in sorted((reports/'complete-native').glob('*.jsonl.gz')):
 if path.name.startswith('pre-'):continue
 width=int(path.name.split('-')[0]);source='testdata/'+path.name.split('-',1)[1].split('.jsonl')[0];events=[json.loads(l) for l in gzip.open(path,'rt')];compile=None;context=None
 for at,e in enumerate(events):
  kind=e['kind']
  if kind=='compileInput':compile=e
  elif kind=='compileResult' and e['accepted']:
   recipe=lookup.get((width,source,e['inputEnd'],'pattern'),{});controls=[x['control'] for x in recipe.get('controls',[])];optim=[True]*3
   if 'optimization_none' in controls:optim=[False]*3
   for name,i in [('auto_possess',0),('dotstar_anchor',1),('start_optimize',2)]:
    if name+'_off' in controls:optim[i]=False
    if name in controls:optim[i]=True
   tables=base/(path.name.split('.jsonl')[0]+'-tables')/(str(e['inputEnd'])+'.bin')
   profile={'width':width,'compileFlags':compile['options'],'extraFlags':compile['extraOptions'],'newline':compile['newline'],'bsr':compile['bsr'],'maxVariableLookbehind':compile['maxVariableLookbehind'],'optimization':optim,'optimizerDirective':-1,'characterTables':list(tables.read_bytes()) if tables.exists() else None}
   plans[(width,e['codeKey'])]={'input':compile,'profile':profile,'source':source,'inputEnd':e['inputEnd'],'codeKey':e['codeKey']}
  elif kind=='matchContext':context=e
  elif kind=='matchInput':
   lastMatch=e;lastContext=context
   recipe=lookup.get((width,source,e['inputEnd'],'subject'),{})
   if recipe:matchContexts.append({'id':recipe['id'],'input':e,'context':context,'planKey':f"{width}-{context['codeKey']}"})
  elif kind=='matchResult':lastResult=e
  elif kind=='substring':
   recipe=lookup.get((width,source,e['inputEnd'],'subject'),{})
   if lastMatch is None or lastResult is None:raise ValueError('substring without match')
   helperCases.append({'id':recipe.get('id'),'source':source,'input':lastMatch,'context':lastContext,'result':lastResult,'helper':e,'planKey':f"{width}-{lastContext['codeKey']}"})
  elif kind=='substituteInput':
   recipe=lookup.get((width,source,e['inputEnd'],'subject'),{});end=next((j for j in range(at+1,len(events)) if events[j]['kind']=='substituteResult'),None)
   if end is None:raise ValueError('missing result')
   plan=plans.get((width,context['codeKey']))
   if plan is None:ledger.append({'id':recipe.get('id'),'reason':'unresolved semantic binding','context':context});continue
   cases.append({'id':recipe.get('id',f'{width}-{source}-{e["inputEnd"]}'),'plan':plan,'context':context,'input':e,'events':events[at+1:end+1],'controls':[x['control'] for x in recipe.get('controls',[])],'excludedFields':recipe.get('excludedFields',[]),'rawRecipe':recipe.get('rawRecipeLineLatin1')})
p=reports/'complete-substring-cases.json.gz';p.write_bytes(gzip.compress(json.dumps({'plans':{f'{w}-{k}':v for (w,k),v in plans.items()},'cases':helperCases},separators=(',',':')).encode(),mtime=0))
print(len(helperCases),'native substring helper calls')
p=reports/'complete-match-contexts.json.gz';p.write_bytes(gzip.compress(json.dumps({'plans':{f'{w}-{k}':v for (w,k),v in plans.items()},'contexts':matchContexts},separators=(',',':')).encode(),mtime=0))
payload=json.dumps({'cases':cases,'ledger':ledger},separators=(',',':')).encode();p=reports/'complete-substitute-cases.json.gz';p.write_bytes(gzip.compress(payload,mtime=0));print(len(cases),'substitute invocations',len(ledger),'pending bindings')
(reports/'complete-cases-receipt.json').write_text(json.dumps({'source':'PCRE2-10.46/b2bd4254; unchanged retained static libraries; development observer independently decodes exact upstream source before managed comparison','cases':len(cases),'pendingBindings':len(ledger),'path':str(p.relative_to(root)),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()},indent=2)+'\n')
if (root/'testsaved1').exists():shutil.move(root/'testsaved1',reports/'retained-tool-serialization-testsaved1')
