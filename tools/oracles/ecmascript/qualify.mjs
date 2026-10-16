// Fixed-input executable and capability qualification. No expectation generation.
import fs from 'node:fs';
import crypto from 'node:crypto';
import {execFileSync} from 'node:child_process';
const root=new URL('../../../',import.meta.url);
const report=new URL('test/corpora/ecmascript/oracle/',root);
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
const binary=fs.realpathSync(process.execPath);
const checks=[
 ['scoped-modifiers',()=>new RegExp('(?i:a)(?-i:b)').test('Ab')&&!new RegExp('(?i:a)(?-i:b)').test('AB')],
 ['duplicate-disjoint-names',()=>new RegExp('(?<x>a)|(?<x>b)','d').exec('b').indices.groups.x[1]===1],
 ['v-subtraction',()=>new RegExp('[[a-z]--[aeiou]]+','v').exec('aeibcd').index===3],
 ['v-string-properties',()=>['Basic_Emoji','Emoji_Keycap_Sequence','RGI_Emoji_Modifier_Sequence','RGI_Emoji_Flag_Sequence','RGI_Emoji_Tag_Sequence','RGI_Emoji_ZWJ_Sequence','RGI_Emoji'].every(n=>new RegExp(`\\p{${n}}`,'v'))],
 ['u-v-complement-fold',()=>new RegExp('\\P{Lowercase_Letter}','iu').test('a')&&!new RegExp('\\P{Lowercase_Letter}','iv').test('a')],
 ['reverse-lookbehind',()=>new RegExp('(?<=([ab]+)([bc]+))$','d').exec('abc')[1]==='a'],
 ['annex-b',()=>new RegExp('\\141').test('a')&&new RegExp('(?=a)?a').test('a')],
 ['lone-surrogate',()=>new RegExp('^.$','u').test('\ud800')],
 ['interior-surrogate-lastIndex',()=>{let r=new RegExp('.','uy');r.lastIndex=1;let m=r.exec('😀');return m.index===0&&m[0].length===2&&r.lastIndex===2}],
 ['unicode16-new-character',()=>new RegExp('\\p{Script=Todhri}','u').test('\u{105c0}')],
 ['all-host-methods',()=>['match','matchAll','search','replace','replaceAll','split'].every(n=>typeof String.prototype[n]==='function')],
 ['all-eight-flags',()=>new RegExp('', 'dgimsuy')&&new RegExp('', 'dgimsvy')]
].map(([id,f])=>{try{return {id,passed:!!f()}}catch(e){return {id,passed:false,error:String(e)}}});
const config=JSON.stringify(process.config,null,2)+'\n';
fs.writeFileSync(new URL('node-config.json',report),config);
for(const file of ['INSTALL_RECEIPT.json','LICENSE']) fs.copyFileSync(`/opt/homebrew/Cellar/node/24.4.1/${file}`,new URL('node-'+file,report));
const links=execFileSync('/usr/bin/otool',['-L',binary],{encoding:'utf8'});
const libraries=[];
for(const line of links.split('\n').slice(1)) {
 const path=line.trim().split(' (')[0];
 if(fs.existsSync(path)) {const real=fs.realpathSync(path), b=fs.readFileSync(real);libraries.push({path,real,length:b.length,sha256:sha(b)})}
}
const pin={schemaVersion:'ecma-oracle-pin-1',oracleId:'node-24.4.1-v8-13.6.233.10-node.17-unicode16-darwin-arm64',
 binary,length:fs.statSync(binary).size,sha256:sha(fs.readFileSync(binary)),versions:process.versions,
 platform:process.platform,architecture:process.arch,release:process.release,
 host:execFileSync('/usr/bin/uname',['-a'],{encoding:'utf8'}).trim(),
 buildConfig:{path:'test/corpora/ecmascript/oracle/node-config.json',length:Buffer.byteLength(config),sha256:sha(config)},
 buildReceipt:{path:'test/corpora/ecmascript/oracle/node-INSTALL_RECEIPT.json',sha256:sha(fs.readFileSync(new URL('node-INSTALL_RECEIPT.json',report)))},
 license:{path:'test/corpora/ecmascript/oracle/node-LICENSE',sha256:sha(fs.readFileSync(new URL('node-LICENSE',report)))},
 libraries,linkedLibraries:links,invocation:'env -u NODE_OPTIONS /opt/homebrew/bin/node --experimental-vm-modules tools/oracles/ecmascript/test262.mjs',
 unicodePolicy:'Node reports Unicode16.0/ICU77.1. Exact executable and ICU library hashes pin compiled tables; no runtime Unicode substitution in GregsEx.',checks};
fs.writeFileSync(new URL('test/corpora/ecmascript/oracle-pin.json',root),JSON.stringify(pin,null,2)+'\n');
console.log(JSON.stringify({oracleId:pin.oracleId,binarySha256:pin.sha256,checks}));
if(checks.some(x=>!x.passed))process.exitCode=1;
