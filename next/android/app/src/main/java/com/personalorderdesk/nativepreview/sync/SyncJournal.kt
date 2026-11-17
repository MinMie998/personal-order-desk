package com.personalorderdesk.nativepreview.sync

import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.time.OffsetDateTime
import java.util.UUID
import org.json.JSONArray
import org.json.JSONObject

/** Device-only recovery information. Never stores keys, tokens or plaintext snapshots. */
internal class SyncJournal(private val directory:File) {
    @Synchronized fun begin(mode:String,repository:String,workspace:String,localDigest:String,remoteHead:String,targetDigest:String,backup:String):String {
        val id=UUID.randomUUID().toString()
        save(id,JSONObject().put("id",id).put("at",OffsetDateTime.now().toString()).put("mode",mode).put("repository",repository)
            .put("workspaceId",workspace).put("localDigest",localDigest).put("remoteHead",remoteHead).put("targetDigest",targetDigest).put("backup",backup).put("phase","prepared"))
        return id
    }
    @Synchronized fun phase(id:String,phase:String,head:String?=null) {
        UUID.fromString(id)
        val value=JSONObject(File(directory,"$id.json").readText()).put("phase",phase).put("updatedAt",OffsetDateTime.now().toString())
        if(head!=null)value.put("publishedHead",head)
        save(id,value)
    }
    @Synchronized fun list(workspace:String):JSONArray=JSONArray().apply {
        directory.listFiles()?.filter{it.extension=="json"&&it.length()<16384}?.sortedByDescending{it.lastModified()}?.mapNotNull{runCatching{JSONObject(it.readText())}.getOrNull()}
            ?.filter{it.optString("workspaceId")==workspace}?.take(10)?.forEach{put(it)}
    }
    @Synchronized fun reconcileOlder(workspace:String,current:String) {
        directory.listFiles()?.filter{it.extension=="json"&&it.length()<16384}?.forEach{file->
            val v=runCatching{JSONObject(file.readText())}.getOrNull()?:return@forEach
            if(v.optString("workspaceId")==workspace&&v.optString("id")!=current&&v.optString("phase") !in listOf("confirmed","reconciled"))phase(v.getString("id"),"reconciled")
        }
    }
    private fun save(id:String,value:JSONObject) {
        check(directory.isDirectory||directory.mkdirs()){"无法保存同步回溯记录，已停止提交"}
        val target=File(directory,"$id.json");val temp=File(directory,"$id.pending")
        FileOutputStream(temp).use{it.write(value.toString().toByteArray(Charsets.UTF_8));it.fd.sync()}
        Files.move(temp.toPath(),target.toPath(),StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING)
    }
}
