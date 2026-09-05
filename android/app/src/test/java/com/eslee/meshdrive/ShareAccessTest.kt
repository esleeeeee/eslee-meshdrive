package com.eslee.meshdrive

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ShareAccessTest {
    @Test fun deviceOverridesNeverGrantAnotherDevicesPermissions(){
        val share=JSONObject().put("permissions",7).put("deviceOverrides",JSONObject().put("blocked",0).put("writer",15).put("player",3))
        assertEquals(7,ShareAccess.permissions(share,"ordinary"))
        ShareAccess.require(share,"writer",8)
        ShareAccess.require(share,"player",2)
        for((device,permission) in listOf("blocked" to 1,"ordinary" to 8,"player" to 4)){
            try{ShareAccess.require(share,device,permission);fail(device)}catch(_:SecurityException){}
        }
    }
}
