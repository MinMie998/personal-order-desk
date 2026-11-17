<script setup lang="ts">
import {ref} from 'vue';
import {invoke} from '@tauri-apps/api/core';
const native=!!(window as any).__TAURI_INTERNALS__;
const busy=ref(false),error=ref(''),message=ref(''),release=ref<any>(null),current=ref('');
const api='https://api.github.com/repos/MinMie998/personal-order-desk/contents/updates/latest.json';
function valid(url:string){return typeof url==='string'&&url.startsWith('https://github.com/MinMie998/personal-order-desk/releases/')&&!/[\s\x00-\x1f]/.test(url)}
function newer(a:string,b:string){const parse=(s:string)=>{const m=/^(\d+)\.(\d+)\.(\d+)(?:-(.+))?$/.exec(s);if(!m)throw new Error('版本号无效');return m};const x=parse(a),y=parse(b);for(let i=1;i<=3;i++){if(Number(x[i])!==Number(y[i]))return Number(x[i])>Number(y[i])}if(!x[4]||!y[4])return !!y[4]&&!x[4];return x[4].localeCompare(y[4],undefined,{numeric:true})>0}
async function check(){busy.value=true;error.value='';message.value='';try{
 if(native){const result:any=await invoke('check_public_update');current.value=result.currentVersion;release.value=result.manifest??null;message.value=result.message??(result.available?'有新版本可下载':'当前已是最新发布版本');}
 else{const health=await fetch('/healthz').then(r=>r.json());current.value=health.version??'';const response=await fetch(api,{headers:{Accept:'application/vnd.github+json'}});if(response.status===404){message.value='发行信息尚未发布';return}if(!response.ok)throw new Error('GitHub暂时无法提供更新');const value=await response.json();if(typeof value.content!=='string'||value.content.length>131072)throw new Error('更新信息不完整');const text=new TextDecoder().decode(Uint8Array.from(atob(value.content.replace(/\s/g,'')),c=>c.charCodeAt(0)));const manifest=JSON.parse(text);if(manifest.format!=='personal-order-desk.update'||manifest.version!==1||manifest.product!=='PersonalOrderDesk'||!valid(manifest.windows?.url))throw new Error('更新来源不兼容');release.value=manifest;message.value=newer(manifest.appVersion,current.value)?'有新版本可下载':'当前已是最新发布版本';}
 }catch(e){error.value=String(e)}finally{busy.value=false}}
async function open(url:string){if(!valid(url))return;try{if(native)await invoke('open_release_url',{url});else window.open(url,'_blank','noopener')}catch(e){error.value=String(e)}}
</script>
<template><section class="card"><div class="section-heading"><div><h2>版本与更新</h2><p>从本项目 GitHub Releases 获取程序更新，账本与登录信息保存在本机。</p></div></div><div class="settings-content"><div class="row-actions"><button :disabled="busy" @click="check">{{busy?'正在检查…':'检查程序更新'}}</button><button v-if="release?.windows?.url" @click="open(release.windows.url)">下载 Windows 版</button><button v-if="release?.android?.url" @click="open(release.android.url)">下载 Android 版</button></div><p v-if="current">当前版本 {{current}}<span v-if="release"> · 最新版本 {{release.appVersion}}</span></p><p v-if="message">{{message}}</p><p v-if="error" class="error-banner">{{error}}</p><p class="muted">更新前可在上方备份账本。Windows 请退出后台、关闭窗口后替换程序文件；无需移动或重建账本。</p></div></section></template>
