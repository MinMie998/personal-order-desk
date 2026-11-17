//! R0 native Device Flow. Fixed GitHub endpoints; no ledger or repository writes.
use reqwest::blocking::Client;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{sync::Mutex, time::{Duration, SystemTime, UNIX_EPOCH}};
use crate::vault as control;

const CLIENT_ID: &str = "Iv23liYSdZSMwUJbFu4N"; // Public GitHub App identifier, never a secret.
const DEVICE_URL: &str = "https://github.com/login/device/code";
const TOKEN_URL: &str = "https://github.com/login/oauth/access_token";
const LOGIN_URL: &str = "https://github.com/login/device";

#[derive(Serialize, Deserialize)]
struct Credentials {
    access_token: String,
    refresh_token: String,
    expires_at: u64,
    refresh_expires_at: u64,
    #[serde(default)]
    reauthorize: bool,
}

impl Credentials {
    fn from_response(value: &Value, now: u64) -> Result<Self, String> {
        let access_token = required_text(value, "access_token")?;
        let refresh_token = required_text(value, "refresh_token")?;
        let expires = required_duration(value, "expires_in")?;
        let refresh_expires = required_duration(value, "refresh_token_expires_in")?;
        Ok(Self {access_token, refresh_token, expires_at:now.saturating_add(expires),
            refresh_expires_at:now.saturating_add(refresh_expires), reauthorize:false})
    }
    fn needs_refresh(&self, now: u64) -> bool { self.expires_at <= now.saturating_add(60) }
    fn needs_login(&self, now: u64) -> bool { self.reauthorize || self.refresh_expires_at <= now }
}

struct Pending { device_code:String, user_code:String, expires_at:u64, interval:u64, next_poll:u64 }
#[derive(Default)]
pub struct AuthState { pending:Option<Pending> }

fn required_text(value:&Value, key:&str)->Result<String,String>{
    value[key].as_str().filter(|v|!v.is_empty()&&v.len()<8192).map(str::to_owned)
        .ok_or_else(||"GitHub授权响应不完整，请重新授权".into())
}
fn required_duration(value:&Value, key:&str)->Result<u64,String>{
    value[key].as_u64().filter(|v|*v>0&&*v<366*24*3600)
        .ok_or_else(||"GitHub未返回有效的凭据有效期，请检查短期令牌设置".into())
}
fn now()->u64{SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()}
fn client()->Result<Client,String>{
    Client::builder().timeout(Duration::from_secs(20)).connect_timeout(Duration::from_secs(10))
        .redirect(reqwest::redirect::Policy::none()).user_agent("PersonalOrderDesk-R0")
        .build().map_err(|_|"无法建立GitHub安全连接".into())
}
fn oauth(url:&str, body:Value)->Result<Value,String>{
    let response=client()?.post(url).header("Accept","application/json").json(&body).send()
        .map_err(|_|"暂时无法连接GitHub，已保留本机登录信息，请稍后重试")?;
    if !response.status().is_success(){return Err(format!("GitHub暂时无法完成授权（HTTP {}），请稍后重试",response.status().as_u16()))}
    response.json().map_err(|_|"GitHub授权响应格式无效".into())
}
fn save(credentials:&Credentials)->Result<(),String>{
    let bytes=serde_json::to_vec(credentials).map_err(|_|"无法整理登录凭据")?;
    control::save_auth(&bytes)
}
fn load()->Result<Option<Credentials>,String>{
    control::read_auth()?.map(|bytes|serde_json::from_slice(&bytes)
        .map_err(|_|"本机登录凭据格式无效，请移除本机登录后重新授权".into())).transpose()
}
fn public_status(credentials:Option<&Credentials>, pending:Option<&Pending>, time:u64)->Value{
    json!({"state":match credentials {None=>"signed-out",Some(c) if c.needs_login(time)=>"reauthorize",Some(c) if c.needs_refresh(time)=>"refresh-needed",Some(_)=>"saved"},
        "pending":pending.map(|p|json!({"userCode":p.user_code,"verificationUrl":LOGIN_URL,
            "expiresIn":p.expires_at.saturating_sub(time),"retryAfter":p.next_poll.saturating_sub(time)})),
        "dataAccess":false})
}
impl AuthState {
    fn status(&self)->Result<Value,String>{Ok(public_status(load()?.as_ref(),self.pending.as_ref(),now()))}
    fn begin(&mut self)->Result<Value,String>{
        if self.pending.as_ref().is_some_and(|p|p.expires_at>now()){return self.status()}
        let response=oauth(DEVICE_URL,json!({"client_id":CLIENT_ID}))?;
        let interval=response["interval"].as_u64().unwrap_or(5).clamp(5,900);
        let time=now();
        self.pending=Some(Pending{device_code:required_text(&response,"device_code")?,
            user_code:required_text(&response,"user_code")?,expires_at:time.saturating_add(required_duration(&response,"expires_in")?),
            interval,next_poll:time.saturating_add(interval)});
        self.status()
    }
    fn poll(&mut self)->Result<Value,String>{
        let time=now();
        let p=self.pending.as_mut().ok_or("没有等待中的授权，请先获取授权码")?;
        if p.expires_at<=time{self.pending=None;return Err("授权码已到期，请重新获取".into())}
        if p.next_poll>time{return self.status()}
        // Advance before the request, including timeouts: never hammer the token endpoint.
        p.next_poll=time.saturating_add(p.interval);
        let response=oauth(TOKEN_URL,json!({"client_id":CLIENT_ID,"device_code":p.device_code,
            "grant_type":"urn:ietf:params:oauth:grant-type:device_code"}))?;
        if let Some(credentials)=self.accept_poll(&response,now())?{save(&credentials)?;}
        self.status()
    }
    fn accept_poll(&mut self,response:&Value,time:u64)->Result<Option<Credentials>,String>{
        match response["error"].as_str(){
            Some("authorization_pending")=>Ok(None),
            Some("slow_down")=>{let p=self.pending.as_mut().ok_or("没有等待中的授权")?;p.interval=p.interval.saturating_add(5);p.next_poll=time.saturating_add(p.interval);Ok(None)},
            Some("access_denied")=>{self.pending=None;Err("你已取消GitHub授权，原有登录保持不变".into())},
            Some("expired_token")=>{self.pending=None;Err("授权码已到期，请重新获取".into())},
            Some(_)=>{self.pending=None;Err("GitHub未完成授权，请检查应用配置后重试".into())},
            None=>{let credentials=Credentials::from_response(response,time)?;self.pending=None;Ok(Some(credentials))},
        }
    }
    fn check(&mut self)->Result<Value,String>{
        let mut credentials=fresh_credentials()?;
        let response=client()?.get("https://api.github.com/user").header("Accept","application/vnd.github+json")
            .bearer_auth(&credentials.access_token).send().map_err(|_|"暂时无法连接GitHub，已保留本机凭据和离线数据")?;
        if response.status().as_u16()==401{credentials.reauthorize=true;save(&credentials)?;return Err("GitHub授权已失效，请重新登录；本地数据保持不变".into())}
        if !response.status().is_success(){return Err(format!("GitHub连接检查暂未成功（HTTP {}），登录信息已保留",response.status().as_u16()))}
        let user:Value=response.json().map_err(|_|"GitHub身份响应格式无效")?;
        let login=required_text(&user,"login")?;
        Ok(json!({"state":"connected","login":login,"dataAccess":false}))
    }
}

// Blocking network and DPAPI run off the WebView event thread. One process-wide auth
// lock serializes refresh rotation, polling, cancellation, and local logout.
static AUTH:Mutex<AuthState>=Mutex::new(AuthState{pending:None});
pub fn execute(action:&str)->Result<Value,String>{
    // Also exclude another desktop window/process during token refresh and atomic save.
    let _disk_lock=control::lock()?;
    let mut state=AUTH.lock().map_err(|_|"登录状态暂不可用，请重启窗口")?;
    match action{
        "status"=>state.status(),"begin"=>state.begin(),"poll"=>state.poll(),"check"=>state.check(),
        "cancel"=>{state.pending=None;state.status()},
        "forget"=>{control::clear_auth()?;state.pending=None;state.status()},
        _=>Err("未知登录操作".into()),
    }
}

fn fresh_credentials()->Result<Credentials,String>{
        let mut credentials=load()?.ok_or("请先登录GitHub")?;
        if credentials.needs_login(now()){return Err("GitHub登录已失效，请重新授权；本地工作副本保持不变".into())}
        if credentials.needs_refresh(now()){
            let response=oauth(TOKEN_URL,json!({"client_id":CLIENT_ID,"grant_type":"refresh_token",
                "refresh_token":credentials.refresh_token}))?;
            if response["error"].as_str().is_some(){
                if matches!(response["error"].as_str(),Some("bad_refresh_token"|"invalid_grant"|"expired_token")){
                    credentials.reauthorize=true;save(&credentials)?;
                }
                return Err("GitHub未完成凭据续期，请重试；若授权已撤销，请重新登录".into())
            }
            let rotated=Credentials::from_response(&response,now())?;
            // The server has invalidated the old token; persist the entire new pair atomically.
            save(&rotated).map_err(|_|"GitHub已续期，但本机保存失败，请重新授权".to_string())?;
            credentials=rotated;
        }

 Ok(credentials)
}

pub fn request(method:&str,path:&str,body:Option<Value>)->Result<(u16,Value),String>{
 if !(path.starts_with("repos/")||path.starts_with("user/"))||path.contains("://"){return Err("GitHub请求路径无效".into())}
 let _lock=control::lock()?;
 let mut credentials=fresh_credentials()?;
 let method=reqwest::Method::from_bytes(method.as_bytes()).map_err(|_|"请求类型无效")?;
 let mut request=client()?.request(method,format!("https://api.github.com/{path}")).header("Accept","application/vnd.github+json").bearer_auth(&credentials.access_token);
 if let Some(body)=body{request=request.json(&body);}
 let response=request.send().map_err(|_|"暂时无法连接GitHub，本地数据保持不变")?;
 let status=response.status().as_u16();
 if status==401{credentials.reauthorize=true;save(&credentials)?;return Err("GitHub授权已失效，请重新登录".into())}
 use std::io::Read;
 let mut bytes=Vec::new();response.take(22*1024*1024+1).read_to_end(&mut bytes).map_err(|_|"远端响应读取中断，请重新同步")?;
 if bytes.len()>22*1024*1024{return Err("远端响应超过安全大小，未应用数据".into())}
 let value=if status==204{Value::Null}else{serde_json::from_slice(&bytes).map_err(|_|"GitHub响应格式无效")?};
 Ok((status,value))
}
