<script setup lang="ts">
import {computed,ref,watch,onUnmounted,nextTick} from 'vue';
import Icon from './Icon.vue';
import {type WorkTask,newTask,rollup,taskDate,taskNotice,taskProgress,taskSummary,taskStatuses,templates,templateTasks} from './tasks';
const props=defineProps<{modelValue:WorkTask[];kind:string;orderDue:string|null;orderStatus?:string;disabled?:boolean}>();
const emit=defineEmits<{(e:'update:modelValue',value:WorkTask[]):void;(e:'editing',value:boolean):void;(e:'complete'):void}>();
const editPanel=ref<HTMLElement|null>(null);
const draft=ref<WorkTask|null>(null),problem=ref(''),template=ref(templates[props.kind]?props.kind:'physical');
const folded=ref<string[]>([]),filter=ref('all');
const roots=computed(()=>props.modelValue.filter(t=>!t.parentId));
const summary=computed(()=>taskSummary(props.modelValue,props.orderStatus));
const completion=computed(()=>summary.value.total?Math.round(summary.value.completed/summary.value.total*100):0);
const children=(id:string)=>props.modelValue.filter(t=>t.parentId===id);
const hasChildren=(id:string)=>children(id).length>0;
function write(tasks:WorkTask[]){emit('update:modelValue',rollup(tasks))}
function edit(t?:WorkTask,parent:string|null=null){if(props.disabled)return false;if(draft.value&&!window.confirm('放弃当前尚未应用的任务编辑？'))return false;draft.value=JSON.parse(JSON.stringify(t||newTask(parent)));problem.value='';void nextTick(()=>editPanel.value?.scrollIntoView({block:'start',behavior:'smooth'}));return true;}
watch(()=>!!draft.value,value=>emit('editing',value));onUnmounted(()=>emit('editing',false));
function state(task:WorkTask,status:string){if(props.disabled)return;if(draft.value?.id===task.id){problem.value='请先应用或取消当前任务编辑，再切换状态';void nextTick(()=>editPanel.value?.scrollIntoView({block:'start',behavior:'smooth'}));return;}const before=props.modelValue.find(t=>t.id===task.dependsOn);if(['doing','done'].includes(status)&&before&&!['done','cancelled'].includes(before.status)&&!window.confirm(`前置任务“${before.title}”尚未完成，仍要${status==='done'?'完成':'开始'}此任务？`))return;const now=new Date().toISOString();write(props.modelValue.map(t=>t.id===task.id?{...t,status,startedAt:['doing','waiting','done'].includes(status)?t.startedAt||now:t.startedAt,completedAt:status==='done'?now:null}:t))}
function wait(task:WorkTask){if(edit(task)&&draft.value){draft.value.status='waiting';draft.value.followUpDate ||=task.dueDate;}}
function foldCompleted(){folded.value=[...new Set([...folded.value,...roots.value.filter(t=>['done','cancelled'].includes(t.status)).map(t=>t.id)])]}
function focusStage(task:WorkTask){filter.value='all';folded.value=folded.value.filter(id=>id!==task.id);void nextTick(()=>document.getElementById(`stage-${task.id}`)?.scrollIntoView({block:'start',behavior:'smooth'}))}
function save(){if(!draft.value)return;const d={...draft.value};
 if(!d.title.trim()){problem.value='请填写任务名称';return}
 if(d.plannedStart&&d.dueDate&&d.plannedStart>d.dueDate){problem.value='截止日期不能早于开始日期';return}
 if(!Number.isInteger(d.reminderDays)||d.reminderDays<0||d.reminderDays>90){problem.value='提醒天数应为0至90天';return}
 const now=new Date().toISOString();if(['doing','waiting','done'].includes(d.status))d.startedAt ||=now;d.completedAt=d.status==='done'?d.completedAt||now:null;
 for(const key of ['plannedStart','dueDate','followUpDate','dependsOn','parentId'] as const)if(!d[key])d[key]=null;
 const list=props.modelValue.some(t=>t.id===d.id)?props.modelValue.map(t=>t.id===d.id?d:t):[...props.modelValue,d];write(list);draft.value=null;
}
function move(t:WorkTask,direction:number){const siblings=props.modelValue.filter(x=>x.parentId===t.parentId);const i=siblings.findIndex(x=>x.id===t.id),other=siblings[i+direction];if(!other)return;const list=[...props.modelValue],a=list.findIndex(x=>x.id===t.id),b=list.findIndex(x=>x.id===other.id);[list[a],list[b]]=[list[b],list[a]];write(list)}
function matches(t:WorkTask){if(filter.value==='all')return true;if(filter.value==='active')return !['done','cancelled'].includes(t.status);return ['red','orange'].includes(taskNotice(t,props.modelValue).tone)}
function visible(root:WorkTask){return matches(root)||children(root.id).some(matches)}
function toggleFold(id:string){folded.value=folded.value.includes(id)?folded.value.filter(x=>x!==id):[...folded.value,id]}
const warnings=computed(()=>props.modelValue.flatMap(t=>{
 const result:string[]=[];const due=taskDate(t,props.modelValue,'dueDate');if(due&&props.orderDue&&due>props.orderDue&&t.status!=='cancelled')result.push(`“${t.title}”的截止日期晚于订单交付日期`);
 const parent=props.modelValue.find(p=>p.id===t.parentId);if(parent?.dueDate&&due&&due>parent.dueDate)result.push(`“${t.title}”晚于父任务截止日期`);
 const before=props.modelValue.find(p=>p.id===t.dependsOn);const beforeDue=before?taskDate(before,props.modelValue,'dueDate'):null;if(beforeDue&&t.plannedStart&&beforeDue>t.plannedStart)result.push(`“${t.title}”的开始日期早于前置任务结束日期`);return result;
}));
function validPredecessor(candidate:WorkTask){if(!draft.value||candidate.id===draft.value.id)return false;const seen=new Set([draft.value.id]);let cursor:WorkTask|undefined=candidate;while(cursor){if(seen.has(cursor.id))return false;seen.add(cursor.id);cursor=props.modelValue.find(t=>t.id===cursor?.dependsOn)}return true}
const formatTime=(v:string|null)=>v?new Date(v).toLocaleString('zh-CN'):'未记录';
</script>
<template>
 <div class="task-board">
  <div class="task-board-heading"><div><h3>制作任务 <span class="badge blue">{{taskProgress(modelValue)}}</span></h3><p>按步骤推进；调整后点击底部“保存项目”保存。</p></div><button type="button" :disabled="disabled" @click="edit()"><Icon name="plus"/>添加任务</button></div>
  <div v-if="modelValue.length" class="task-progress-overview"><div class="task-progress-caption"><strong>整体制作进度</strong><span>{{summary.completed}} / {{summary.total}} 项 · {{completion}}%</span></div><progress :value="summary.completed" :max="summary.total||1" :aria-label="`已完成 ${summary.completed} 项，共 ${summary.total} 项`"/><nav class="task-stage-flow" aria-label="制作阶段"><button v-for="(stage,index) in roots" :key="stage.id" type="button" :class="{finished:stage.status==='done',current:stage.status==='doing',waiting:stage.status==='waiting',cancelled:stage.status==='cancelled'}" @click="focusStage(stage)"><span class="stage-index">{{stage.status==='done'?'✓':index+1}}</span><span><strong>{{stage.title}}</strong><small>{{hasChildren(stage.id)?taskProgress(children(stage.id)):taskStatuses[stage.status]}}</small></span></button></nav></div>
  <div v-if="modelValue.length" class="task-flow-summary"><p><span>当前进度</span><strong>{{summary.progress}}</strong></p><p><span>下一步</span><strong>{{summary.next}}</strong></p></div>
  <div v-if="summary.total&&summary.completed===summary.total&&!['done','cancelled'].includes(orderStatus||'')" class="task-handoff"><div><strong>制作步骤已全部完成</strong><p>确认已交付后，可将订单标记为完成；收款和物流仍可继续记录。</p></div><button type="button" class="primary" :disabled="disabled||!!draft" @click="emit('complete')">确认项目完成</button><small>点击后仍需保存项目</small></div>
  <div class="task-board-body" :class="{'has-task-editor':!!draft}"><div class="task-board-list">
  <div v-if="!modelValue.length" class="task-template"><h3>从常用流程开始</h3><p>模板只添加步骤，日期由你安排；不会修改订单截止日期。</p><div class="row-actions"><select v-model="template" aria-label="任务模板"><option v-for="(value,key) in templates" :key="key" :value="key">{{value.label}}</option></select><button type="button" :disabled="disabled" @click="write(templateTasks(template))">套用流程模板</button></div></div>
  <div v-if="modelValue.length" class="task-view-switch"><button v-for="(label,key) in {all:'全部',active:'未完成',due:'需要关注'}" :key="key" type="button" :class="{selected:filter===key}" @click="filter=key">{{label}}</button><button type="button" class="text-button" @click="foldCompleted">折叠已完成阶段</button><button v-if="folded.length" type="button" class="text-button" @click="folded=[]">展开全部</button><small>父任务随子任务汇总，订单由你确认完成。</small></div>
  <div v-if="warnings.length" class="task-warnings"><strong>日期需要留意</strong><p v-for="warning in warnings" :key="warning">{{warning}}</p></div>
  <article v-for="root in roots.filter(visible)" :id="`stage-${root.id}`" :key="root.id" class="task-group">
   <div v-for="task in [root,...(folded.includes(root.id)?[]:children(root.id).filter(matches))]" :key="task.id" class="task-item" :class="{'task-child':!!task.parentId,'task-completed':task.status==='done','task-cancelled':task.status==='cancelled'}">
    <div class="task-item-main"><button v-if="hasChildren(task.id)" type="button" class="task-fold" :aria-expanded="!folded.includes(task.id)" @click="toggleFold(task.id)">{{folded.includes(task.id)?'▸':'▾'}}</button><input v-else type="checkbox" :checked="task.status==='done'" :disabled="disabled||task.status==='cancelled'" :aria-label="`完成任务 ${task.title}`" @change="state(task,task.status==='done'?'todo':'done')"/>
     <button type="button" class="task-name" @click="edit(task)"><strong>{{task.title}}</strong><small v-if="hasChildren(task.id)">{{taskProgress(children(task.id))}}</small><small v-else>{{taskStatuses[task.status]}}</small></button><span class="badge" :class="taskNotice(task,modelValue).tone">{{taskNotice(task,modelValue).text}}</span>
    </div>
    <div class="task-item-detail"><span v-if="taskDate(task,modelValue,'plannedStart')">开始 {{taskDate(task,modelValue,'plannedStart')}}</span><span v-if="taskDate(task,modelValue,'dueDate')">截止 {{taskDate(task,modelValue,'dueDate')}}</span><span v-if="task.completedAt" class="green">完成 {{formatTime(task.completedAt)}}</span><p v-if="task.notes">{{task.notes}}</p><p v-if="task.status==='waiting'" class="orange">等待：{{task.waitingReason||'原因待补充'}}<span v-if="task.followUpDate"> · {{task.followUpDate}} 跟进</span></p><p v-if="task.dependsOn" class="muted">前置：{{modelValue.find(t=>t.id===task.dependsOn)?.title||'待核对'}}</p></div>
    <div class="task-item-actions"><button v-if="!hasChildren(task.id)&&['todo','waiting'].includes(task.status)" type="button" class="text-button" :disabled="disabled" @click="state(task,'doing')">{{task.status==='waiting'?'继续做':'开始做'}}</button><button v-if="!hasChildren(task.id)&&['todo','doing'].includes(task.status)" type="button" class="text-button" :disabled="disabled" @click="wait(task)">设为等待</button><button type="button" class="text-button" :disabled="disabled" @click="edit(task)">编辑</button><button v-if="!task.parentId" type="button" class="text-button" :disabled="disabled" @click="edit(undefined,task.id)">＋子任务</button><button type="button" class="task-sort" :disabled="disabled" @click="move(task,-1)" aria-label="上移任务">↑</button><button type="button" class="task-sort" :disabled="disabled" @click="move(task,1)" aria-label="下移任务">↓</button></div>
   </div>
  </article>
  <p v-if="modelValue.length&&!roots.some(visible)" class="muted">当前筛选下没有任务。</p>
  </div>
  <div v-if="draft" ref="editPanel" class="task-edit-panel"><div class="task-board-heading"><h3>{{modelValue.some(t=>t.id===draft!.id)?'编辑任务':draft.parentId?'新增子任务':'新增任务'}}</h3><button type="button" class="icon-button" @click="draft=null" aria-label="关闭任务编辑"><Icon name="close"/></button></div>
   <div class="form-grid"><label class="full">任务名称<input v-model="draft.title" maxlength="200" placeholder="例如：焊接主板并检查短路"/></label><label>父任务<select v-model="draft.parentId" :disabled="hasChildren(draft.id)"><option :value="null">顶层任务</option><option v-for="t in roots.filter(t=>t.id!==draft!.id)" :key="t.id" :value="t.id">{{t.title}}</option></select></label><label>状态<select v-model="draft.status" :disabled="hasChildren(draft.id)"><option v-for="(label,key) in taskStatuses" :key="key" :value="key">{{label}}</option></select><small v-if="hasChildren(draft.id)">由子任务汇总</small></label>
    <label>计划开始日期<input v-model="draft.plannedStart" type="date"/></label><label>截止日期<input v-model="draft.dueDate" type="date"/><small v-if="hasChildren(draft.id)">留空时汇总子任务日期</small></label><label>提前提醒（天）<input v-model.number="draft.reminderDays" type="number" min="0" max="90"/></label><label>前置任务<select v-model="draft.dependsOn"><option :value="null">不设置</option><option v-for="t in modelValue.filter(validPredecessor)" :key="t.id" :value="t.id">{{t.title}}</option></select></label>
    <template v-if="draft.status==='waiting'"><label>等待原因<input v-model="draft.waitingReason" placeholder="例如：等待元件到货"/></label><label>跟进日期<input v-model="draft.followUpDate" type="date"/></label></template><label class="full">当前进度 / 备注<textarea v-model="draft.notes" rows="2" placeholder="已经做到哪里，接下来还缺什么…"/></label>
    <div class="full task-time-records">创建 {{formatTime(draft.createdAt)}}<br/>实际开始 {{formatTime(draft.startedAt)}}<br/>实际完成 {{formatTime(draft.completedAt)}}</div>
   </div><p v-if="problem" class="error" role="alert">{{problem}}</p><div class="row-actions"><button type="button" :disabled="disabled" class="primary" @click="save">应用到项目</button><button type="button" @click="draft=null">取消</button><small>应用后记得保存项目。取消任务可保留历史，无需删除。</small></div>
  </div></div>
 </div>
</template>
