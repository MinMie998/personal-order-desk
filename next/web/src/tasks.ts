import {localDate} from './model';
export type WorkTask={id:string;parentId:string|null;title:string;status:string;plannedStart:string|null;dueDate:string|null;followUpDate:string|null;reminderDays:number;notes:string;waitingReason:string;dependsOn:string|null;createdAt:string;startedAt:string|null;completedAt:string|null};
export const taskStatuses:Record<string,string>={todo:'未开始',doing:'进行中',waiting:'等待中',done:'已完成',cancelled:'已取消'};
export function newTask(parentId:string|null=null):WorkTask{return {id:crypto.randomUUID(),parentId,title:'',status:'todo',plannedStart:null,dueDate:null,followUpDate:null,reminderDays:1,notes:'',waitingReason:'',dependsOn:null,createdAt:new Date().toISOString(),startedAt:null,completedAt:null}}
export function rollup(tasks:WorkTask[]):WorkTask[]{return tasks.map(t=>{const children=tasks.filter(c=>c.parentId===t.id);if(!children.length)return t;const active=children.filter(c=>c.status!=='cancelled');const status=!active.length?'cancelled':active.every(c=>c.status==='done')?'done':active.every(c=>c.status==='waiting')?'waiting':active.some(c=>c.status!=='todo')?'doing':'todo';return {...t,status,startedAt:children.map(c=>c.startedAt).filter((v):v is string=>!!v).sort()[0]||null,completedAt:status==='done'?active.map(c=>c.completedAt).filter((v):v is string=>!!v).sort().at(-1)||null:null}})}
export function taskDate(task:WorkTask,tasks:WorkTask[],field:'plannedStart'|'dueDate'):string|null{if(task[field])return task[field];const values=tasks.filter(t=>t.parentId===task.id&&t.status!=='cancelled').map(t=>t[field]).filter((v):v is string=>!!v).sort();return (field==='dueDate'?values.at(-1):values[0])||null}
export function taskNotice(task:WorkTask,tasks:WorkTask[]):{text:string;tone:string;date:string|null}{
 if(task.status==='done'||task.status==='cancelled')return {text:taskStatuses[task.status],tone:task.status==='done'?'green':'muted',date:null};
 const due=taskDate(task,tasks,'dueDate'),follow=task.status==='waiting'?task.followUpDate:null;
 const date=due&&follow?(due<follow?due:follow):due||follow;
 if(!date)return {text:task.status==='waiting'?'等待跟进':'未设日期',tone:task.status==='waiting'?'orange':'muted',date:null};
 const days=Math.round((Date.parse(date+'T12:00:00')-Date.parse(localDate()+'T12:00:00'))/86400000);
 const label=date===follow&&date!==due?'跟进':'截止';
 return {text:days<0?`${label}逾期 ${-days} 天`:days===0?`今天${label}`:`${days} 天后${label}`,tone:days<=0?'red':days<=task.reminderDays?'orange':'blue',date};
}
export function leaves(tasks:WorkTask[]){return tasks.filter(t=>t.status!=='cancelled'&&!tasks.some(c=>c.parentId===t.id))}
export function taskProgress(tasks:WorkTask[]){const items=leaves(tasks);return `${items.filter(t=>t.status==='done').length}/${items.length} 项完成`}
export function taskSummary(tasks:WorkTask[],orderStatus?:string){
 const items=leaves(tasks),doing=items.filter(t=>t.status==='doing'),waiting=items.filter(t=>t.status==='waiting'),todo=items.filter(t=>t.status==='todo');
 const names=(list:WorkTask[])=>list.slice(0,2).map(t=>t.title).join('、')+(list.length>2?`等 ${list.length} 项`:'');
 const blocked=(task:WorkTask)=>{const predecessor=tasks.find(t=>t.id===task.dependsOn);return !!predecessor&&!['done','cancelled'].includes(predecessor.status)};
 const available=todo.find(t=>!blocked(t)&&(!t.plannedStart||t.plannedStart<=localDate()));
 let progress=!items.length?'暂无有效任务':items.every(t=>t.status==='done')?'任务已全部完成，等待确认交付':doing.length?`正在做：${names(doing)}`:waiting.length?`等待：${names(waiting)}`:'任务尚未开始';
 if(doing.length&&waiting.length)progress+=`；另有 ${waiting.length} 项等待中`;
 const planned=todo.filter(t=>!blocked(t)&&t.plannedStart&&t.plannedStart>localDate()).sort((a,b)=>a.plannedStart!.localeCompare(b.plannedStart!))[0];
 const follow=[...waiting].sort((a,b)=>(a.followUpDate||'9999').localeCompare(b.followUpDate||'9999'))[0];
 const next=available?available.title:doing.length?`继续 ${names(doing)}`:planned?`${planned.plannedStart} 开始：${planned.title}`:follow?`跟进 ${follow.title}${follow.followUpDate?`（${follow.followUpDate}）`:''}`:todo.length?'先完成前置任务，再推进后续步骤':items.length?'确认交付或补充后续任务':'按需添加任务';
 if(orderStatus==='done')return {progress:'项目已完成交付',next:'按需跟进收款和物流',completed:items.filter(t=>t.status==='done').length,total:items.length};
 if(orderStatus==='cancelled')return {progress:'项目已取消，任务记录保留',next:'按需处理退款或后续沟通',completed:items.filter(t=>t.status==='done').length,total:items.length};
 return {progress,next,completed:items.filter(t=>t.status==='done').length,total:items.length};
}
export const templates:Record<string,{label:string;groups:[string,string[]][]}>={
 physical:{label:'实物制作',groups:[['需求与方案',['确认功能和接口','确认原理图与方案']],['采购与硬件制作',['采购元件','确认到货','焊接与检查']],['程序与整机调试',['完成程序','整机联调','连续运行测试']],['交付准备',['录制演示视频','整理源码与说明']]]},
 simulation:{label:'仿真交付',groups:[['设计与实现',['确认需求','搭建仿真与编写程序']],['调试与交付',['测试功能','整理工程与演示']]]},
 schematic:{label:'原理图 / PCB',groups:[['方案设计',['确认器件与接口','绘制原理图']],['PCB与交付',['布局布线','检查与导出文件']]]},
 code:{label:'程序 / 报告',groups:[['开发',['确认需求','编写程序']],['交付',['调试测试','整理报告与源码']]]}
};
export function templateTasks(kind:string){const result:WorkTask[]=[];for(const [title,children] of (templates[kind]||templates.physical).groups){const parent={...newTask(),title};result.push(parent);for(const child of children)result.push({...newTask(parent.id),title:child});}return result}
