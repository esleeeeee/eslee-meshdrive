package com.eslee.meshdrive

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.documentfile.provider.DocumentFile
import org.json.JSONObject
import java.io.*
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.Base64
import com.eslee.quicksend.engine.MerkleAccumulator

class StorageApi(private val context:Context,private val document:(String,String,Int)->DocumentFile){
    private val uploads=File(context.filesDir,"uploads").apply{mkdirs()}
    private val chunkSize=8*1024*1024
    private fun version(d:DocumentFile)="${d.length().toString(16)}-${d.lastModified().toString(16)}"
    fun get(r:HttpRequest):HttpReply?{
        val share=r.query["shareId"]?:return null;val path=r.query["path"].orEmpty()
        if(r.path.endsWith("/manifest")){val d=document(share,path,4);val merkle=MerkleAccumulator();context.contentResolver.openInputStream(d.uri)!!.use{input->val buffer=ByteArray(chunkSize);while(true){val n=readChunk(input,buffer);if(n==0)break;merkle.addChunk(buffer.copyOf(n))}};return HttpReply.text(JSONObject().put("size",d.length()).put("modifiedUtcTicks",d.lastModified()*10000+621355968000000000L).put("version",version(d)).put("chunkSize",chunkSize).put("leafCount",merkle.leafCount).put("merkleRoot",Base64.getEncoder().encodeToString(merkle.root())).toString())}
        if(r.path.endsWith("/chunk")){val d=document(share,path,4);if(r.query["version"]!=version(d))return HttpReply.text("",412);val offset=r.query["offset"]!!.toLong();require(offset>=0&&offset<d.length()&&offset%chunkSize==0L);val id=r.query["fileId"]!!.replace("-","");require(id.matches(Regex("[0-9a-fA-F]{32}")));val data=context.contentResolver.openInputStream(d.uri)!!.use{input->skip(input,offset);val bytes=ByteArray(minOf(chunkSize.toLong(),d.length()-offset).toInt());DataInputStream(input).readFully(bytes);bytes};val payload=ByteArray(data.size+60);val header=ByteBuffer.wrap(payload);header.put(id.chunked(2).map{it.toInt(16).toByte()}.toByteArray());header.putLong(offset);header.putInt(data.size);header.put(MessageDigest.getInstance("SHA-256").digest(data));header.put(data);return HttpReply(200,"application/octet-stream",payload.size.toLong(),ByteArrayInputStream(payload))}
        if(r.path.endsWith("/thumbnail")){val d=document(share,path,2);val dir=File(context.cacheDir,"thumbnails").apply{mkdirs()};val key=hash(share+path+version(d));val file=File(dir,"$key.jpg");if(!file.exists()){val bounds=BitmapFactory.Options().apply{inJustDecodeBounds=true};context.contentResolver.openInputStream(d.uri)!!.use{BitmapFactory.decodeStream(it,null,bounds)};require(bounds.outWidth>0&&bounds.outHeight>0);var sample=1;while(maxOf(bounds.outWidth,bounds.outHeight)/sample>512)sample*=2;val bitmap=context.contentResolver.openInputStream(d.uri)!!.use{BitmapFactory.decodeStream(it,null,BitmapFactory.Options().apply{inSampleSize=sample})}?:throw IOException();val scale=minOf(1.0,256.0/maxOf(bitmap.width,bitmap.height));val small=Bitmap.createScaledBitmap(bitmap,maxOf(1,(bitmap.width*scale).toInt()),maxOf(1,(bitmap.height*scale).toInt()),true);file.outputStream().use{small.compress(Bitmap.CompressFormat.JPEG,80,it)};if(small!=bitmap)small.recycle();bitmap.recycle()};file.setLastModified(System.currentTimeMillis());trim(dir,256L*1024*1024,file);return HttpReply(200,"image/jpeg",file.length(),file.inputStream(),mapOf("ETag" to "\"$key\""))}
        return null
    }
    @Synchronized fun upload(r:HttpRequest,device:String):HttpReply?{
        if(r.path.endsWith("/upload-start")){val request=JSONObject(r.body.toString(Charsets.UTF_8));val m=request.getJSONObject("manifest");val name=request.getString("name");require(name.isNotBlank()&&name==name.substringAfterLast('/')&&!name.contains('\\')&&!name.contains(':'));require(m.getLong("size")>=0&&m.getInt("chunkSize")==chunkSize);require(Base64.getDecoder().decode(m.getString("merkleRoot")).size==32);document(request.getString("shareId"),request.getString("path"),8);val id=hash(device+request.toString()).take(32);val state=File(uploads,"$id.json");if(!state.exists())write(state,JSONObject().put("device",device).put("request",request).put("offset",0));val saved=JSONObject(state.readText());return HttpReply.text(JSONObject().put("id",id).put("offset",saved.getLong("offset")).toString())}
        if(!r.path.endsWith("/upload-chunk")&&!r.path.endsWith("/upload-complete"))return null
        val id=r.query["id"]!!.replace("-","");require(id.matches(Regex("[0-9a-f]{32}")));val state=File(uploads,"$id.json");val saved=JSONObject(state.readText());if(saved.getString("device")!=device)throw SecurityException();val request=saved.getJSONObject("request");val parent=document(request.getString("shareId"),request.getString("path"),8);val m=request.getJSONObject("manifest");val partial=File(uploads,"$id.part");if(saved.optBoolean("completed"))return HttpReply.text("{}")
        if(r.path.endsWith("/upload-chunk")){val raw=r.body;require(raw.size>60&&raw.size<=chunkSize+60);val h=ByteBuffer.wrap(raw);require(raw.copyOfRange(0,16).joinToString(""){"%02x".format(it)}==id);val offset=h.getLong(16);val length=h.getInt(24);require(offset==saved.getLong("offset")&&length==raw.size-60&&length==minOf(chunkSize.toLong(),m.getLong("size")-offset).toInt());val bytes=raw.copyOfRange(60,raw.size);require(MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(bytes),raw.copyOfRange(28,60)));RandomAccessFile(partial,"rw").use{it.setLength(offset);it.seek(offset);it.write(bytes);it.fd.sync()};saved.put("offset",offset+length);write(state,saved);return HttpReply.text("{}")}
        require(saved.getLong("offset")==m.getLong("size"));if(!partial.exists()&&m.getLong("size")==0L)partial.createNewFile();val merkle=MerkleAccumulator();partial.inputStream().use{input->val buffer=ByteArray(chunkSize);while(true){val n=readChunk(input,buffer);if(n==0)break;merkle.addChunk(buffer.copyOf(n))}};require(MessageDigest.isEqual(merkle.root(),Base64.getDecoder().decode(m.getString("merkleRoot"))));var name=request.getString("name");var index=1;while(parent.findFile(name)!=null){val original=File(request.getString("name"));name="${original.nameWithoutExtension} (${index++}).${original.extension}"};val target=parent.createFile("application/octet-stream",name)?:throw IOException();try{context.contentResolver.openOutputStream(target.uri,"w")!!.use{out->partial.inputStream().use{it.copyTo(out)}};saved.put("completed",true).put("uri",target.uri.toString());write(state,saved);partial.delete()}catch(e:Exception){target.delete();throw e};return HttpReply.text("{}")
    }
    private fun write(file:File,value:JSONObject)=AtomicFiles.writeText(file,value.toString())
    private fun hash(text:String)=MessageDigest.getInstance("SHA-256").digest(text.toByteArray()).joinToString(""){"%02x".format(it)}
    companion object {
        fun readChunk(input:InputStream,buffer:ByteArray):Int{var count=0;while(count<buffer.size){val n=input.read(buffer,count,buffer.size-count);if(n<0)break;count+=n};return count}
        fun skip(input:InputStream,length:Long){var left=length;while(left>0){val n=input.skip(left);if(n>0)left-=n else{if(input.read()<0)throw EOFException();left--}}}
        fun trim(dir:File,budget:Long,keep:File){var total=dir.listFiles().orEmpty().sumOf{it.length()};dir.listFiles().orEmpty().sortedBy{it.lastModified()}.forEach{if(it!=keep&&total>budget){val size=it.length();if(it.delete())total-=size}}}
    }
}
