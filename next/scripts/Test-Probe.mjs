import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {resolve} from 'node:path';
import {setTimeout as wait} from 'node:timers/promises';
import assert from 'node:assert/strict';
const token=randomUUID();
const child=spawn(resolve('next/rust/target/release/desk-server.exe'),[],{env:{...process.env,DESK_R0_SHUTDOWN_TOKEN:token},windowsHide:true,stdio:['ignore','pipe','pipe']});
let exited=false;let exitCode;let errors='';child.on('exit',code=>{exited=true;exitCode=code});child.stderr.on('data',b=>errors+=b);
const root='http://127.0.0.1:15059';
try{
 let health;
 for(let n=0;n<50;n++){if(exited)throw Error('Own probe exited: '+errors);try{health=await (await fetch(root+'/healthz')).json();break}catch{await wait(100)}}
 assert.equal(health?.stage,'R0');assert.equal(health?.dataAccess,false);
 assert.equal((await fetch(root+'/r0/shutdown',{method:'POST'})).status,401);
 assert.equal((await fetch(root+'/r0/shutdown',{method:'POST',headers:{'X-Desk-Control':'wrong'}})).status,401);
 assert.equal((await fetch(root+'/healthz')).status,200);
 assert.equal((await fetch(root+'/api/v3/orders',{method:'POST'})).status,404);
 assert.equal((await fetch(root+'/r0/shutdown',{method:'POST',headers:{'X-Desk-Control':token}})).status,202);
 for(let n=0;n<50&&!exited;n++)await wait(100);
 assert.equal(exited,true);assert.equal(exitCode,0);
 console.log('PASS: identity, disabled ledger writes, unauthorized shutdown rejected, authorized graceful shutdown.');
}finally{
 if(!exited){try{await fetch(root+'/r0/shutdown',{method:'POST',headers:{'X-Desk-Control':token}})}catch{}}
}
