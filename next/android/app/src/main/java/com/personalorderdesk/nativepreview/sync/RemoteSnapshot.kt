package com.personalorderdesk.nativepreview.sync

import org.json.JSONObject
import java.security.MessageDigest
import java.util.Base64

internal class RemoteBook(val head:String,val snapshot:JSONObject,val envelope:JSONObject,val tree:String="",val blobSha:String="")
internal interface SnapshotRemote {
    fun read(recovery:SyncRecovery):RemoteBook
    fun head(repository:String):String
    fun publish(recovery:SyncRecovery,base:RemoteBook,snapshot:JSONObject):String=error("不支持发布")
}
internal class GitHubSnapshotRemote(private val account:GitHubAccount):SnapshotRemote {
    override fun head(repository:String):String {
        require(GitHubAccount.validRepository(repository))
        return sha(account.readApi("/repos/$repository/git/ref/heads/desk-sync-v1").getJSONObject("object").getString("sha"))
    }
    override fun read(recovery:SyncRecovery):RemoteBook {
        account.checkRepository(recovery.repository)
        val prefix="/repos/${recovery.repository}";val head=head(recovery.repository)
        val commit=account.readApi("$prefix/git/commits/$head")
        val tree=sha(commit.getJSONObject("tree").getString("sha"))
        val listing=account.readApi("$prefix/git/trees/$tree?recursive=1")
        check(!listing.optBoolean("truncated",false)){"远端目录不完整，请使用独立的数据仓库"}
        val entries=listing.getJSONArray("tree");val path="workspaces/${recovery.workspaceId}/snapshot.enc.json"
        val entry=(0 until entries.length()).map{entries.getJSONObject(it)}.singleOrNull{it.optString("path")==path}?:error("此仓库没有对应工作台的远端账本，请先在电脑完成首次同步")
        check(entry.optString("type")=="blob"&&entry.optLong("size",Long.MAX_VALUE) in 1..15L*1024*1024){"远端快照类型或大小不支持"}
        val blobSha=sha(entry.getString("sha"));val blob=account.readApi("$prefix/git/blobs/$blobSha")
        check(blob.optString("encoding")=="base64"){"远端快照编码不支持"}
        val bytes=try{Base64.getDecoder().decode(blob.getString("content").replace("\r","").replace("\n",""))}catch(e:IllegalArgumentException){error("远端快照编码损坏")}
        check(bytes.size<=15*1024*1024){"远端快照超过支持大小"}
        val digest=MessageDigest.getInstance("SHA-1").apply{update("blob ${bytes.size}\u0000".toByteArray(Charsets.UTF_8));update(bytes)}.digest().joinToString(""){"%02x".format(it)}
        check(digest.equals(blobSha,true)){"远端快照对象校验不通过"}
        val envelope=try{JSONObject(bytes.toString(Charsets.UTF_8))}catch(e:Exception){error("远端加密包格式无效")}
        return RemoteBook(head,recovery.decrypt(envelope),envelope,tree,blobSha)
    }
    override fun publish(recovery:SyncRecovery,base:RemoteBook,snapshot:JSONObject):String {
        PhoneSnapshotCodec.decode(snapshot)
        account.checkRepository(recovery.repository)
        check(head(recovery.repository)==base.head){"远端已变化，请重新检查同步"}
        val prefix="/repos/${recovery.repository}";val path="workspaces/${recovery.workspaceId}"
        val envelope=recovery.encrypt(snapshot)
        val blob=account.writeApi("$prefix/git/blobs","POST",JSONObject().put("encoding","base64").put("content",Base64.getEncoder().encodeToString(envelope.toString().toByteArray(Charsets.UTF_8))))
        val entries=org.json.JSONArray().put(JSONObject().put("path","$path/snapshot.enc.json").put("mode","100644").put("type","blob").put("sha",sha(blob.getString("sha"))))
            .put(JSONObject().put("path","$path/backups/${sha(base.head)}.enc.json").put("mode","100644").put("type","blob").put("sha",sha(base.blobSha)))
        val tree=account.writeApi("$prefix/git/trees","POST",JSONObject().put("base_tree",sha(base.tree)).put("tree",entries))
        val commit=account.writeApi("$prefix/git/commits","POST",JSONObject().put("message","Merge encrypted workbench snapshot").put("tree",sha(tree.getString("sha"))).put("parents",org.json.JSONArray().put(base.head)))
        val next=sha(commit.getString("sha"))
        val reference=account.writeApi("$prefix/git/refs/heads/desk-sync-v1","PATCH",JSONObject().put("sha",next).put("force",false))
        check(reference.getJSONObject("object").getString("sha")==next){"远端确认尚未完成，请重新检查同步，不要重复手动记账"}
        return next
    }
    private fun sha(text:String)=text.also{require(Regex("[a-fA-F0-9]{40}").matches(it)){"GitHub 版本标识无效"}}
}
