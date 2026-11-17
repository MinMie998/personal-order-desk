package com.personalorderdesk.nativepreview

import org.json.JSONObject
import org.json.JSONArray
import java.time.OffsetDateTime
import java.time.LocalDate
import java.util.UUID
import java.io.File

data class PhoneArchive(val workspaceId:String,val orders:List<WorkOrder>,val money:List<WorkMoney>,val history:List<WorkHistory>,val generation:String="legacy")
fun WorkbenchDatabase.archive(workspaceId:String):String{
 var result=""
 runInTransaction{
  val data=JSONObject().put("format","personal-order-desk-android-backup").put("version",2).put("workspaceId",dao().workspace()?.workspaceId?:workspaceId).put("generation",dao().workspace()?.generation?:"legacy").put("createdAt",OffsetDateTime.now().toString())
  data.put("orders",JSONArray().apply{dao().allOrders().forEach{put(JSONObject(it.wire()))}})
  data.put("money",JSONArray().apply{dao().allMoney().forEach{put(JSONObject(it.wire()))}})
  data.put("history",JSONArray().apply{dao().allHistory().forEach{put(JSONObject().put("id",it.id.toString()).put("stableId",it.stableId).put("entityId",it.entityId).put("action",it.action).put("at",it.at).put("payload",it.payload))}})
  result=data.toString(2)
 };return result
}
private fun JSONObject.optional(key:String):String?=if(isNull(key))null else getString(key)
internal fun decodeWireOrder(v:JSONObject):WorkOrder = WorkOrder(id=v.getString("id"),number=v.getString("number"),title=v.getString("title"),kind=v.getString("kind"),status=v.getString("status"),acceptedDate=v.getString("acceptedDate"),dueDate=v.optional("dueDate"),completedDate=v.optional("completedDate"),quoteCents=parseCents(v.getString("quoteCents")),progress=v.getString("progress"),nextStep=v.getString("nextStep"),projectPath=v.getString("projectPath"),notes=v.getString("notes"),trackingNumber=v.getString("trackingNumber"),carrier=v.getString("carrier"),shippedDate=v.optional("shippedDate"),deliveredDate=v.optional("deliveredDate"),parentId=v.optional("parentId"),revision=v.getLong("revision"),deletedAt=v.optional("deletedAt"),tasks=if(v.has("tasks"))TaskConverters().decode(v.getJSONArray("tasks").toString())else emptyList(),historicalDates=v.optBoolean("historicalDates",v.isNull("dueDate")||v.getString("status")=="done"&&v.isNull("completedDate")))
fun readPhoneArchive(content:String):PhoneArchive{
 val root=JSONObject(content);require(root.getString("format")=="personal-order-desk-android-backup"&&root.getInt("version") in listOf(1,2)){"这不是兼容的手机工作台备份"}
 val workspace=root.getString("workspaceId");UUID.fromString(workspace)
 val orders=root.getJSONArray("orders").let{items->(0 until items.length()).map{index->val v=items.getJSONObject(index)
  decodeWireOrder(v)
 }}
 val entries=root.getJSONArray("money").let{items->(0 until items.length()).map{index->val v=items.getJSONObject(index)
  WorkMoney(id=v.getString("id"),orderId=v.getString("orderId"),kind=v.getString("kind"),amountCents=parseCents(v.getString("amountCents")),date=v.optional("date"),method=v.getString("method"),note=v.getString("note"),voidedAt=v.optional("voidedAt"),voidReason=v.optional("voidReason"))
 }}
 val history=root.getJSONArray("history").let{items->(0 until items.length()).map{index->val v=items.getJSONObject(index);WorkHistory(id=v.getString("id").toLong(),entityId=v.getString("entityId"),action=v.getString("action"),at=v.getString("at"),payload=v.getString("payload"),stableId=v.optString("stableId",UUID.nameUUIDFromBytes("$workspace:history:${v.getString("id")}".toByteArray(Charsets.UTF_8)).toString()))}}
 require(orders.map{it.id}.distinct().size==orders.size&&entries.map{it.id}.distinct().size==entries.size&&history.map{it.id}.distinct().size==history.size){"备份存在重复记录标识"}
 val ids=orders.map{it.id}.toSet()
 orders.forEach{normalizeTasks(it.tasks);UUID.fromString(it.id);require(it.number.isNotBlank()&&it.title.isNotBlank()&&it.quoteCents>=0&&it.revision>=0&&workKinds.containsKey(it.kind)&&workStatuses.containsKey(it.status)){"备份项目内容不完整"};LocalDate.parse(it.acceptedDate);listOf(it.dueDate,it.completedDate,it.shippedDate,it.deliveredDate).filterNotNull().forEach{d->LocalDate.parse(d)};require(it.parentId==null||it.parentId in ids){"备份追加单关联缺失"}}
 entries.forEach{UUID.fromString(it.id);require(it.orderId in ids&&it.amountCents>0&&moneyKinds.containsKey(it.kind)){"备份流水金额或关联无效"};it.date?.let{d->LocalDate.parse(d)}}
 require(history.map{it.stableId}.distinct().size==history.size){"备份历史稳定标识重复"}
 history.forEach{require(Regex("[A-Za-z0-9._:-]{1,100}").matches(it.stableId)){"备份历史稳定标识无效"}}
 require(history.all{it.id>0}){"备份历史标识无效"}
 val generation=root.optString("generation","legacy");if(generation!="legacy")UUID.fromString(generation)
 return PhoneArchive(workspace,orders,entries,history,generation)
}
fun WorkbenchDatabase.restoreArchive(archive:PhoneArchive,protection:File){
 require(protection.isFile&&protection.length()>0){"保护备份未完成，未开始恢复"}
 runInTransaction{
  val revisions=dao().allOrders().associate{it.id to it.revision}
  val restored=archive.orders.map{it.copy(revision=Math.addExact(maxOf(it.revision,revisions[it.id]?:0),1))}
  dao().clearMoney();dao().clearOrders();dao().clearHistory()
  val sync=dao().syncState()
  if(sync?.workspaceId==archive.workspaceId)dao().syncState(sync.copy(baseline=null,confirmedHead=null,confirmedAt=null))else dao().clearSyncState()
  dao().workspace(WorkspaceMeta(workspaceId=archive.workspaceId,generation=archive.generation))
  restored.forEach{dao().insert(it)};archive.money.forEach{dao().insert(it)};archive.history.forEach{dao().history(it)}
  dao().history(WorkHistory(entityId="workspace",action="restore-workspace",payload=JSONObject().put("protectionBackup",protection.name).toString()))
 }
}
fun WorkbenchDatabase.csv(kind:String):String{
 val rows=mutableListOf<List<String>>()
 runInTransaction{
  val orders=dao().allOrders().filter{it.deletedAt==null};val money=dao().allMoney();val index=orders.associateBy{it.id}
  if(kind=="orders"){
   rows+=listOf("订单编号","项目名称","交付类型","工作状态","接单日期","截止日期","订单金额(元)","净收款(元)","实际支出(元)","实际利润(元)","待收款(元)","当前进度","下一步","工程路径","备注")
   orders.forEach{o->val b=orderBalance(o,money);rows+=listOf(o.number,o.title,workKinds[o.kind]?:o.kind,workStatuses[o.status]?:o.status,o.acceptedDate,o.dueDate?:"",yuan(o.quoteCents).removePrefix("¥"),yuan(b.received).removePrefix("¥"),yuan(b.cost).removePrefix("¥"),yuan(b.profit).removePrefix("¥"),yuan(b.remaining).removePrefix("¥"),o.progress,o.nextStep,o.projectPath,o.notes)}
  }else{
   rows+=listOf("订单编号","项目名称","类型","金额(元)","日期","方式","备注","已作废","作废原因")
   money.filter{it.orderId in index}.forEach{e->val o=index.getValue(e.orderId);rows+=listOf(o.number,o.title,moneyKinds[e.kind]?:e.kind,yuan(e.amountCents).removePrefix("¥"),e.date?:"",e.method,e.note,if(e.voidedAt==null)"否"else"是",e.voidReason?:"")}
  }
 }
 return "\uFEFF"+rows.joinToString("\r\n"){row->row.joinToString(","){cell->"\""+(if(Regex("^\\s*[=+@-]").containsMatchIn(cell))"'$cell"else cell).replace("\"","\"\"")+"\""}}
}
