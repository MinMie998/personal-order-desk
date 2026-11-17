package com.personalorderdesk.nativepreview

import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import org.json.JSONObject

internal data class ProjectDraft(val workspaceId:String,val base:WorkOrder,val order:WorkOrder,val quote:String,val tab:String) {
    fun encode()=JSONObject().put("version",1).put("workspaceId",workspaceId).put("base",JSONObject(base.wire()))
        .put("order",JSONObject(order.wire())).put("quote",quote).put("tab",tab).toString()
    companion object {
        fun decode(text:String):ProjectDraft {
            val o=JSONObject(text);require(o.getInt("version")==1){"草稿版本不兼容，原文件保留"}
            val draft=ProjectDraft(o.getString("workspaceId"),decodeWireOrder(o.getJSONObject("base")),decodeWireOrder(o.getJSONObject("order")),o.getString("quote"),o.getString("tab"))
            require(draft.base.id==draft.order.id){"草稿项目身份不一致"};return draft
        }
    }
}

/** Drafts contain no credentials and live only in private app files, outside the ledger. */
internal class ProjectDraftStore(private val directory:File) {
    private fun file(workspace:String,id:String):File {
        val key=MessageDigest.getInstance("SHA-256").digest("$workspace:$id".toByteArray()).joinToString(""){"%02x".format(it)}
        return File(directory,"$key.json")
    }
    fun read(workspace:String,id:String):ProjectDraft?=synchronized(lock) {
        val f=file(workspace,id);if(!f.isFile)return@synchronized null
        require(f.length()<=MAX_BYTES){"草稿文件过大，已保留原文件"}
        ProjectDraft.decode(f.readText()).also { require(it.workspaceId==workspace&&it.base.id==id){"草稿账本身份不匹配"} }
    }
    fun list(workspace:String):List<ProjectDraft> = synchronized(lock) {
        directory.listFiles()?.filter{it.extension=="json"&&it.length()<=MAX_BYTES}?.sortedByDescending{it.lastModified()}?.mapNotNull{
            runCatching{ProjectDraft.decode(it.readText())}.getOrNull()?.takeIf{d->d.workspaceId==workspace}
        }?:emptyList()
    }
    fun write(draft:ProjectDraft)=synchronized(lock) {
        check(directory.isDirectory||directory.mkdirs()){"无法创建草稿目录"}
        val bytes=draft.encode().toByteArray(Charsets.UTF_8);require(bytes.size<=MAX_BYTES){"草稿过大，请直接保存项目"}
        val target=file(draft.workspaceId,draft.base.id);val temp=File(directory,target.name+".pending")
        FileOutputStream(temp).use{it.write(bytes);it.fd.sync()}
        Files.move(temp.toPath(),target.toPath(),StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING)
        Unit
    }
    fun remove(workspace:String,id:String)=synchronized(lock) {
        val f=file(workspace,id);check(!f.exists()||f.delete()){"无法删除草稿"};Unit
    }
    companion object { private val lock=Any();private const val MAX_BYTES=2L*1024*1024 }
}
internal class ProjectDraftSession(private val store:ProjectDraftStore,private val workspace:String,private val id:String) {
    private var closed=false
    @Synchronized fun write(draft:ProjectDraft){if(!closed)store.write(draft)}
    @Synchronized fun finish(){closed=true;store.remove(workspace,id)}
}
