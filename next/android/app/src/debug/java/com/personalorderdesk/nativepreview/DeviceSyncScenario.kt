package com.personalorderdesk.nativepreview

import android.content.Context
import android.content.ContextWrapper
import androidx.room.Room
import com.personalorderdesk.nativepreview.sync.*
import org.json.JSONObject
import java.io.File
import java.util.UUID

/** Debug-only isolated Room integration. No real DB, account, Keystore, key file or network. */
object DeviceSyncScenario {
    @JvmStatic fun run(context:Context):String {
        val directory=File(context.cacheDir,"synthetic-sync-${UUID.randomUUID()}").apply{check(mkdirs())}
        val isolated=object:ContextWrapper(context){override fun getFilesDir()=directory}
        val db=Room.inMemoryDatabaseBuilder(context,WorkbenchDatabase::class.java).build()
        try {
            db.dao().workspace(WorkspaceMeta(workspaceId=UUID.randomUUID().toString()))
            val recovery=recovery()
            val original=WorkOrder(number="SYNTHETIC-ONLY",title="合成项目",kind="physical",quoteCents=5000,acceptedDate="2026-11-17",dueDate=null,historicalDates=true,tasks=taskTemplate("physical"))
            val receipt=WorkMoney(orderId=original.id,amountCents=1000)
            val source=PhoneSnapshotCodec.encode(PhoneArchive(recovery.workspaceId,listOf(original),listOf(receipt),emptyList()),JSONObject())
            val remote=Remote(recovery,source)
            val service=PhoneSyncRepository(isolated,db,remote,{"synthetic-sealed"},{recovery})
            service.importRecovery(recovery)
            service.confirmDownload(service.prepare("download"))
            check(db.dao().allOrders().size==1 && db.dao().allMoney().size==1)
            db.save(db.dao().order(original.id)!!.copy(progress="手机完成焊接"))
            val phoneReceipt=WorkMoney(orderId=original.id,amountCents=2000)
            db.record(phoneReceipt);db.record(phoneReceipt)
            remote.change { it.getJSONObject("orders").getJSONObject(original.id).put("title","电脑改名") }
            val merge=service.prepare("merge");check(merge.result.getJSONArray("conflicts").length()==0)
            check(service.confirmMerge(merge).getBoolean("uploaded"))
            check(db.dao().order(original.id)!!.progress=="手机完成焊接"&&db.dao().order(original.id)!!.title=="电脑改名")
            check(db.dao().allMoney().size==2&&!service.status().getBoolean("localChanges"))
            expectFailure{service.confirmMerge(merge)}
            db.save(db.dao().order(original.id)!!.copy(title="手机标题"))
            remote.change { it.getJSONObject("orders").getJSONObject(original.id).put("title","电脑标题") }
            val conflict=service.prepare("merge");check(conflict.allConflicts.length()>0)
            expectFailure{service.confirmMerge(conflict)}
            val choices=JSONObject();for(i in 0 until conflict.allConflicts.length())choices.put(conflict.allConflicts.getJSONObject(i).getString("id"),"remote")
            service.confirmMerge(service.resolve(conflict,choices))
            check(db.dao().order(original.id)!!.title=="电脑标题")
            val stale=service.prepare("merge");val before=remote.publishes
            db.save(db.dao().order(original.id)!!.copy(notes="预览后新修改"))
            expectFailure{service.confirmMerge(stale)};check(remote.publishes==before)
            val outdated=service.prepare("merge");remote.change{it.getJSONObject("orders").getJSONObject(original.id).put("nextStep","电脑新步骤")}
            expectFailure{service.confirmMerge(outdated)}
            val interrupted=service.prepare("merge")
            remote.duringPublish={db.save(db.dao().order(original.id)!!.copy(notes="上传期间的新修改"))}
            expectFailure{service.confirmMerge(interrupted)}
            check(db.dao().order(original.id)!!.notes=="上传期间的新修改")
            remote.duringPublish=null
            var retry=service.prepare("merge")
            if(retry.allConflicts.length()>0){val c=JSONObject();for(i in 0 until retry.allConflicts.length())c.put(retry.allConflicts.getJSONObject(i).getString("id"),"local");retry=service.resolve(retry,c)}
            service.confirmMerge(retry)
            check(db.dao().allMoney().size==2&&db.dao().order(original.id)!!.notes=="上传期间的新修改")
            check(!service.status().getBoolean("localChanges"))
            val newReceipt=WorkMoney(orderId=original.id,amountCents=100)
            db.record(newReceipt)
            remote.change{it.getJSONObject("orders").getJSONObject(original.id).put("deletedAt","2026-11-17T12:00:00+08:00")}
            val trash=service.prepare("merge");check(trash.allConflicts.length()>0)
            val deleteChoices=JSONObject();for(i in 0 until trash.allConflicts.length())deleteChoices.put(trash.allConflicts.getJSONObject(i).getString("id"),"remote")
            service.confirmMerge(service.resolve(trash,deleteChoices))
            check(db.dao().order(original.id)!!.deletedAt!=null&&db.dao().allMoney().size==3)
            remote.change{it.put("generation",UUID.randomUUID().toString())}
            expectFailure{service.prepare("merge")}
            check(db.dao().allMoney().size==3)
            service.confirmDownload(service.prepare("download"))
            check(db.dao().workspace()!!.generation==remote.snapshot.getString("generation"))
            check(File(directory,"backups").listFiles()!!.isNotEmpty())
            return "PASS: isolated Room download/merge/conflicts/replay/concurrency/recovery/delete-money/generation"
        } finally {db.close()}
    }
    private fun recovery():SyncRecovery {
        val key=java.util.Base64.getEncoder().encodeToString(ByteArray(32))
        val keyId=NativeSync.execute(JSONObject().put("operation","keyInfo").put("key",key)).getString("keyId")
        return SyncRecovery.parse(JSONObject().put("format","personal-order-desk.sync-recovery").put("version",1).put("workspaceId","11111111-1111-4111-8111-111111111111").put("repository","synthetic/book").put("key",key).put("keyId",keyId).toString())
    }
    private fun expectFailure(block:()->Unit){var failed=false;try{block()}catch(e:Exception){failed=true};check(failed){"Unsafe operation was accepted"}}
    private class Remote(val recovery:SyncRecovery,var snapshot:JSONObject):SnapshotRemote {
        var sequence=1;var publishes=0;var duringPublish:(()->Unit)?=null
        var envelope=recovery.encrypt(snapshot)
        private fun currentHead()=sequence.toString(16).padStart(40,'0')
        override fun head(repository:String)=currentHead()
        override fun read(recovery:SyncRecovery)=RemoteBook(currentHead(),JSONObject(snapshot.toString()),JSONObject(envelope.toString()),"b".repeat(40),"c".repeat(40))
        fun change(block:(JSONObject)->Unit){block(snapshot);sequence++;envelope=recovery.encrypt(snapshot)}
        override fun publish(recovery:SyncRecovery,base:RemoteBook,snapshot:JSONObject):String {
            check(base.head==currentHead());publishes++;this.snapshot=JSONObject(snapshot.toString());sequence++;envelope=recovery.encrypt(this.snapshot)
            duringPublish?.invoke();return currentHead()
        }
    }
}
