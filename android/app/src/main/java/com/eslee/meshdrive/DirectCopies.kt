package com.eslee.meshdrive

import org.json.JSONObject
import java.io.File
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors

class DirectCopies(private val engine:MeshEngine):AutoCloseable {
    private data class Grant(val value:JSONObject,var used:Long)
    private val grants=mutableMapOf<String,Grant>()
    private val jobs=ConcurrentHashMap<String,JSONObject>()
    private val owners=ConcurrentHashMap<String,String>()
    private val worker=Executors.newSingleThreadExecutor()
    @Volatile private var stopped=false

    @Synchronized fun validate(token:String,target:String,share:String?=null,path:String?=null):JSONObject {
        val grant=grants[token]?:throw SecurityException("복사 권한이 없습니다")
        val value=grant.value
        if(System.currentTimeMillis()-grant.used>=900000||value.getString("targetDeviceId")!=target||
            (share!=null&&(value.getString("shareId")!=share||value.getString("path")!=path)))throw SecurityException("복사 권한이 만료되었거나 대상이 다릅니다")
        engine.access(value.getString("requesterId"),value.getString("shareId"),value.getString("path"),4)
        engine.access(target,value.getString("shareId"),value.getString("path"),4)
        grant.used=System.currentTimeMillis();return JSONObject(value.toString())
    }

    fun handle(r:HttpRequest,requester:String):HttpReply? {
        if(r.path.endsWith("/copy-authorize")){
            val request=JSONObject(r.body.toString(Charsets.UTF_8));val target=request.getString("targetDeviceId")
            engine.access(requester,request.getString("shareId"),request.getString("path"),4)
            engine.access(target,request.getString("shareId"),request.getString("path"),4)
            synchronized(this){
                grants.entries.removeIf{System.currentTimeMillis()-it.value.used>=900000}
                require(grants.size<256)
                val token=ByteArray(32).also{SecureRandom().nextBytes(it)}.joinToString(""){"%02X".format(it)}
                grants[token]=Grant(JSONObject().put("requesterId",requester).put("targetDeviceId",target)
                    .put("shareId",request.getString("shareId")).put("path",request.getString("path")),System.currentTimeMillis())
                return HttpReply.text(JSONObject().put("token",token).toString())
            }
        }
        if(r.path.endsWith("/copy-grant"))return HttpReply.text(validate(r.query["token"]!!,requester).toString())
        if(r.path.endsWith("/copy-progress")){
            val id=r.query["id"]!!;if(owners[id]!=requester)throw SecurityException()
            return HttpReply.text(jobs[id]?.toString()?:throw SecurityException())
        }
        if(!r.path.endsWith("/copy-receive"))return null
        val request=JSONObject(r.body.toString(Charsets.UTF_8))
        val share=request.getString("shareId");val path=request.getString("path")
        engine.access(requester,share,path,8)
        val source=engine.peers[request.getString("sourceDeviceId")]?:throw java.io.IOException("원본 기기를 찾을 수 없습니다")
        val token=request.getString("token")
        val grant=engine.objectRequest(source,"/v1/secure/storage/copy-grant?token=${java.net.URLEncoder.encode(token,"UTF-8")}")
        if(grant.getString("requesterId")!=requester||grant.getString("targetDeviceId")!=engine.security.id)throw SecurityException()
        PairingProtocol.safeParts(grant.getString("path"))
        val id=java.util.UUID.randomUUID().toString().replace("-","")
        val name=grant.getString("path").substringAfterLast('/')
        owners[id]=requester
        fun progress(state:String,done:Long=0,total:Long=0){jobs[id]=JSONObject().put("id",id).put("name",name).put("state",state).put("completedBytes",done).put("totalBytes",total)}
        progress("대기")
        worker.execute {
            try {
                val check={check(!stopped);engine.access(requester,share,path,8);Unit}
                check()
                val staged=AndroidTransfers.download(engine,source,grant.getString("shareId"),grant.getString("path"),File(engine.context.filesDir,"direct-parts"),token,check){done,total->progress("복사 중",done,total)}
                val parent=engine.access(requester,share,path,8)
                var finalName=name;var index=1
                while(parent.findFile(finalName)!=null){val f=File(name);finalName="${f.nameWithoutExtension} (${index++}).${f.extension}"}
                val output=parent.createFile("application/octet-stream",".meshdrive-$id.part")?:throw java.io.IOException()
                try {
                    DocumentCopies.publishNew(engine.context,staged,output.uri,check)
                    check();check(parent.findFile(finalName)==null&&output.renameTo(finalName))
                    progress("완료",staged.length(),staged.length());staged.delete()
                }catch(e:Exception){output.delete();throw e}
            }catch(_:Exception){progress("중단 · 권한과 연결을 확인하고 다시 시도하세요")}
        }
        return HttpReply.text(JSONObject().put("id",id).toString())
    }
    override fun close(){stopped=true;worker.shutdownNow()}
}
