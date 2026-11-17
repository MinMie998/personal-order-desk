package com.personalorderdesk.nativepreview.sync

import android.content.Context
import com.personalorderdesk.nativepreview.*
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.time.OffsetDateTime
import java.util.UUID

internal class PhoneSyncPlan(val mode:String,val local:JSONObject,val remote:RemoteBook,val result:JSONObject,val state:PhoneSyncState,val localDigest:String,val backupPath:String,val allConflicts:org.json.JSONArray=result.optJSONArray("conflicts")?:org.json.JSONArray(),val choices:JSONObject=JSONObject()) {
    var consumed=false
}
internal class PhoneSyncRepository(private val context:Context,private val db:WorkbenchDatabase,private val remote:SnapshotRemote,private val sealRecovery:(SyncRecovery)->String=SyncRecoveryCipher::seal,private val openRecovery:(String)->SyncRecovery=SyncRecoveryCipher::open) {
    private val journal=SyncJournal(File(context.filesDir,"sync-journal"))
    @Synchronized fun status():JSONObject {
        val state=db.dao().syncState()?:return JSONObject().put("configured",false)
        val recovery=openRecovery(state.sealedRecovery)
        val local=PhoneSnapshotCodec.snapshot(db)
        return JSONObject().put("configured",true).put("repository",state.repository).put("workspaceId",state.workspaceId).put("keyId",recovery.keyId)
            .put("confirmedHead",state.confirmedHead?:"").put("confirmedAt",state.confirmedAt?:"").put("hasBaseline",state.baseline!=null)
            .put("localChanges",state.baseline==null||PhoneSnapshotCodec.digest(local)!=PhoneSnapshotCodec.digest(JSONObject(state.baseline)))
            .put("runs",journal.list(state.workspaceId))
    }
    @Synchronized fun importRecovery(recovery:SyncRecovery) {
        val sealed=sealRecovery(recovery)
        db.runInTransaction{
            backupLocal()
            val old=db.dao().syncState()
            val same=old?.let{it.workspaceId==recovery.workspaceId&&it.repository.equals(recovery.repository,true)&&runCatching{openRecovery(it.sealedRecovery).keyId==recovery.keyId}.getOrDefault(false)}==true
            db.dao().syncState(if(same)old!!.copy(sealedRecovery=sealed)else PhoneSyncState(workspaceId=recovery.workspaceId,repository=recovery.repository,sealedRecovery=sealed))
        }
    }
    @Synchronized fun prepare(mode:String):PhoneSyncPlan {
        require(mode in listOf("download","merge"))
        val state=db.dao().syncState()?:error("请先导入电脑导出的恢复密钥")
        val recovery=openRecovery(state.sealedRecovery)
        var local=JSONObject();var backup=""
        db.runInTransaction{local=PhoneSnapshotCodec.snapshot(db);backup=backupLocal().absolutePath}
        val book=remote.read(recovery)
        PhoneSnapshotCodec.decode(book.snapshot)
        val result=if(mode=="download")JSONObject().put("snapshot",PhoneSnapshotCodec.normalized(book.snapshot)).put("conflicts",org.json.JSONArray())
        else preparePhoneMerge(state.baseline,local,book.snapshot)
        if(result.getJSONArray("conflicts").length()==0)PhoneSnapshotCodec.decode(result.getJSONObject("snapshot"))
        return PhoneSyncPlan(mode,local,book,result,state,PhoneSnapshotCodec.digest(local),backup)
    }
    @Synchronized fun confirmDownload(plan:PhoneSyncPlan):String {
        check(plan.mode=="download"&&!plan.consumed){"预览已失效，请重新检查远端"}
        val currentHead=remote.head(plan.state.repository)
        val snapshot=plan.result.getJSONObject("snapshot");PhoneSnapshotCodec.decode(snapshot)
        val expected=PhoneSnapshotCodec.digest(snapshot)
        protectRemote(plan.remote)
        var backup=""
        db.runInTransaction{
            requireDownloadCurrent(plan,db.dao().syncState(),PhoneSnapshotCodec.snapshot(db),currentHead)
            backup=backupLocal().absolutePath
            applySnapshot(snapshot,plan.state,plan.remote.head,expected)
        }
        plan.consumed=true
        runCatching{val run=journal.begin("download",plan.state.repository,plan.state.workspaceId,plan.localDigest,plan.remote.head,expected,backup);journal.phase(run,"confirmed",plan.remote.head);journal.reconcileOlder(plan.state.workspaceId,run)}
        return backup
    }
    @Synchronized fun resolve(plan:PhoneSyncPlan,choices:JSONObject):PhoneSyncPlan {
        check(plan.mode=="merge"&&!plan.consumed){"合并预览已失效，请重新检查"}
        val allowed=(0 until plan.allConflicts.length()).map{plan.allConflicts.getJSONObject(it).getString("id")}.toSet()
        require(choices.keys().asSequence().all{it in allowed && choices.getString(it) in listOf("local","remote")}){"冲突选择不属于本次预览"}
        val result=preparePhoneMerge(plan.state.baseline,plan.local,plan.remote.snapshot,choices)
        if(result.getJSONArray("conflicts").length()==0)PhoneSnapshotCodec.decode(result.getJSONObject("snapshot"))
        return PhoneSyncPlan(plan.mode,plan.local,plan.remote,result,plan.state,plan.localDigest,plan.backupPath,plan.allConflicts,JSONObject(choices.toString()))
    }
    @Synchronized fun confirmMerge(plan:PhoneSyncPlan):JSONObject {
        val resolved=resolve(plan,plan.choices)
        check(resolved.result.getJSONArray("conflicts").length()==0){"请先逐项处理所有冲突"}
        val snapshot=resolved.result.getJSONObject("snapshot");val digest=PhoneSnapshotCodec.digest(snapshot)
        val recovery=openRecovery(plan.state.sealedRecovery)
        val currentHead=remote.head(plan.state.repository)
        var backup=""
        db.runInTransaction{requireMergeCurrent(plan,db.dao().syncState(),PhoneSnapshotCodec.snapshot(db),currentHead);backup=backupLocal().absolutePath}
        protectRemote(plan.remote)
        val run=journal.begin("merge",plan.state.repository,plan.state.workspaceId,plan.localDigest,plan.remote.head,digest,backup)
        var published=false
        try {
            val upload=plan.remote.snapshot.getInt("format")<2||digest!=PhoneSnapshotCodec.digest(plan.remote.snapshot)
            var head=plan.remote.head
            if(upload){
                journal.phase(run,"publishing")
                head=remote.publish(recovery,plan.remote,snapshot);published=true
                journal.phase(run,"published",head)
                val verified=remote.read(recovery)
                check(verified.head==head&&PhoneSnapshotCodec.digest(verified.snapshot)==digest){"远端提交后又有新版本，请重新检查合并；本机记录保留"}
            }
            db.runInTransaction{
                requireMergeCurrent(plan,db.dao().syncState(),PhoneSnapshotCodec.snapshot(db),currentHead)
                applySnapshot(snapshot,plan.state,head,digest)
            }
            plan.consumed=true
            val logged=runCatching{journal.phase(run,"confirmed",head);journal.reconcileOlder(plan.state.workspaceId,run)}.isSuccess
            return JSONObject().put("head",head).put("uploaded",upload).put("backup",backup).put("journalSaved",logged)
        }catch(e:Exception){
            runCatching{journal.phase(run,if(published)"remote-confirmed-local-pending"else"needs-check")}
            throw IllegalStateException(if(published)"远端已更新，手机尚未完成确认；本机记录保留，请重新检查同步，不要重复录入收支"else"同步尚未确认，本机记录保留：${e.message?:"请重新检查"}")
        }
    }
    private fun applySnapshot(snapshot:JSONObject,state:PhoneSyncState,head:String,expected:String) {
        val archive=PhoneSnapshotCodec.decode(snapshot)
        val revisions=db.dao().allOrders().associate{it.id to it.revision}
        db.dao().clearMoney();db.dao().clearOrders();db.dao().clearHistory()
        archive.orders.forEach{db.dao().insert(it.copy(revision=Math.addExact(maxOf(it.revision,revisions[it.id]?:0),1)))}
        archive.money.forEach{db.dao().insert(it)};archive.history.forEach{db.dao().history(it)}
        db.dao().workspace(WorkspaceMeta(workspaceId=archive.workspaceId,generation=archive.generation))
        db.dao().syncState(state.copy(baseline=snapshot.toString(),confirmedHead=head,confirmedAt=OffsetDateTime.now().toString(),preferences=snapshot.getJSONObject("preferences").toString()))
        check(PhoneSnapshotCodec.digest(PhoneSnapshotCodec.snapshot(db))==expected){"写入后的账本摘要不一致，已回滚手机替换"}
    }
    private fun backupLocal():File {
        val id=checkNotNull(db.dao().workspace()){ "本机工作台信息缺失" }.workspaceId
        val text=db.archive(id);readPhoneArchive(text)
        val bytes=text.toByteArray(Charsets.UTF_8);require(bytes.size<=50*1024*1024){"本机保护备份超过50MiB，已停止操作"}
        val directory=File(context.filesDir,"backups");check(directory.isDirectory||directory.mkdirs()){ "无法创建保护备份目录" }
        val file=File(directory,"同步前-${UUID.randomUUID()}.json")
        FileOutputStream(file).use{it.write(bytes);it.fd.sync()}
        return file
    }
    private fun protectRemote(book:RemoteBook) {
        val directory=File(context.filesDir,"remote-protection");check(directory.isDirectory||directory.mkdirs()){ "无法保存远端密文保护副本" }
        val file=File(directory,"${book.head}.enc.json")
        if(file.exists()){
            check(JSONObject(file.readText()).optString("ciphertext")==book.envelope.getString("ciphertext")){"远端密文保护副本校验失败"};return
        }
        FileOutputStream(file).use{it.write(book.envelope.toString().toByteArray(Charsets.UTF_8));it.fd.sync()}
    }
}

internal fun requireDownloadCurrent(plan:PhoneSyncPlan,state:PhoneSyncState?,local:JSONObject,head:String) {
    check(plan.mode=="download"&&!plan.consumed){"下载预览已失效，请重新检查"}
    check(head==plan.remote.head){"远端在预览后已更新，请重新读取；本机未替换"}
    check(state==plan.state){"同步设置已变化，请重新检查"}
    check(PhoneSnapshotCodec.digest(local)==plan.localDigest){"本机在预览后又有修改，请重新检查；没有覆盖新记录"}
}

internal fun requireMergeCurrent(plan:PhoneSyncPlan,state:PhoneSyncState?,local:JSONObject,head:String) {
    check(plan.mode=="merge"&&!plan.consumed){"合并预览已失效，请重新检查"}
    check(head==plan.remote.head){"远端已更新，请重新检查"}
    check(state==plan.state){"连接或共同基准已变化，请重新检查"}
    check(PhoneSnapshotCodec.digest(local)==plan.localDigest){"手机在预览后又有修改，请重新检查"}
}

internal fun preparePhoneMerge(baseline:String?,local:JSONObject,remote:JSONObject,choices:JSONObject=JSONObject()):JSONObject {
    check(baseline!=null){"尚无共同基准，请先预览从远端下载完整副本；本机已有数据会先备份并单独确认"}
    val base=JSONObject(baseline)
    check(local.getString("workspaceId")==remote.getString("workspaceId")){"当前是独立账本，请选择下载预览，不能直接混合两个工作台"}
    return NativeSync.execute(JSONObject().put("operation","merge").put("base",base).put("local",local).put("remote",remote).put("choices",choices))
}
