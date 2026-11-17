package com.personalorderdesk.nativepreview.sync

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import android.util.Base64
import org.json.JSONObject
import java.io.File
import java.net.URL
import java.net.URLEncoder
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.net.ssl.HttpsURLConnection

internal class AndroidAccountVault(context:Context):AccountVault {
    private val file=AtomicFile(File(context.filesDir,"github-account.sealed"))
    private val aad="PersonalOrderDesk.Android.GitHub.v1".toByteArray(Charsets.UTF_8)
    private fun key(create:Boolean):SecretKey {
        val store=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
        val existing=store.getKey(ALIAS,null) as? SecretKey
        if(existing!=null)return existing
        check(create){"本机 GitHub 加密密钥不可用，请移除本机登录后重新授权"}
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore").apply{
            init(KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        }.generateKey()
    }
    override fun read():String? {
        if(!file.baseFile.exists()&&!File(file.baseFile.path+".bak").exists())return null
        try {
            check(file.baseFile.length()<=128*1024 && File(file.baseFile.path+".bak").length()<=128*1024)
            val o=JSONObject(file.openRead().use{it.readBytes().toString(Charsets.UTF_8)})
            check(o.getInt("version")==1)
            val cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,key(false),GCMParameterSpec(128,Base64.decode(o.getString("iv"),Base64.NO_WRAP)));cipher.updateAAD(aad)
            return cipher.doFinal(Base64.decode(o.getString("ciphertext"),Base64.NO_WRAP)).toString(Charsets.UTF_8)
        }catch(e:Exception){throw IllegalStateException("无法解锁本机 GitHub 登录，请重新授权；账本数据保留")}
    }
    override fun write(value:String) {
        val cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key(true));cipher.updateAAD(aad)
        val o=JSONObject().put("version",1).put("iv",Base64.encodeToString(cipher.iv,Base64.NO_WRAP)).put("ciphertext",Base64.encodeToString(cipher.doFinal(value.toByteArray(Charsets.UTF_8)),Base64.NO_WRAP))
        val output=file.startWrite()
        try{output.write(o.toString().toByteArray(Charsets.UTF_8));file.finishWrite(output)}catch(e:Exception){file.failWrite(output);throw IllegalStateException("无法保存 GitHub 登录，账本数据未修改")}
    }
    override fun clear(){file.delete()}
    companion object {private const val ALIAS="PersonalOrderDesk.GitHub.v1"}
}

internal class AndroidGitHubWire:GitHubWire {
    override fun oauth(path:String,body:JSONObject):JSONObject {
        require(path in listOf("/login/device/code","/login/oauth/access_token"))
        val data=body.keys().asSequence().joinToString("&"){k->URLEncoder.encode(k,"UTF-8")+"="+URLEncoder.encode(body.getString(k),"UTF-8")}
        return request("https://github.com$path",null,data)
    }
    override fun api(path:String,token:String):JSONObject {
        require(validReadPath(path)){"不支持的 GitHub 读取路径"}
        return request("https://api.github.com$path",token,null)
    }
    override fun write(path:String,method:String,body:JSONObject,token:String):JSONObject {
        require(validWritePath(path,method)){"不支持的 GitHub 写入路径"}
        if(method=="PATCH")require(body.optBoolean("force",true)==false){"禁止强制覆盖同步分支"}
        return request("https://api.github.com$path",token,body.toString(),method,"application/json; charset=utf-8")
    }
    private fun request(url:String,token:String?,body:String?,method:String=if(body==null)"GET"else"POST",contentType:String="application/x-www-form-urlencoded"):JSONObject {
        val connection=URL(url).openConnection() as HttpsURLConnection
        try {
            connection.connectTimeout=10000;connection.readTimeout=20000;connection.instanceFollowRedirects=false
            connection.setRequestProperty("Accept","application/json");connection.setRequestProperty("User-Agent","PersonalOrderDesk-Android")
            if(token!=null){connection.setRequestProperty("Authorization","Bearer $token");connection.setRequestProperty("X-GitHub-Api-Version","2022-11-28")}
            connection.requestMethod=method
            if(body!=null){val bytes=body.toByteArray(Charsets.UTF_8);require(bytes.size<=24*1024*1024){"请求过大，已保留本地数据"};connection.doOutput=true;connection.setRequestProperty("Content-Type",contentType);connection.setFixedLengthStreamingMode(bytes.size);connection.outputStream.use{it.write(bytes)}}
            when(val code=connection.responseCode){
                in 200..299->Unit
                401->error("GitHub 登录已失效，请重新授权，本地数据保留")
                403,429->error("GitHub 权限不足或访问受限，请稍后检查授权与限流状态")
                404->error("仓库不存在或 GitHub App 未获此仓库授权，请检查安装范围")
                409,422->error("远端版本变化或请求未通过校验，本次不强制覆盖；请重新检查同步")
                else->error("GitHub 暂时无法完成操作（HTTP $code），本地数据保留")
            }
            val out=java.io.ByteArrayOutputStream()
            val limit=if(url.contains("/git/blobs/"))24*1024*1024 else 2*1024*1024
            connection.inputStream.use{stream->val buffer=ByteArray(8192);while(true){val n=stream.read(buffer);if(n<0)break;check(out.size()+n<=limit){"GitHub 响应过大"};out.write(buffer,0,n)}}
            return try{JSONObject(out.toString("UTF-8"))}catch(e:org.json.JSONException){error("GitHub 响应格式无效，请稍后重试")}
        }catch(e:java.io.IOException){throw IllegalStateException("暂时无法连接 GitHub，请检查网络；本地数据保留")}
        finally{connection.disconnect()}
    }
    companion object {
        fun validWritePath(path:String,method:String):Boolean {
            val parts=path.removePrefix("/repos/").split('/',limit=3)
            if(!path.startsWith("/repos/")||parts.size!=3||!GitHubAccount.validRepository(parts[0]+"/"+parts[1]))return false
            return method=="POST"&&parts[2] in listOf("git/blobs","git/trees","git/commits")||method=="PATCH"&&parts[2]=="git/refs/heads/desk-sync-v1"
        }
        fun validReadPath(path:String):Boolean {
            if(path=="/user")return true
            val parts=path.removePrefix("/repos/").split('/',limit=3)
            if(!path.startsWith("/repos/")||parts.size<2||!GitHubAccount.validRepository(parts[0]+"/"+parts[1]))return false
            if(parts.size==2)return true
            return parts[2]=="git/ref/heads/desk-sync-v1"||Regex("git/(commits|blobs)/[0-9a-fA-F]{40}").matches(parts[2])||Regex("git/trees/[0-9a-fA-F]{40}\\?recursive=1").matches(parts[2])
        }
    }
}
internal object PhoneGitHub {
    @Volatile private var value:GitHubAccount?=null
    @Synchronized fun get(context:Context)=value?:GitHubAccount(AndroidAccountVault(context.applicationContext),AndroidGitHubWire()).also{value=it}
}
