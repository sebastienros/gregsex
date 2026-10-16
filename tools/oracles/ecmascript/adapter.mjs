// Qualified builtins only. Adding d in an independent shadow run recovers native
// capture coordinates; the original run supplies state, visible d and host outputs.
export const fromUnits=u=>String.fromCharCode(...u);
export const toUnits=s=>Array.from({length:s.length},(_,i)=>s.charCodeAt(i));
export const numeric=s=>s==='NaN'?NaN:s==='+Infinity'?Infinity:s==='-Infinity'?-Infinity:Number(s);
export const numberToken=n=>Object.is(n,-0)?'-0':Number.isNaN(n)?'NaN':n===Infinity?'+Infinity':n===-Infinity?'-Infinity':String(n);
const absent=()=>({participated:false,index:0,length:0});
const capture=range=>range?{participated:true,index:range[0],length:range[1]-range[0]}:absent();
const namesSnapshot=m=>m?Object.entries(m.groups??{}):null;
// Small structural name-number audit, separate from matching. Escaped identifier
// names are decoded by the native group's keys in source order; recipes retain
// raw UTF16 pattern units, and alias IDs are determined from capture parentheses.
function aliases(p,actualNames){
 const result=new Map();let group=0,inClass=0,escaped=false;
 for(let i=0;i<p.length;i++){
  const c=p[i];if(escaped){escaped=false;continue}if(c==='\\'){escaped=true;continue}
  if(c==='['){inClass++;continue}if(c===']'){if(inClass)inClass--;continue}if(inClass||c!=='(')continue;
  if(p[i+1]!=='?'){group++;continue}
  if(p[i+2]==='<'&&!['=','!'].includes(p[i+3])){
   group++;let end=p.indexOf('>',i+3);if(end<0)throw Error('Malformed named group');
   let name=p.slice(i+3,end).replace(/\\u\{([0-9a-fA-F]+)\}|\\u([0-9a-fA-F]{4})/g,(_,a,b)=>String.fromCodePoint(parseInt(a??b,16)));
   if(!result.has(name))result.set(name,[]);result.get(name).push(group);
  }
 }
 if(JSON.stringify([...result.keys()].sort())!==JSON.stringify(actualNames.sort()))throw Error('Name audit mismatch');
 return result;
}
function match(m,p,hasIndices,wholeOnly=false){
 const caps=(wholeOnly?[m.indices[0]]:m.indices).map(capture);
 const names=wholeOnly?[]:[...aliases(p,Object.keys(m.groups??{}))].map(([name,groupIds])=>({name,groupIds,capture:capture(m.indices.groups[name])}));
 return {index:m.index,length:m[0].length,captures:caps,names,hasIndices};
}
export function observe(recipe){
 const p=fromUnits(recipe.patternUnits),s=fromUnits(recipe.inputUnits), flags=recipe.flags;
 let r;
 try{r=new RegExp(p,flags)}catch(e){
  if(e.name!=='SyntaxError')throw e;
  const seen=new Set();let offset=-1;
  for(let i=0;i<flags.length;i++){let f=flags[i];if(!'dgimsuvy'.includes(f)||seen.has(f)||(f==='u'&&seen.has('v'))||(f==='v'&&seen.has('u'))){offset=i;break}seen.add(f)}
  return {status:'InvalidSyntax',lastIndexAfter:null,matches:[],texts:null,error:{code:'InvalidSyntax',featureId:recipe.capabilities[0],location:offset<0?'Pattern':'Flags',offset:offset<0?0:offset,length:offset<0?p.length:1,offsetUnit:'Utf16CodeUnits',workConsumed:null,requiredCapacity:0}};
 }
 if(recipe.operation==='Parse')return {status:'ParseSuccess',lastIndexAfter:null,matches:[],texts:null,error:null};
 const last=numeric(recipe.lastIndexBefore);r.lastIndex=last;
 const dflags=flags.includes('d')?flags:flags+'d';let shadow=new RegExp(p,dflags);shadow.lastIndex=last;
 const result={status:'NoMatch',lastIndexAfter:null,matches:[],texts:null,error:null};
 const hasIndices=flags.includes('d');
 const op=recipe.operation;
 if(['Exec','Test','ExecNext','Boolean','Range','Captures'].includes(op)){
  const m=r.exec(s),indexed=shadow.exec(s);
  if(JSON.stringify(m)!==JSON.stringify(indexed)||JSON.stringify(namesSnapshot(m))!==JSON.stringify(namesSnapshot(indexed))||!Object.is(r.lastIndex,shadow.lastIndex))throw Error('d-shadow changes match/state/names');
  if(m){result.status='Match';if(op!=='Test'&&op!=='Boolean')result.matches=[match(indexed,p,hasIndices)]}
  if(op==='ExecNext'&&m&&m[0].length===0)r.lastIndex=advance(s,r.lastIndex,flags.includes('u')||flags.includes('v'));
 }else if(op==='Match'){
  const m=s.match(r);
  if(m){result.status='Match';if(flags.includes('g')){shadow.lastIndex=0;let i=0,x;while((x=shadow.exec(s))){if(i>=m.length||x[0]!==m[i++])throw Error('Match shadow mismatch');result.matches.push(match(x,p,false,true));if(x[0]==='')shadow.lastIndex=advance(s,shadow.lastIndex,flags.includes('u')||flags.includes('v'))}if(i!==m.length)throw Error('Match count mismatch')}
  else{shadow.lastIndex=last;result.matches=[match(shadow.exec(s),p,hasIndices)]}}
 }else if(op==='MatchAll'){
  if(!flags.includes('g'))return hostError(recipe,'GlobalFlagRequired');
  const m=[...s.matchAll(r)],indexed=[...s.matchAll(shadow)];
  if(JSON.stringify(m)!==JSON.stringify(indexed)||JSON.stringify(m.map(namesSnapshot))!==JSON.stringify(indexed.map(namesSnapshot)))throw Error('MatchAll shadow mismatch');
  result.status='Match';result.matches=indexed.map(x=>match(x,p,hasIndices));
 }else if(op==='Search'){
  const index=s.search(r);if(index>=0){result.status='Match';shadow.lastIndex=0;const m=shadow.exec(s);if(m.index!==index)throw Error('Search shadow mismatch');result.matches=[match(m,p,false,true)]}
 }else if(op==='Replace'||op==='ReplaceAll'){
  if(op==='ReplaceAll'&&!flags.includes('g'))return hostError(recipe,'GlobalFlagRequired');
  result.status='Match';result.texts=[toUnits(s[op==='Replace'?'replace':'replaceAll'](r,fromUnits(recipe.replacementUnits)))];
 }else if(op==='Split'){
  result.status='Match';result.texts=s.split(r,recipe.splitLimit===null?undefined:numeric(recipe.splitLimit)).map(x=>x===undefined?null:toUnits(x));
 }else throw Error('Unadmitted operation '+op);
 result.lastIndexAfter=numberToken(r.lastIndex);return result;
}
export function advance(s,index,unicode){if(!unicode||index+1>=s.length)return index+1;const a=s.charCodeAt(index),b=s.charCodeAt(index+1);return index+(a>=0xd800&&a<=0xdbff&&b>=0xdc00&&b<=0xdfff?2:1)}
function hostError(recipe,code){return {status:'HostArgumentError',lastIndexAfter:null,matches:[],texts:null,error:{code,featureId:recipe.capabilities[0],location:'Host',offset:0,length:0,offsetUnit:'Utf16CodeUnits',workConsumed:null,requiredCapacity:0}}}
