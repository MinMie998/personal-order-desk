package com.personalorderdesk.nativepreview.sync

import org.json.JSONObject

internal interface AccountVault { fun read():String?;fun write(value:String);fun clear() }
internal interface GitHubWire {
    fun oauth(path:String,body:JSONObject):JSONObject
    fun api(path:String,token:String):JSONObject
    fun write(path:String,method:String,body:JSONObject,token:String):JSONObject=error("此传输不支持写入")
}

/** Account-only coordinator. Contains no ledger, merge, repository write or recovery key API. */
internal class GitHubAccount(private val vault:AccountVault,private val wire:GitHubWire,private val now:()->Long={System.currentTimeMillis()/1000}) {
    private data class Pending(val deviceCode:String,val userCode:String,val expires:Long,var interval:Long,var next:Long)
    private var pending:Pending?=null
    private fun credentials()=vault.read()?.let{JSONObject(it)}
    @Synchronized fun status():JSONObject {
        val c=credentials();val p=pending?.takeIf{it.expires>now()}
        return JSONObject().put("saved",c!=null).put("login",c?.optString("login","")?:"")
            .put("pending",p?.let{JSONObject().put("userCode",it.userCode).put("expiresIn",(it.expires-now()).coerceAtLeast(0))}?:JSONObject.NULL)
    }
    @Synchronized fun begin():JSONObject {
        if(pending?.let{it.expires>now()}==true)return status()
        val r=wire.oauth("/login/device/code",JSONObject().put("client_id",CLIENT_ID).put("scope","offline_access"))
        check(!r.has("error")){"GitHub 未能创建授权，请检查应用设备授权配置"}
        val interval=r.optLong("interval",5).coerceIn(5,900);val time=now()
        pending=Pending(text(r,"device_code"),text(r,"user_code"),time+duration(r,"expires_in"),interval,time+interval)
        return status()
    }
    @Synchronized fun poll():JSONObject {
        val p=pending?:return status();val time=now()
        if(time>=p.expires){pending=null;error("授权码已到期，请重新获取")}
        if(time<p.next)return status()
        p.next=time+p.interval
        val r=wire.oauth("/login/oauth/access_token",JSONObject().put("client_id",CLIENT_ID).put("device_code",p.deviceCode).put("grant_type","urn:ietf:params:oauth:grant-type:device_code"))
        when(r.optString("error")) {
            "authorization_pending"->return status()
            "slow_down"->{p.interval+=5;p.next=time+p.interval;return status()}
            "access_denied"->{pending=null;error("已取消 GitHub 授权，原登录和本地账本保留")}
            "expired_token"->{pending=null;error("授权码已到期，请重新获取")}
            ""->{saveResponse(r);pending=null;return status()}
            else->{pending=null;error("GitHub 授权未完成，请重新获取授权码")}
        }
    }
    @Synchronized fun cancel():JSONObject {pending=null;return status()}
    @Synchronized fun remove():JSONObject {pending=null;vault.clear();return status()}
    private fun saveResponse(r:JSONObject) {
        val c=JSONObject().put("accessToken",text(r,"access_token")).put("refreshToken",text(r,"refresh_token"))
            .put("expiresAt",now()+duration(r,"expires_in")).put("refreshExpiresAt",now()+duration(r,"refresh_token_expires_in"))
        vault.write(c.toString())
    }
    private fun token():String {
        var c=credentials()?:error("请先连接 GitHub")
        if(c.getLong("expiresAt")<=now()+60){
            check(c.getLong("refreshExpiresAt")>now()){ "GitHub 登录已到期，请重新授权，本地账本保留" }
            val r=wire.oauth("/login/oauth/access_token",JSONObject().put("client_id",CLIENT_ID).put("grant_type","refresh_token").put("refresh_token",c.getString("refreshToken")))
            check(!r.has("error")){"GitHub 登录无法续期，请重新授权，本地账本保留"}
            saveResponse(r);c=credentials()?:error("续期凭据未保存")
        }
        return c.getString("accessToken")
    }
    @Synchronized fun checkAccount():JSONObject {
        val c=credentials()?:error("请先连接 GitHub")
        val user=wire.api("/user",token());val current=credentials()?:c
        current.put("login",text(user,"login"));vault.write(current.toString());return status()
    }
    @Synchronized fun checkRepository(repository:String):JSONObject {
        require(validRepository(repository)){"请填写 用户名/仓库名，不要填写网址"}
        val r=wire.api("/repos/$repository",token())
        check(r.optBoolean("private",false)){"仅支持私有数据仓库，请勿使用公开源码仓库"}
        return JSONObject().put("repository",text(r,"full_name")).put("private",true).put("checkedAt",now())
    }
    @Synchronized fun readApi(path:String):JSONObject=wire.api(path,token())
    @Synchronized fun writeApi(path:String,method:String,body:JSONObject):JSONObject=wire.write(path,method,body,token())
    companion object {
        const val CLIENT_ID="Iv23liYSdZSMwUJbFu4N" // Public identifier; never a secret.
        const val LOGIN_URL="https://github.com/login/device"
        fun validRepository(text:String)=Regex("[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}").matches(text)&&text.substringAfter('/') !in listOf(".","..")
        private fun text(o:JSONObject,k:String):String=o.getString(k).also{require(it.isNotBlank()&&it.length<8192){"GitHub 响应缺少有效字段"}}
        private fun duration(o:JSONObject,k:String):Long=o.getLong(k).also{require(it in 1..366L*24*3600){"GitHub 凭据有效期无效"}}
    }
}
