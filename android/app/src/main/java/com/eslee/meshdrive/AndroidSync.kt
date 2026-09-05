package com.eslee.meshdrive

import android.net.Uri
import androidx.documentfile.provider.DocumentFile
import org.json.JSONArray
import org.json.JSONObject
import java.io.*
import java.security.MessageDigest
import java.time.Instant
import java.util.UUID

/** Windows drives the sync schedule; Android exposes only explicitly approved SAF roots. */
class AndroidSync(private val context:android.content.Context,private val trusted:(String)->Boolean,private val paused:()->Boolean,
                  private val resolveTree:(Uri)->DocumentFile?={DocumentFile.fromTreeUri(context,it)}) {
    private val preferences=context.getSharedPreferences("sync",0)
    private val roots=JSONArray(preferences.getString("roots","[]")!!)
    private val inbox=File(context.filesDir,"sync-inbox").apply{mkdirs()}
    private val archives=File(context.filesDir,"sync-versions").apply{mkdirs()}
    private val chunkSize=8*1024*1024
    @Synchronized fun snapshot()=JSONArray(roots.toString())
    @Synchronized fun add(uri:Uri,name:String,devices:List<String>){
        require(name.isNotBlank())
        require(resolveTree(uri)?.isDirectory==true)
        roots.put(JSONObject().put("id",UUID.randomUUID().toString().replace("-","")).put("name",name)
            .put("uri",uri.toString()).put("allowedDevices",JSONArray(devices)))
        saveRoots()
    }
    @Synchronized fun remove(id:String){val index=(0 until roots.length()).firstOrNull{roots.getJSONObject(it).getString("id")==id}?:return;roots.remove(index);saveRoots()}
    private fun saveRoots(){check(preferences.edit().putString("roots",roots.toString()).commit())}
    @Synchronized private fun root(id:String,device:String?):DocumentFile {
        if(device!=null&&(paused()||!trusted(device)))throw SecurityException()
        val root=(0 until roots.length()).map{roots.getJSONObject(it)}.firstOrNull{it.getString("id")==id}?:throw SecurityException("동기화가 설정되지 않은 폴더")
        val allowed=root.getJSONArray("allowedDevices")
        if(device!=null&&(0 until allowed.length()).none{allowed.getString(it)==device})throw SecurityException("동기화가 허용되지 않은 기기")
        return resolveTree(Uri.parse(root.getString("uri")))?:throw IOException()
    }
    private fun parts(path:String)=PairingProtocol.safeParts(path).also{require(it.isNotEmpty()&&it.none{p->p.startsWith('.')})}
    private fun parent(id:String,path:String,device:String?,create:Boolean):DocumentFile? {
        val parts=parts(path);var directory=root(id,device)
        for(part in parts.dropLast(1)){
            directory=directory.findFile(part)?:if(create)directory.createDirectory(part)?:throw IOException() else return null
            require(directory.isDirectory)
        }
        return directory
    }
    private fun document(id:String,path:String,device:String?)=parent(id,path,device,false)?.findFile(parts(path).last())
    private fun hash(input:InputStream):String {
        val digest=MessageDigest.getInstance("SHA-256");val buffer=ByteArray(65536)
        while(true){val n=input.read(buffer);if(n<0)break;digest.update(buffer,0,n)}
        return digest.digest().joinToString(""){"%02X".format(it)}
    }
    private fun hash(file:File)=file.inputStream().use{hash(it)}
    private fun hash(doc:DocumentFile)=context.contentResolver.openInputStream(doc.uri)!!.use{hash(it)}
    private fun current(id:String,path:String,device:String?):String?=document(id,path,device)?.let{require(it.isFile);hash(it)}
    private fun write(file:File,json:JSONObject)=AtomicFiles.writeText(file,json.toString())
    @Synchronized fun versions(id:String):List<JSONObject>{root(id,null);return archives.listFiles().orEmpty().filter{it.extension=="json"}.map{JSONObject(it.readText())}.filter{it.getString("rootId")==id}.sortedByDescending{it.getString("createdAt")}}
    fun retentionCount()=preferences.getInt("count",20)
    fun retentionDays()=preferences.getInt("days",30)
    fun retention(count:Int,days:Int){require(count in 1..1000&&days in 1..3650);check(preferences.edit().putInt("count",count).putInt("days",days).commit())}
    private fun preserve(id:String,path:String,doc:DocumentFile,hash:String):File {
        val key=UUID.randomUUID().toString().replace("-","");val file=File(archives,"$key.bin")
        context.contentResolver.openInputStream(doc.uri)!!.use{input->file.outputStream().use{input.copyTo(it)}}
        check(hash(file)==hash){"이전 버전 보관 실패"}
        write(File(archives,"$key.json"),JSONObject().put("id",key).put("rootId",id).put("path",path).put("hash",hash).put("createdAt",Instant.now().toString()).put("size",file.length()))
        return file
    }
    @Synchronized private fun apply(id:String,path:String,expected:String?,source:File?,newHash:String?,device:String?){
        root(id,device)
        val previous=document(id,path,device);val oldHash=previous?.let{require(it.isFile);hash(it)}
        check(oldHash==expected){"대상 파일이 독립적으로 변경되었습니다"}
        if(source!=null)check(hash(source)==newHash){"동기화 무결성 오류"}
        if(oldHash==newHash)return
        val saved=if(previous!=null)preserve(id,path,previous,oldHash!!)else null
        val directory=parent(id,path,device,source!=null)?:return
        if(source==null){root(id,device);check(current(id,path,device)==expected);check(previous?.delete()!=false);trim(id,path);return}
        val temp=directory.createFile("application/octet-stream",".meshdrive-sync-${UUID.randomUUID()}.part")?:throw IOException()
        try {
            context.contentResolver.openOutputStream(temp.uri,"w")!!.use{out->source.inputStream().use{it.copyTo(out)}}
            check(hash(temp)==newHash){"저장된 동기화 파일 무결성 오류"}
            root(id,device);check(current(id,path,device)==expected){"동기화 중 원본이 변경되었습니다"}
            check(previous?.delete()!=false)
            val name=parts(path).last()
            if(directory.findFile(name)!=null||!temp.renameTo(name)){
                if(saved!=null&&directory.findFile(name)==null){
                    val restored=directory.createFile("application/octet-stream",name)?:throw IOException("이전 버전에서 수동 복원이 필요합니다")
                    context.contentResolver.openOutputStream(restored.uri,"w")!!.use{out->saved.inputStream().use{it.copyTo(out)}}
                    check(hash(restored)==expected){"이전 버전에서 수동 복원이 필요합니다"}
                }
                throw IOException("파일 교체 실패. 이전 버전이 보관되어 있습니다")
            }
        }catch(e:Exception){temp.delete();throw e}
        trim(id,path)
    }
    private fun trim(id:String,path:String){
        versions(id).filter{it.getString("path")==path}.forEachIndexed{index,v->
            if(index>0&&(index>=retentionCount()||Instant.parse(v.getString("createdAt")).isBefore(Instant.now().minusSeconds(retentionDays()*86400L)))){
                val key=v.getString("id");require(key.matches(Regex("[0-9a-f]{32}")))
                File(archives,"$key.bin").delete();File(archives,"$key.json").delete()
            }
        }
    }
    @Synchronized fun restore(id:String,version:String){
        val metadata=versions(id).first{it.getString("id")==version};require(version.matches(Regex("[0-9a-f]{32}")))
        val path=metadata.getString("path");apply(id,path,current(id,path,null),File(archives,"$version.bin"),metadata.getString("hash"),null)
    }
    private fun nullable(json:JSONObject,key:String)=if(json.isNull(key))null else json.getString(key)
    private fun ticketFile(id:String,extension:String):File{require(id.matches(Regex("[0-9a-fA-F]{64}")));return File(inbox,id+extension)}
    private fun envelope(id:String,device:String):JSONObject {
        val saved=JSONObject(ticketFile(id,".json").readText());if(saved.getString("device")!=device)throw SecurityException()
        root(saved.getJSONObject("request").getString("rootId"),device);return saved
    }
    @Synchronized fun handle(r:HttpRequest,device:String):HttpReply {
        if(r.path.endsWith("/roots")){
            val visible=JSONArray();for(i in 0 until roots.length()){
                val item=roots.getJSONObject(i);try{root(item.getString("id"),device);visible.put(JSONObject().put("id",item.getString("id")).put("name",item.getString("name")))}catch(_:SecurityException){}
            };return HttpReply.text(visible.toString())
        }
        if(r.path.endsWith("/inventory")){
            val start=root(r.query["rootId"]!!,device);val entries=JSONArray();val pending=java.util.ArrayDeque<Pair<DocumentFile,String>>();pending.add(start to "")
            while(pending.isNotEmpty()){
                val (directory,prefix)=pending.removeFirst()
                for(doc in directory.listFiles().filter{!it.name.orEmpty().startsWith('.')}){
                    val path=prefix+doc.name;parts(path)
                    if(doc.isDirectory)pending.add(doc to "$path/")else entries.put(JSONObject().put("path",path).put("size",doc.length()).put("hash",hash(doc)))
                    require(entries.length()+pending.size<=100000)
                }
            };return HttpReply.text(entries.toString())
        }
        if(r.path.endsWith("/content")){
            val doc=document(r.query["rootId"]!!,r.query["path"]!!,device)?:throw FileNotFoundException()
            val digest=hash(doc);if(digest!=r.query["hash"])return HttpReply.text("",412)
            val length=doc.length();var start=0L;var end=length-1;var code=200
            val headers=mutableMapOf("Accept-Ranges" to "bytes","ETag" to "\"$digest\"")
            r.headers["range"]?.let{range->
                val match=Regex("bytes=(\\d+)-(\\d*)").matchEntire(range)?:return HttpReply.text("",416)
                start=match.groupValues[1].toLong();end=if(match.groupValues[2].isEmpty())end else minOf(end,match.groupValues[2].toLong())
                if(start>end||start>=length)return HttpReply(416,headers=mapOf("Content-Range" to "bytes */$length"))
                code=206;headers["Content-Range"]="bytes $start-$end/$length"
            }
            val input=context.contentResolver.openInputStream(doc.uri)?:throw IOException()
            try{StorageApi.skip(input,start)}catch(e:Exception){input.close();throw e}
            return HttpReply(code,"application/octet-stream",maxOf(0,end-start+1),input,headers)
        }
        if(r.path.endsWith("/delete")){
            val request=JSONObject(r.body.toString(Charsets.UTF_8));apply(request.getString("rootId"),request.getString("path"),request.getString("expectedHash"),null,null,device)
            return HttpReply.text("{}")
        }
        if(r.path.endsWith("/upload-start")){
            val request=JSONObject(r.body.toString(Charsets.UTF_8));val root=request.getString("rootId");val path=request.getString("path")
            val hash=request.getString("newHash");val size=request.getLong("size");require(size>=0&&hash.matches(Regex("[0-9A-F]{64}")))
            val current=current(root,path,device);val expected=nullable(request,"expectedHash")
            val key=MessageDigest.getInstance("SHA-256").digest((device+"|"+root+"|"+path+"|"+expected+"|"+hash+"|"+size).toByteArray()).joinToString(""){"%02x".format(it)}
            if(current==hash)return HttpReply.text(JSONObject().put("id",key).put("offset",size).put("completed",true).toString())
            check(current==expected){"대상 파일이 변경되었습니다"}
            write(ticketFile(key,".json"),JSONObject().put("device",device).put("request",request))
            val offset=RandomAccessFile(ticketFile(key,".part"),"rw").use{file->var safe=minOf(file.length(),size);if(safe!=size)safe-=safe%chunkSize;file.setLength(safe);file.fd.sync();safe}
            return HttpReply.text(JSONObject().put("id",key).put("offset",offset).put("completed",false).toString())
        }
        if(r.path.endsWith("/upload-chunk")){
            val id=r.query["id"]!!;val request=envelope(id,device).getJSONObject("request");val offset=r.query["offset"]!!.toLong()
            require(offset>=0&&offset%chunkSize==0L&&r.body.isNotEmpty()&&r.body.size.toLong()==minOf(chunkSize.toLong(),request.getLong("size")-offset))
            RandomAccessFile(ticketFile(id,".part"),"rw").use{file->check(file.length()==offset);file.seek(offset);file.write(r.body);file.fd.sync()}
            return HttpReply.text("{}")
        }
        if(r.path.endsWith("/upload-complete")){
            val id=r.query["id"]!!;val request=envelope(id,device).getJSONObject("request");val part=ticketFile(id,".part")
            val root=request.getString("rootId");val path=request.getString("path");val expected=nullable(request,"expectedHash");val digest=request.getString("newHash")
            if(current(root,path,device)==digest)return HttpReply.text("{}")
            if(part.length()!=request.getLong("size")||hash(part)!=digest){part.outputStream().close();throw IOException("동기화 무결성 오류. 재전송 필요")}
            apply(root,path,expected,part,digest,device);part.delete();return HttpReply.text("{}")
        }
        return HttpReply.text("",404)
    }
}
