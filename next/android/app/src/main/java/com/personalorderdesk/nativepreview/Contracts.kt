package com.personalorderdesk.nativepreview
fun parseCents(text:String):Long {
 require(Regex("-?(0|[1-9][0-9]*)").matches(text)&&text!="-0"){"invalid_cents"}
 return text.toLong()
}
data class FieldMerge(val fields:Map<String,String>,val conflicts:List<String>)
fun mergeFields(base:Map<String,String>,local:Map<String,String>,remote:Map<String,String>):FieldMerge {
 val output=sortedMapOf<String,String>();val conflicts=mutableListOf<String>()
 (base.keys+local.keys+remote.keys).sorted().forEach { key->
 val b=base[key];val l=local[key];val r=remote[key]
 if(l==r){l?.let{output[key]=it}}else if(l==b){r?.let{output[key]=it}}else if(r==b){l?.let{output[key]=it}}else{conflicts+=key}
 };return FieldMerge(output,conflicts)
}
