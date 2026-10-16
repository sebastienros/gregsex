"""Freeze source-only GregsEx resource recipes; no external engine is their oracle."""
import json,pathlib,copy
ROOT=pathlib.Path(__file__).resolve().parents[3]
base=json.load(open(ROOT/'test/GregsEx.Tests/Contracts/EcmaScript/ContractVectors.json'))
# Retain all independent contract recipes unchanged, including existing work counters.
(ROOT/'test/corpora/ecmascript/contract-corpus.json').write_text(json.dumps(base,indent=2)+'\n')
cases=[]
fields=['maxPatternUnits','maxCompileWork','maxWork','maxWorkspaceBytes','frameCapacity','maxResults','maxOutputUnits','maxHostMemoryBytes','cancelled','timeoutTicks']
def limits(**kw):return {k:kw.get(k,False if k=='cancelled' else None) for k in fields}
def add(key,cap,status,code,p,s,op,limit,capacity=0):
 c={'id':'ES-L-'+key,'capabilities':[cap], 'provenance':{'kind':'Specification','uri':'https://github.com/sebastienros/gregsex','clause':'GregsEx ECMAScript contract v1 resource policy; API-05/PERF-004/VERIFY-03','license':'GregsEx repository license','oracleId':None},'patternUnits':[ord(x) for x in p],'inputUnits':[ord(x) for x in s],'flags':'g','operation':op,'startAt':0,'destinationLength':None,'limits':limits(**limit),'lastIndexBefore':'0','replacementUnits':[120,120,120] if op=='Replace' else None,'splitLimit':None,'expected':{'status':status,'lastIndexAfter':None,'matches':[],'texts':None,'error':{'code':code,'featureId':cap,'location':'Pattern' if op=='Parse' else 'Input' if cap=='ES-020' else 'Host','offset':0,'length':0,'offsetUnit':'Utf16CodeUnits','workConsumed':None,'requiredCapacity':capacity}}}
 cases.append(c)
# These are portable category recipes, not fabricated deterministic work counts or
# simulated JavaScript exhaustion. Integration must bind actual bounded GregsEx.
add('output','ES-019','HostLimitExceeded','OutputLimitExceeded','a','a','Replace',{'maxOutputUnits':2},3)
add('results','ES-019','HostLimitExceeded','ResultLimitExceeded','','ab','MatchAll',{'maxResults':1},2)
# Memory failure RequiredCapacity depends on implementation accounting. Preserve
# that required check in coverage/README rather than inventing its byte count.
add('output-boundary','ES-019','HostLimitExceeded','OutputLimitExceeded','a','a','Replace',{'maxOutputUnits':3},3)
cases[-1]['expected']={'status':'Match','lastIndexAfter':'0','matches':[], 'texts':[[120,120,120]],'error':None}
add('pattern','ES-020','LimitExceeded','PatternLimitExceeded','aa','','Parse',{'maxPatternUnits':1})
add('compile-work','ES-020','LimitExceeded','CompileWorkLimitExceeded','(a|b)*','','Parse',{'maxCompileWork':1})
add('match-work','ES-020','LimitExceeded','WorkLimitExceeded','a','a','Exec',{'maxWork':1})
add('workspace','ES-020','LimitExceeded','WorkspaceLimitExceeded','a','a','Exec',{'maxWorkspaceBytes':1})
add('frames','ES-020','LimitExceeded','WorkspaceCapacityExceeded','(a|aa)*b','aaaa','Exec',{'frameCapacity':1})
add('cancelled','ES-020','Cancelled','Cancelled','a','','Exec',{'cancelled':True})
add('deadline','ES-020','TimedOut','TimedOut','a','','Exec',{'timeoutTicks':1})
(ROOT/'test/corpora/ecmascript/resource-corpus.json').write_text(json.dumps({'contractVersion':base['contractVersion'],'profileId':base['profileId'],'dataId':base['dataId'],'cases':cases},indent=2)+'\n')
print('Frozen',len(base['cases']),'contract and',len(cases),'portable resource recipes; no engine assertions executed')
