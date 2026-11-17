package com.personalorderdesk.nativepreview;

import android.app.Activity;
import android.app.Instrumentation;
import android.os.Bundle;
import android.os.Build;
import org.json.JSONObject;
import java.lang.reflect.Method;
import java.util.Base64;

/** Synthetic-only device probe, independent of the target's pruned Kotlin runtime. */
public class SyncCoreProbe extends Instrumentation {
    private boolean syncSuite;
    private Object core;
    private Method execute;
    private JSONObject call(JSONObject request) throws Exception {
        return (JSONObject)execute.invoke(core,request);
    }
    @Override public void onCreate(Bundle arguments){super.onCreate(arguments);syncSuite=arguments!=null&&"sync".equals(arguments.getString("suite"));start();}
    @Override public void onStart(){
        Bundle result=new Bundle();
        try{
            Class<?> type=Class.forName("com.personalorderdesk.nativepreview.sync.NativeSync",true,getTargetContext().getClassLoader());
            core=type.getField("INSTANCE").get(null);
            execute=type.getMethod("execute",JSONObject.class);
            JSONObject snapshot=new JSONObject().put("format",2).put("generation","legacy")
                .put("workspaceId","11111111-1111-4111-8111-111111111111")
                .put("orders",new JSONObject()).put("money",new JSONObject()).put("history",new JSONObject()).put("preferences",new JSONObject());
            String expected="07f85dd93a959ba512a2a6079e9ee90c0ad7dd66db4d329f17872f3ea03fa9b3";
            String digest=call(new JSONObject().put("operation","digest").put("snapshot",snapshot)).getString("digest");
            if(!expected.equals(digest))throw new IllegalStateException("Synthetic digest mismatch");
            // Public all-zero synthetic key, unrelated to the user's recovery file.
            String key=Base64.getEncoder().encodeToString(new byte[32]);
            JSONObject encrypted=call(new JSONObject().put("operation","encrypt").put("snapshot",snapshot).put("key",key));
            JSONObject decrypted=call(new JSONObject().put("operation","decrypt").put("envelope",encrypted).put("key",key).put("workspaceId",snapshot.getString("workspaceId")));
            if(!expected.equals(decrypted.getString("digest")))throw new IllegalStateException("Synthetic encryption mismatch");
            result.putString("result","PASS: native digest and AES-GCM roundtrip");
            result.putString("preferredAbi",Build.SUPPORTED_ABIS[0]);
            javax.net.ssl.HttpsURLConnection connection=(javax.net.ssl.HttpsURLConnection)new java.net.URL("https://api.github.com/").openConnection();
            connection.setRequestMethod("PATCH");connection.disconnect();
            result.putString("patchMethod","supported (no request sent)");
            if(syncSuite){
                Class<?> scenario=Class.forName("com.personalorderdesk.nativepreview.DeviceSyncScenario",true,getTargetContext().getClassLoader());
                result.putString("syncSuite",String.valueOf(scenario.getMethod("run",android.content.Context.class).invoke(null,getTargetContext())));
            }
            finish(Activity.RESULT_OK,result);
        }catch(Throwable failure){
            Throwable e=failure.getCause()==null?failure:failure.getCause();
            result.putString("result","FAIL: "+e.getClass().getSimpleName());
            result.putString("syntheticProbeError",e.toString());
            finish(Activity.RESULT_CANCELED,result);
        }
    }
}
