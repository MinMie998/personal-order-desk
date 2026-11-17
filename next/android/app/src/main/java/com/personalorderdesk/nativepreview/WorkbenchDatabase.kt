package com.personalorderdesk.nativepreview

import android.content.Context
import androidx.room.*
import kotlinx.coroutines.flow.Flow
import java.time.LocalDate
import java.time.OffsetDateTime
import java.util.UUID
import java.math.BigDecimal
import java.math.BigInteger

@Entity(tableName="orders")
data class WorkOrder(
 @PrimaryKey val id:String=UUID.randomUUID().toString(),val number:String="",val title:String="",
 val kind:String="simulation",val status:String="todo",val acceptedDate:String=LocalDate.now().toString(),
 val dueDate:String?=LocalDate.now().plusDays(1).toString(),val completedDate:String?=null,
 val quoteCents:Long=0,val progress:String="",val nextStep:String="",val projectPath:String="",val notes:String="",
 val trackingNumber:String="",val carrier:String="",val shippedDate:String?=null,val deliveredDate:String?=null,
 val parentId:String?=null,val revision:Long=0,val deletedAt:String?=null,
 @ColumnInfo(defaultValue="'[]'") val tasks:List<WorkTask> = emptyList(),
 @ColumnInfo(defaultValue="0") val historicalDates:Boolean=false
)
@Entity(tableName="money",foreignKeys=[ForeignKey(entity=WorkOrder::class,parentColumns=["id"],childColumns=["orderId"],onDelete=ForeignKey.RESTRICT)],indices=[Index("orderId")])
data class WorkMoney(@PrimaryKey val id:String=UUID.randomUUID().toString(),val orderId:String,val kind:String="receipt",val amountCents:Long,
 val date:String?=null,val method:String="微信",val note:String="",val voidedAt:String?=null,val voidReason:String?=null)
@Entity(tableName="workspace_meta") data class WorkspaceMeta(@PrimaryKey val id:Int=1,val workspaceId:String=UUID.randomUUID().toString(),val generation:String="legacy",val schemaVersion:Int=3,val syncProtocolVersion:Int=2)
@Entity(tableName="sync_state") data class PhoneSyncState(@PrimaryKey val id:Int=1,val workspaceId:String,val repository:String,val sealedRecovery:String,val baseline:String?=null,val confirmedHead:String?=null,val confirmedAt:String?=null,val preferences:String="{}")
@Entity(tableName="history",indices=[Index(value=["stableId"],unique=true)])data class WorkHistory(@PrimaryKey(autoGenerate=true)val id:Long=0,val entityId:String,val action:String,val at:String=OffsetDateTime.now().toString(),val payload:String,@ColumnInfo(defaultValue="''")val stableId:String=UUID.randomUUID().toString())

@Dao interface WorkDao{
 @Query("SELECT * FROM sync_state WHERE id=1")fun syncState():PhoneSyncState?
 @Insert(onConflict=OnConflictStrategy.REPLACE)fun syncState(state:PhoneSyncState)
 @Query("DELETE FROM sync_state")fun clearSyncState()
 @Query("SELECT * FROM workspace_meta WHERE id=1")fun workspace():WorkspaceMeta?
 @Query("SELECT * FROM workspace_meta WHERE id=1")fun observeWorkspace():Flow<WorkspaceMeta?>
 @Insert(onConflict=OnConflictStrategy.REPLACE)fun workspace(meta:WorkspaceMeta)
 @Query("SELECT * FROM orders ORDER BY rowid DESC")fun observeOrders():Flow<List<WorkOrder>>
 @Query("SELECT * FROM money ORDER BY rowid DESC")fun observeMoney():Flow<List<WorkMoney>>
 @Query("SELECT * FROM orders WHERE id=:id")fun order(id:String):WorkOrder?
 @Query("SELECT * FROM money WHERE id=:id")fun entry(id:String):WorkMoney?
 @Query("SELECT * FROM money WHERE orderId=:id")fun money(id:String):List<WorkMoney>
 @Query("SELECT * FROM history WHERE entityId=:id ORDER BY julianday(at) DESC,id DESC LIMIT 60")fun history(id:String):List<WorkHistory>
 @Query("SELECT * FROM orders ORDER BY rowid")fun allOrders():List<WorkOrder>
 @Query("SELECT * FROM money ORDER BY rowid")fun allMoney():List<WorkMoney>
 @Query("SELECT * FROM history ORDER BY id")fun allHistory():List<WorkHistory>
 @Query("DELETE FROM money")fun clearMoney()
 @Query("DELETE FROM orders")fun clearOrders()
 @Query("DELETE FROM history")fun clearHistory()
 @Insert(onConflict=OnConflictStrategy.ABORT)fun insert(order:WorkOrder)
 @Update fun update(order:WorkOrder)
 @Insert(onConflict=OnConflictStrategy.ABORT)fun insert(entry:WorkMoney)
 @Update fun update(entry:WorkMoney)
 @Insert fun history(entry:WorkHistory)
}
@TypeConverters(TaskConverters::class)
@Database(entities=[WorkOrder::class,WorkMoney::class,WorkHistory::class,WorkspaceMeta::class,PhoneSyncState::class],version=3,exportSchema=true)
abstract class WorkbenchDatabase:RoomDatabase(){
 abstract fun dao():WorkDao
 fun save(order:WorkOrder){
  require(order.number.isNotBlank()&&order.title.isNotBlank()){"请填写订单编号和项目名称"}
  LocalDate.parse(order.acceptedDate);listOf(order.dueDate,order.completedDate,order.shippedDate,order.deliveredDate).filterNotNull().forEach{LocalDate.parse(it)}
  require(order.quoteCents>=0){"金额不能小于零"}
  require(workKinds.containsKey(order.kind)&&workStatuses.containsKey(order.status)){"项目类型或状态无效"}
  require(order.deliveredDate==null||order.shippedDate!=null){"请先填写寄出日期"}
  require(order.deliveredDate==null||order.deliveredDate>=order.shippedDate!!){"收货日期不能早于寄出日期"}
  runInTransaction{
   val previous=dao().order(order.id)
   require(order.parentId==null||order.parentId!=order.id&&dao().order(order.parentId)!=null){"关联原单无效"}
   require(previous?.deletedAt==null){"回收站项目请先恢复"}
   require(previous?.revision==order.revision||previous==null&&order.revision==0L){"项目已更新，请重新打开后保存"}
   val saved=order.copy(tasks=normalizeTasks(order.tasks),number=order.number.trim(),title=order.title.trim(),revision=Math.addExact(order.revision,1),
    dueDate=order.dueDate?:if(!order.historicalDates&&order.kind!="physical")LocalDate.parse(order.acceptedDate).plusDays(1).toString()else null,
    completedDate=if(order.status=="done")order.completedDate?:if(order.historicalDates&&previous?.status=="done")null else LocalDate.now().toString()else null)
   if(previous==null)dao().insert(saved)else dao().update(saved)
   dao().history(WorkHistory(entityId=order.id,action="save-order",payload=saved.wire()))
  }
 }
 fun trash(order:WorkOrder,restore:Boolean=false){runInTransaction{
  val current=dao().order(order.id)?:error("项目不存在")
  require(current.revision==order.revision){"项目已更新，请重新打开后操作"}
  val saved=current.copy(deletedAt=if(restore)null else OffsetDateTime.now().toString(),revision=Math.addExact(current.revision,1))
  dao().update(saved);dao().history(WorkHistory(entityId=order.id,action=if(restore)"restore-order"else"trash-order",payload=saved.wire()))
 }}
 fun record(entry:WorkMoney){require(entry.amountCents>0){"请输入大于零的金额"};entry.date?.let{LocalDate.parse(it)}
  require(moneyKinds.containsKey(entry.kind)){"收支类型无效"}
  runInTransaction{
   val previous=dao().entry(entry.id);if(previous!=null){require(previous==entry){"流水已存在，请刷新核对"};return@runInTransaction}
   val order=dao().order(entry.orderId)?:error("项目不存在");require(order.deletedAt==null){"请先恢复回收站项目"}
   val balance=orderBalance(order,dao().money(order.id))
   if(entry.kind=="refund")require(entry.amountCents.toBigInteger()<=balance.received){"退款不能超过净收款"}
   if(entry.kind=="cost-refund")require(entry.amountCents.toBigInteger()<=balance.cost){"支出退回不能超过实际支出"}
   dao().insert(entry);dao().history(WorkHistory(entityId=entry.orderId,action="add-money",payload=entry.wire()))
  }
 }
 fun void(entry:WorkMoney,reason:String){require(reason.isNotBlank()){"请填写作废原因"};runInTransaction{
  val current=dao().entry(entry.id)?:error("流水不存在");if(current.voidedAt!=null)return@runInTransaction
  val saved=current.copy(voidedAt=OffsetDateTime.now().toString(),voidReason=reason.trim());dao().update(saved);dao().history(WorkHistory(entityId=entry.orderId,action="void-money",payload=saved.wire()))
 }}
 companion object{
  @Volatile private var instance:WorkbenchDatabase?=null
  @Synchronized fun open(context:Context):WorkbenchDatabase=instance?:build(context,"workbench-next.db").also{instance=it}
  @Synchronized fun closeCached(){instance?.close();instance=null}
  internal fun build(context:Context,name:String):WorkbenchDatabase=Room.databaseBuilder(context.applicationContext,WorkbenchDatabase::class.java,name)
   .addMigrations(migrationV1ToV2(context.applicationContext),migrationV2ToV3(context.applicationContext))
   .addCallback(object:RoomDatabase.Callback(){override fun onCreate(db:androidx.sqlite.db.SupportSQLiteDatabase){
    val id=context.getSharedPreferences("workbench-next",Context.MODE_PRIVATE).getString("workspaceId",null)?:UUID.randomUUID().toString()
    db.execSQL("INSERT INTO workspace_meta VALUES(1,?,'legacy',3,2)",arrayOf(id))
   }}).build()
 }
}
data class WorkBalance(val received:BigInteger,val cost:BigInteger,val profit:BigInteger,val remaining:BigInteger)
fun orderBalance(order:WorkOrder,entries:List<WorkMoney>):WorkBalance{
 var received=BigInteger.ZERO;var cost=BigInteger.ZERO
 entries.filter{it.orderId==order.id&&it.voidedAt==null}.forEach{val n=it.amountCents.toBigInteger();when(it.kind){"receipt"->received+=n;"refund"->received-=n;"cost"->cost+=n;"cost-refund"->cost-=n}}
 return WorkBalance(received,cost,received-cost,(order.quoteCents.toBigInteger()-received).max(BigInteger.ZERO))
}
fun yuan(value:BigInteger)="¥"+BigDecimal(value,2).toPlainString()
fun yuan(value:Long)=yuan(value.toBigInteger())
fun inputCents(value:String):Long{require(Regex("[0-9]+(\\.[0-9]{1,2})?").matches(value.trim())){"金额最多两位小数"};return BigDecimal(value.trim()).movePointRight(2).longValueExact()}
val workKinds=linkedMapOf("simulation" to "仿真","physical" to "实物制作","schematic" to "原理图 / PCB","code" to "代码 / 报告","other" to "其他")
val workStatuses=linkedMapOf("todo" to "未开始","doing" to "进行中","waiting" to "等待反馈","done" to "已完成","cancelled" to "已取消")
val moneyKinds=linkedMapOf("receipt" to "收款","cost" to "支出","refund" to "退款","cost-refund" to "支出退回")
private fun json(values:Map<String,Any?>)=org.json.JSONObject().apply{values.forEach{(k,v)->put(k,v?:org.json.JSONObject.NULL)}}.toString()
fun WorkOrder.wire()=json(linkedMapOf("id" to id,"number" to number,"title" to title,"kind" to kind,"status" to status,"acceptedDate" to acceptedDate,"dueDate" to dueDate,"completedDate" to completedDate,"quoteCents" to quoteCents.toString(),"progress" to progress,"nextStep" to nextStep,"projectPath" to projectPath,"notes" to notes,"trackingNumber" to trackingNumber,"carrier" to carrier,"shippedDate" to shippedDate,"deliveredDate" to deliveredDate,"parentId" to parentId,"revision" to revision,"deletedAt" to deletedAt,"historicalDates" to historicalDates,"tasks" to org.json.JSONArray(TaskConverters().encode(tasks))))
fun WorkMoney.wire()=json(linkedMapOf("id" to id,"orderId" to orderId,"kind" to kind,"amountCents" to amountCents.toString(),"date" to date,"method" to method,"note" to note,"voidedAt" to voidedAt,"voidReason" to voidReason))
