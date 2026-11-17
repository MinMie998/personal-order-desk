// UI presentation only. Refuses every directory except this project's named preview ledger.
import {resolve} from 'node:path';
import {randomUUID} from 'node:crypto';
const base='http://127.0.0.1:15059';
const headers={'X-Desk-Request':'1','Sec-Fetch-Site':'same-origin','Origin':base,'Content-Type':'application/json'};
async function request(path,body){const response=await fetch(base+path,{headers,method:body?'POST':'GET',body:body?JSON.stringify(body):undefined});const value=await response.json();if(!response.ok)throw Error(value.error||'Preview request failed');return value;}
const workspace=await request('/api/v3/workspace');
if(resolve(workspace.dataDirectory).toLowerCase()!==resolve('tmp/界面预览账本').toLowerCase())throw Error('Not the named preview directory. No data written.');
let workspaceId=null;
async function action(name,payload={}){return request('/api/v3/action',{action:name,payload,workspaceId});}
if(!workspace.initialized)await action('create-workspace');
const snapshot=await request('/api/v3/snapshot');workspaceId=snapshot.workspaceId;
if(!snapshot.previewMode)throw Error('Preview mode is not enabled. No sample records written.');
if(snapshot.orders.length){console.log('Existing preview ledger reused; no duplicate samples.');process.exit(0);}
function day(offset=0){const d=new Date();d.setDate(d.getDate()+offset);return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;}
function order(number,title,kind,status,quote,due,extra={}){return{id:randomUUID(),number,title,kind,status,acceptedDate:day(-2),dueDate:day(due),completedDate:status==='done'?day(-1):null,quoteCents:String(quote),progress:'',nextStep:'',projectPath:'',notes:'仅用于界面展示的虚构订单',trackingNumber:'',carrier:'',shippedDate:null,deliveredDate:null,parentId:null,revision:0,deletedAt:null,...extra};}
const items=[
 order('演示-001','温湿度采集与显示实物','physical','doing',80000,2,{progress:'程序已完成，正在焊接电路',nextStep:'通电调试，录制演示视频'}),
 order('演示-002','流水灯与数码管仿真','simulation','doing',15000,0,{progress:'仿真已跑通，正在整理说明',nextStep:'补齐说明后交付'}),
 order('演示-003','串口通讯仿真','simulation','done',30000,-1,{progress:'已交付工程与演示视频'}),
 order('演示-004','电源控制原理图与 PCB','schematic','waiting',20000,5,{progress:'初版原理图已完成',nextStep:'等待确认接口定义'}),
 order('演示-005','数据采集程序与报告','code','todo',12000,7,{nextStep:'整理需求，确定采样频率'}),
 order('演示-006','控制模块实物制作','physical','done',68000,1,{progress:'已完成制作并寄出',carrier:'顺丰',trackingNumber:'演示单号-不可查物流',shippedDate:day(-1)}),
];
items.push(order('演示-004','追加：指示灯与接口调整','schematic','todo',8000,3,{parentId:items[3].id,nextStep:'按原单继续调整接口'}));
for(const item of items)await action('save-order',item);
for(const [index,kind,amount,date] of [[0,'receipt',50000,day(-1)],[0,'cost',18000,null],[2,'receipt',30000,day(-1)],[2,'cost',4000,null],[3,'receipt',10000,day(-2)],[5,'receipt',68000,day(-1)],[5,'cost',26000,day(-2)]]){
 await action('add-money',{id:randomUUID(),orderId:items[index].id,kind,amountCents:String(amount),date,method:kind==='receipt'?'微信':'支付宝',note:'虚构演示流水',voidedAt:null,voidReason:null});
}
console.log('Preview presentation prepared: 7 fictional orders; production ledger untouched.');
