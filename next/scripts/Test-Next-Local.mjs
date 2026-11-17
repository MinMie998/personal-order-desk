// Synthetic HTTP integration only: random port + fresh project-owned temporary ledger.
// No production ledger, shared desktop service, credentials or GitHub access.
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,writeFileSync} from 'node:fs';
import {resolve,basename} from 'node:path';
import {createServer} from 'node:net';
import {setTimeout as wait} from 'node:timers/promises';
import assert from 'node:assert/strict';
const folder=resolve('tmp','next-local-final-'+randomUUID());mkdirSync(folder,{recursive:true});
const port=await new Promise((resolvePort,reject)=>{const socket=createServer();socket.once('error',reject);socket.listen(0,'127.0.0.1',()=>{const {port}=socket.address();socket.close(error=>error?reject(error):resolvePort(port));});});
const token=randomUUID(),base=`http://127.0.0.1:${port}`;let workspace;
const backend=process.argv[2]?resolve(process.argv[2]):resolve('next/rust/target/release/desk-server.exe');
const child=spawn(backend,[],{env:{...process.env,DESK_NEXT_DATA_DIR:resolve(folder,'book'),DESK_SERVER_PORT:String(port),DESK_R0_SHUTDOWN_TOKEN:token},windowsHide:true,stdio:'ignore'});
const checks=[];
async function call(path,body,expected=200){const r=await fetch(base+path,{method:body?'POST':'GET',headers:{'X-Desk-Control':token,'Content-Type':'application/json'},body:body?JSON.stringify(body):undefined,signal:AbortSignal.timeout(15000)});assert.equal(r.status,expected);return r.json();}
const act=(action,payload={},expected=200)=>call('/api/v3/action',{action,payload,workspaceId:workspace},expected);
const order=title=>({id:randomUUID(),number:'SYNTHETIC-'+title,title,kind:'physical',status:'done',acceptedDate:'2026-11-16',dueDate:null,completedDate:null,quoteCents:'10000',progress:'',nextStep:'',projectPath:'',notes:'合成隔离验证',trackingNumber:'',carrier:'',shippedDate:null,deliveredDate:null,parentId:null,revision:0,deletedAt:null,tasks:[],historicalDates:true});
try{
 let started=false;for(let n=0;n<100;n++){try{if((await fetch(base+'/healthz')).ok){started=true;break;}}catch{}await wait(50);}assert(started,'Isolated backend startup');
 const html=await (await fetch(base+'/')).text();assert(html.includes('id="app"'));const script=html.match(/src="([^"]+\.js)"/);assert(script,'Built frontend entry');assert.equal((await fetch(base+script[1])).status,200);checks.push('packaged Web entry and JavaScript asset served');
 assert.equal((await call('/api/v3/capabilities')).syncRules,2);await act('create-workspace');let snapshot=await call('/api/v3/snapshot');workspace=snapshot.workspaceId;
 const first=await act('save-order',order('ONE'));const second=await act('save-order',order('TWO'));assert.equal(first.completedDate,null);assert.equal(first.dueDate,null);checks.push('historical unknown dates preserved through HTTP');
 const receipt={id:randomUUID(),orderId:first.id,kind:'receipt',amountCents:'7000',date:null,method:'合成方式',note:'',voidedAt:null,voidReason:null};
 await act('add-money',receipt);await act('add-money',receipt);await act('add-money',{...receipt,id:randomUUID(),kind:'cost',amountCents:'3500'});snapshot=await call('/api/v3/snapshot');assert.equal(snapshot.money.length,2);checks.push('receipt retries idempotent and optional dates roundtrip');
 const backup=await act('backup');const {digest}=await act('ledger-digest');const generation=randomUUID();
 const source={format:2,generation,workspaceId:workspace,orders:{[first.id]:{...first,title:'REMOTE-SYNTHETIC',revision:0}},money:{},history:{},preferences:snapshot.preferences};
 const replaced=await act('replace-ledger',{confirmReplace:true,expectedDigest:digest,snapshot:source});assert.equal(replaced.replaced,true);snapshot=await call('/api/v3/snapshot');assert.equal(snapshot.orders.length,1);assert.equal(snapshot.orders[0].title,'REMOTE-SYNTHETIC');assert(snapshot.orders[0].revision>first.revision);assert.equal(snapshot.money.length,0);checks.push('whole ledger replacement removes obsolete projects and money atomically');
 await act('save-order',first,400);await act('save-order',second,400);await act('replace-ledger',{confirmReplace:true,expectedDigest:digest,snapshot:source},400);checks.push('stale editor and stale replacement digest rejected');
 await act('restore-backup',{name:basename(backup.path)});snapshot=await call('/api/v3/snapshot');assert.equal(snapshot.orders.length,2);assert.equal(snapshot.money.length,2);assert.equal(snapshot.money.find(m=>m.id===receipt.id).amountCents,'7000');checks.push('protected backup restored projects and exact-cent money');
 assert.equal((await act('sync-status')).configured,false);await act('sync-prepare',{mode:'reset'},400);checks.push('unconfigured synchronization refuses publish without contacting GitHub');
 const report={passed:true,syntheticOnly:true,checks,fixture:folder};writeFileSync(resolve(folder,'report.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report,null,2));
}finally{
 try{await fetch(base+'/r0/shutdown',{method:'POST',headers:{'X-Desk-Control':token},signal:AbortSignal.timeout(5000)});}catch{}
 if(child.exitCode===null)await Promise.race([new Promise(resolveExit=>child.once('exit',resolveExit)),wait(5000)]);
 if(child.exitCode===null)throw Error('Isolated verification backend did not exit normally');
}
