use serde::{Deserialize,Serialize};
use std::collections::{BTreeMap,BTreeSet};
#[derive(Clone,Serialize,Deserialize)]
#[serde(rename_all="camelCase",deny_unknown_fields)]
pub struct WorkTask{
 pub id:String,pub parent_id:Option<String>,pub title:String,pub status:String,
 pub planned_start:Option<String>,pub due_date:Option<String>,pub follow_up_date:Option<String>,
 pub reminder_days:u32,pub notes:String,pub waiting_reason:String,pub depends_on:Option<String>,
 pub created_at:String,pub started_at:Option<String>,pub completed_at:Option<String>,
}
pub fn validate(tasks:&mut [WorkTask])->Result<(),String>{
 if tasks.len()>500{return Err("单笔订单最多500个任务".into())}
 let ids:BTreeSet<_>=tasks.iter().map(|t|t.id.clone()).collect();
 if ids.len()!=tasks.len(){return Err("任务标识重复".into())}
 let parents:BTreeMap<_,_>=tasks.iter().map(|t|(t.id.clone(),t.parent_id.clone())).collect();
 let dependencies:BTreeMap<_,_>=tasks.iter().map(|t|(t.id.clone(),t.depends_on.clone())).collect();
 for t in tasks.iter_mut(){
  uuid::Uuid::parse_str(&t.id).map_err(|_|"任务标识无效")?;t.title=t.title.trim().into();
  if t.title.is_empty()||t.title.len()>600||t.notes.len()>24000||t.waiting_reason.len()>3000||t.reminder_days>90{return Err("请填写任务名称，提醒天数应为0至90天".into())}
  if !["todo","doing","waiting","done","cancelled"].contains(&t.status.as_str()){return Err("任务状态无效".into())}
  for d in [&t.planned_start,&t.due_date,&t.follow_up_date].into_iter().flatten(){chrono::NaiveDate::parse_from_str(d,"%Y-%m-%d").map_err(|_|"任务日期格式无效")?;}
  if t.planned_start.as_ref().zip(t.due_date.as_ref()).is_some_and(|(a,b)|a>b){return Err("任务截止日期不能早于计划开始日期".into())}
  chrono::DateTime::parse_from_rfc3339(&t.created_at).map_err(|_|"任务创建时间无效")?;
  for d in [&t.started_at,&t.completed_at].into_iter().flatten(){chrono::DateTime::parse_from_rfc3339(d).map_err(|_|"任务实际时间无效")?;}
  if let Some(p)=&t.parent_id{if p==&t.id||!ids.contains(p)||parents.get(p).is_some_and(|v|v.is_some()){return Err("任务只支持两级，子任务必须关联当前订单的顶层任务".into())}}
  if let Some(p)=&t.depends_on{
   if !ids.contains(p){return Err("前置任务不存在".into())}
   let mut seen=BTreeSet::from([t.id.clone()]);let mut cursor=Some(p.clone());while let Some(id)=cursor{if !seen.insert(id.clone()){return Err("前置任务形成循环".into())}cursor=dependencies.get(&id).cloned().flatten();}
  }
  if t.status!="done"{t.completed_at=None}else if t.completed_at.is_none(){t.completed_at=Some(chrono::Local::now().to_rfc3339())}
  if ["doing","waiting","done"].contains(&t.status.as_str())&&t.started_at.is_none(){t.started_at=Some(chrono::Local::now().to_rfc3339())}
 }
 // Parent state derives from children; order completion remains an explicit user action.
 let original=tasks.to_vec();
 for task in tasks.iter_mut().filter(|t|t.parent_id.is_none()){
  let children:Vec<_>=original.iter().filter(|c|c.parent_id.as_deref()==Some(&task.id)).collect();
  if children.is_empty(){continue}
  let active:Vec<_>=children.iter().filter(|c|c.status!="cancelled").collect();
  task.status=if active.is_empty(){"cancelled"}else if active.iter().all(|c|c.status=="done"){"done"}else if active.iter().all(|c|c.status=="waiting"){"waiting"}else if active.iter().any(|c|c.status!="todo"){"doing"}else{"todo"}.into();
  task.started_at=children.iter().filter_map(|c|c.started_at.clone()).min_by_key(|s|chrono::DateTime::parse_from_rfc3339(s).ok());
  task.completed_at=if task.status=="done"{active.iter().filter_map(|c|c.completed_at.clone()).max_by_key(|s|chrono::DateTime::parse_from_rfc3339(s).ok())}else{None};
 }
 Ok(())
}
