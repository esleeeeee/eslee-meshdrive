package com.eslee.meshdrive

import org.json.JSONObject

object ShareAccess {
    fun permissions(share:JSONObject,device:String?):Int {
        val default=share.getInt("permissions")
        return if(device==null)default else share.optJSONObject("deviceOverrides")?.optInt(device,default)?:default
    }
    fun require(share:JSONObject,device:String?,requested:Int){
        if(permissions(share,device) and requested != requested)throw SecurityException("공유 권한이 없습니다")
    }
}
