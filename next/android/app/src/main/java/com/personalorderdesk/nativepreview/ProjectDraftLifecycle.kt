package com.personalorderdesk.nativepreview

import androidx.activity.ComponentActivity
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

@Composable internal fun KeepProjectDraft(session:ProjectDraftSession,draft:ProjectDraft,onError:(String)->Unit) {
    val latest by rememberUpdatedState(draft)
    val activity=LocalContext.current as ComponentActivity
    LaunchedEffect(draft){
        delay(350)
        try{withContext(Dispatchers.IO){session.write(draft)}}catch(e:Exception){onError(e.message?:"草稿未能暂存，请直接保存项目")}
    }
    DisposableEffect(activity,session){
        fun flush(){runCatching{session.write(latest)}.onFailure{onError(it.message?:"草稿未能暂存")}}
        val observer=LifecycleEventObserver{_,event->if(event==Lifecycle.Event.ON_STOP)flush()}
        activity.lifecycle.addObserver(observer)
        onDispose{activity.lifecycle.removeObserver(observer);flush()}
    }
}
