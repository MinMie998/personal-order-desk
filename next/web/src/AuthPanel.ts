import {defineComponent,h,onMounted,onUnmounted,ref} from 'vue';
import {invoke} from '@tauri-apps/api/core';
import Icon from './Icon.vue';
import './style.css';
type AuthStatus={state:string;login?:string;pending?:{userCode:string;verificationUrl:string;expiresIn:number;retryAfter:number};dataAccess:boolean};
export default defineComponent({setup(){
 const status=ref('尚未检查本机后台');const busy=ref(false);
 const auth=ref<AuthStatus|null>(null);const authBusy=ref(false);const authError=ref('');
 const copyNotice=ref('');
 const native='__TAURI_INTERNALS__' in window;
 let timer:ReturnType<typeof setTimeout>|undefined;let disposed=false;
 async function run(command:string){busy.value=true;try{await invoke(command);status.value=command==='stop_service'?'后台已退出，重新打开工作台后可继续使用。':'本机后台连接正常。'}catch(error){status.value=String(error)}finally{busy.value=false}}
 async function account(action:string){
  if(timer)clearTimeout(timer);authBusy.value=true;authError.value='';if(action==='begin')copyNotice.value='';
  try{auth.value=await invoke<AuthStatus>('github_auth',{action})}
  catch(error){authError.value=String(error);try{auth.value=await invoke<AuthStatus>('github_auth',{action:'status'})}catch{auth.value=null}}
  finally{
   authBusy.value=false;
   if(!disposed&&auth.value?.pending&&!['cancel','forget'].includes(action)){
    timer=setTimeout(()=>account('poll'),Math.max(auth.value.pending.retryAfter,5)*1000);
   }
  }
 }
 async function openLogin(){try{await invoke('open_github_login')}catch(error){authError.value=String(error)}}
 async function copyCode(){
  const code=auth.value?.pending?.userCode;if(!code)return;
  try{
   try{await navigator.clipboard.writeText(code)}catch{
    const field=document.createElement('textarea');field.value=code;field.style.position='fixed';field.style.opacity='0';field.setAttribute('readonly','');document.body.append(field);
    const previous=document.activeElement;try{field.select();if(!document.execCommand('copy'))throw new Error('copy unavailable')}finally{field.remove();if(previous instanceof HTMLElement)previous.focus()}
   }
   copyNotice.value='授权码已复制，前往 GitHub 粘贴即可。';
  }catch{copyNotice.value='复制失败，请选中授权码手动复制。'}
 }
 const labels:Record<string,string>={'signed-out':'尚未登录',saved:'已保存登录，可检查连接','refresh-needed':'登录需要续期，连接时自动处理',reauthorize:'授权已失效，请重新登录',connected:'已连接 GitHub'};
 onMounted(()=>{if(native)void account('status')});
 onUnmounted(()=>{disposed=true;if(timer)clearTimeout(timer)});
 return ()=>h('div',{class:'account-settings'},[
  
  h('section',[h('h2','GitHub 账户'),h('p','连接 GitHub 账户。登录仅连接账户，上传由远端同步区单独操作。'),
   native?h('div',[
    h('p',{class:'account-state','aria-live':'polite'},auth.value?`${labels[auth.value.state]??auth.value.state}${auth.value.login?` · ${auth.value.login}`:''}`:'正在读取本机登录状态…'),
    auth.value?.pending?h('div',{class:'authorization'},[
     h('p','在 GitHub 官方页面输入下面的授权码，核对应用名称后确认。'),
     h('div',{class:'device-code-row'},[
      h('strong',{class:'device-code'},auth.value.pending.userCode),
      h('button',{type:'button',onClick:copyCode},[h(Icon,{name:'copy'}),'复制授权码'])]),
     copyNotice.value?h('p',{class:'code-copy-notice',role:'status','aria-live':'polite'},copyNotice.value):null,
     h('p','https://github.com/login/device'),
     h('div',{class:'steps'},[
      h('button',{onClick:openLogin},'打开 GitHub 官方授权页'),
      h('button',{disabled:authBusy.value,onClick:()=>account('cancel')},'取消本次授权')]),
     h('small','程序会按 GitHub 规定的间隔检查结果。关闭窗口会取消等待，已有登录不受影响。')
    ]):h('div',{class:'steps'},[
     h('button',{disabled:authBusy.value,onClick:()=>account('begin')},auth.value?.state==='signed-out'?'登录 GitHub':'重新授权'),
     h('button',{disabled:authBusy.value||!auth.value||auth.value.state==='signed-out',onClick:()=>account('check')},authBusy.value?'正在处理…':'检查连接与续期'),
     h('button',{disabled:authBusy.value||!auth.value||auth.value.state==='signed-out',onClick:()=>account('forget')},'移除本机登录')]),
    authError.value?h('p',{class:'error',role:'alert'},authError.value):null,
    h('p',{class:'hint'},'凭据使用 Windows 当前用户加密保存，不会传入网页。移除本机登录仅清除本机凭据，不撤销 GitHub 上的授权。')
   ]):h('p','账户授权请在 Windows 客户端中完成。')]),
  h('details',{class:'card background-settings'},[h('summary','本机后台管理'),h('p','当前连接新版独立工作空间。关闭窗口保留后台，退出后台后需重新打开工作台。'),
   native?h('div',{class:'steps'},[['start_service','启动或连接后台'],['service_status','检查后台'],['stop_service','退出本机后台']].map(([command,label])=>h('button',{disabled:busy.value,onClick:()=>run(command)},label))):h('p','浏览器模式：请在 Tauri 窗口验证后台控制。'),
   h('p',{'aria-live':'polite',class:'hint'},status.value)]),
  h('small','账户连接与账本同步分别管理，以同步记录中的远端确认时间为准。')]);
}});
