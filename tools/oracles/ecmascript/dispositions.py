"""Exact per-file edition and product-boundary disposition; never suppress a run."""
import json,pathlib,re
ROOT=pathlib.Path(__file__).resolve().parents[3];DIR=ROOT/'test/corpora/ecmascript'
intake=json.load(open(DIR/'test262-intake.json'));report=json.load(open(ROOT/'test/corpora/ecmascript/oracle/test262-recheck-results.json'))
failed={x['path'] for x in report['results'] if x['outcome']=='failed'}
rules=[
 ('static-accessor-proposal',r'^test/annexB/built-ins/RegExp/legacy-accessors/', 'Pending legacy-regexp proposal/static JS constructor properties, not Annex B.1.2 regex grammar. Typed matcher has no RegExp constructor static property object.'),
 ('compile-object',r'^test/annexB/built-ins/RegExp/prototype/compile/', 'RegExp.compile and JS realm/subclass objects explicitly excluded by ECMAScript host contract.'),
 ('replace-property-override',r'^test/built-ins/RegExp/prototype/Symbol.replace/(coerce-global|flags-tostring-error|get-flags-err|get-unicode-error)\.js$', 'Overrides/accessors/coercions and direct Symbol dispatch explicitly excluded; builtin matching and string templates remain required.'),
 ('primitive-symbol-dispatch',r'^test/built-ins/String/prototype/(match|matchAll|search|replace|replaceAll|split)/cstm-.*-on-.*-primitive\.js$', 'Primitive coercion and overridden Symbol accessors explicitly excluded. Typed string + GregsEx pattern operations do not expose JS prototype objects.'),
 ('staging-replace-trace',r'^test/staging/sm/RegExp/replace-trace\.js$', 'Nonnormative staging pending test observes Proxy/property override/Symbol/replacer callback order; excluded JS object surface. No regex result is quarantined.')]
dispositions=[]
for f in intake['files']:
 m=f['metadata']; edition='nonnormative-pending-or-staging' if '/staging/' in f['path'] or m.get('esid')=='pending' or 'legacy-regexp' in m.get('features',[]) else 'edition16-or-earlier-assertion'
 rule=next((x for x in rules if re.search(x[1],f['path'])),None)
 if f['path'] in failed and rule is None:raise RuntimeError('Unexplained required failure: '+f['path'])
 dispositions.append({'path':f['path'],'sha256':f['sha256'],'editionDisposition':edition,'originalClause':m.get('esid',m.get('es6id',m.get('es5id','metadata absent'))),'productDisposition':'excluded-javascript-object-boundary' if rule else 'retained-oracle-assertions-for-required-semantic-audit','rule':rule[0] if rule else None,'reason':rule[2] if rule else 'Original suite run retained; JS object assertions never confer product coverage. Required grammar/matching/typed operation cases remain in portable corpus, imports and integration obligations.','failedVariants':sum(x['path']==f['path'] and x['outcome']=='failed' for x in report['results'])})
(DIR/'test262-dispositions.json').write_text(json.dumps({'schemaVersion':'test262-dispositions-1','commit':intake['commit'],'basis':'Existing ECMAScript contract excludes JS objects/coercions/overrides/Symbol dispatch/RegExp.compile/replacer callbacks; no new regex-syntax exclusion. Pending/staging assertions are not normative edition16 requirements. Original and recheck failures are preserved.','rules':[{'id':a,'pattern':b,'reason':c} for a,b,c in rules],'files':dispositions},indent=2)+'\n')
print('Disposed',len(dispositions),'files;',len(failed),'failed files;',sum(x['failedVariants'] for x in dispositions),'failed variants; required semantic failures unexplained:0')
