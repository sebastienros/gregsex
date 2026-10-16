import fs from 'node:fs';import crypto from 'node:crypto';
export const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
export function identity(bytes,item){if(bytes.length!==item.length||sha(bytes)!==item.sha256)throw Error('Artifact identity mismatch')}
export function runtime(pin){
 identity(fs.readFileSync(fs.realpathSync(process.execPath)),pin);
 if(JSON.stringify(process.versions)!==JSON.stringify(pin.versions)||process.platform!==pin.platform||process.arch!==pin.architecture||process.env.NODE_OPTIONS)throw Error('Unqualified runtime/environment');
 for(const lib of pin.libraries)identity(fs.readFileSync(lib.real),lib);
}
