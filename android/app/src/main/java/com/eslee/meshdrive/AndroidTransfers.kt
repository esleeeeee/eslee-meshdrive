package com.eslee.meshdrive

import org.json.JSONObject
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.Base64

object AndroidTransfers {
    private const val CHUNK=8*1024*1024
    fun upload(engine:MeshEngine,peer:MeshEngine.Peer,share:String,path:String,source:File){
        val merkle=com.eslee.quicksend.engine.MerkleAccumulator()
        source.inputStream().use { input ->
            val buffer=ByteArray(CHUNK)
            while(true){val count=StorageApi.readChunk(input,buffer);if(count==0)break;merkle.addChunk(buffer.copyOf(count))}
        }
        val size=source.length()
        val modified=source.lastModified()*10000+621355968000000000L
        val manifest=JSONObject().put("size",size).put("modifiedUtcTicks",modified)
            .put("version","${size.toString(16)}-${modified.toString(16)}")
            .put("chunkSize",CHUNK).put("leafCount",merkle.leafCount)
            .put("merkleRoot",Base64.getEncoder().encodeToString(merkle.root()))
        val request=JSONObject().put("shareId",share).put("path",path).put("name",source.name).put("manifest",manifest)
        fun send(route:String,method:String,body:ByteArray):String {
            val connection=engine.connection(peer,"/v1/secure/storage/$route")
            try {
                connection.requestMethod=method;connection.doOutput=true
                connection.setRequestProperty("Content-Type",if(method=="PUT")"application/octet-stream" else "application/json")
                connection.setFixedLengthStreamingMode(body.size)
                connection.outputStream.use{it.write(body)}
                require(connection.responseCode in 200..299){"전송 실패 (${connection.responseCode})"}
                return connection.inputStream.use{it.readBytes().toString(Charsets.UTF_8)}
            } finally { connection.disconnect() }
        }
        val ticket=JSONObject(send("upload-start","POST",request.toString().toByteArray()))
        val id=ticket.getString("id").replace("-","")
        require(id.matches(Regex("[0-9a-fA-F]{32}")))
        var offset=ticket.getLong("offset");require(offset in 0..size&&(offset==size||offset%CHUNK==0L))
        RandomAccessFile(source,"r").use { input ->
            input.seek(offset)
            while(offset<size){
                val bytes=ByteArray(minOf(CHUNK.toLong(),size-offset).toInt());input.readFully(bytes)
                val payload=ByteBuffer.allocate(bytes.size+60)
                    .put(id.chunked(2).map{it.toInt(16).toByte()}.toByteArray())
                    .putLong(offset).putInt(bytes.size).put(MessageDigest.getInstance("SHA-256").digest(bytes)).put(bytes).array()
                send("upload-chunk?id=$id","PUT",payload);offset+=bytes.size
            }
        }
        send("upload-complete?id=$id","POST",ByteArray(0))
    }
    internal fun verifyOrReset(file:RandomAccessFile,checkpoint:File,expected:ByteArray){
        val merkle=com.eslee.quicksend.engine.MerkleAccumulator()
        file.seek(0);val buffer=ByteArray(CHUNK)
        while(file.filePointer<file.length()){val count=minOf(CHUNK.toLong(),file.length()-file.filePointer).toInt();file.readFully(buffer,0,count);merkle.addChunk(buffer.copyOf(count))}
        if(!MessageDigest.isEqual(merkle.root(),expected)){
            file.setLength(0);file.fd.sync();AtomicFiles.writeText(checkpoint,"0")
            throw java.io.IOException("최종 파일 무결성 오류 · 다시 시도하면 처음부터 받습니다")
        }
    }
    fun root(leaves:List<ByteArray>):ByteArray = com.eslee.quicksend.engine.MerkleAccumulator.restore(leaves.fold(ByteArray(0)){all,leaf->all+leaf}).root()
    fun download(engine:MeshEngine,peer:MeshEngine.Peer,share:String,path:String,destination:File,copyToken:String?=null,checkAccess:()->Unit={},progress:(Long,Long)->Unit={_,_->}):File{
        checkAccess();PairingProtocol.safeParts(path)
        val suffix=if(copyToken==null)"" else "&copyToken=${java.net.URLEncoder.encode(copyToken,"UTF-8")}"
        destination.mkdirs();val c=engine.connection(peer,engine.resource("manifest",share,path)+suffix);val manifest=try{require(c.responseCode==200);c.inputStream.use{JSONObject(it.readBytes().toString(Charsets.UTF_8))}}finally{c.disconnect()}
        val size=manifest.getLong("size");require(size>=0&&manifest.getInt("chunkSize")==CHUNK);val expected=Base64.getDecoder().decode(manifest.getString("merkleRoot"));require(expected.size==32&&manifest.getLong("leafCount")==size/CHUNK+(if(size%CHUNK==0L)0 else 1));
        val key=MessageDigest.getInstance("SHA-256").digest((peer.id+share+path+manifest.getString("version")).toByteArray()).take(16).joinToString(""){"%02x".format(it)}
        val partial=File(destination,".$key.part");val checkpoint=File(destination,".$key.checkpoint");val leaves=mutableListOf<ByteArray>();
        RandomAccessFile(partial,"rw").use{out->var offset=minOf((checkpoint.takeIf{it.exists()}?.readText()?.toLongOrNull()?:0).coerceAtLeast(0),out.length(),size);if(offset!=size)offset-=offset%CHUNK;out.setLength(offset);out.seek(0);val buffer=ByteArray(CHUNK);var hashed=0L;while(hashed<offset){val n=minOf(CHUNK.toLong(),offset-hashed).toInt();out.readFully(buffer,0,n);leaves.add(MessageDigest.getInstance("SHA-256").digest(byteArrayOf(0)+buffer.copyOf(n)));hashed+=n};out.seek(offset)
            while(offset<size){checkAccess();progress(offset,size);val url=engine.resource("chunk",share,path)+"&offset=$offset&fileId=$key&version=${java.net.URLEncoder.encode(manifest.getString("version"),"UTF-8")}"+suffix;val conn=engine.connection(peer,url);val payload=try{require(conn.responseCode==200);conn.inputStream.use{val bounded=ByteArray(CHUNK+61);bounded.copyOf(StorageApi.readChunk(it,bounded))}}finally{conn.disconnect()};require(payload.size in 61..(CHUNK+60));val header=ByteBuffer.wrap(payload);val actualOffset=header.getLong(16);val length=header.getInt(24);require(actualOffset==offset&&length==payload.size-60&&length==minOf(CHUNK.toLong(),size-offset).toInt());val bytes=payload.copyOfRange(60,payload.size);require(MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(bytes),payload.copyOfRange(28,60))){"청크 무결성 오류"};out.write(bytes);leaves.add(MessageDigest.getInstance("SHA-256").digest(byteArrayOf(0)+bytes));offset+=length;out.fd.sync();AtomicFiles.writeText(checkpoint,offset.toString())}
            checkAccess();progress(size,size)
            verifyOrReset(out,checkpoint,expected)
        }
        var target=File(destination,path.substringAfterLast('/'));var index=1;while(target.exists()){val original=File(path.substringAfterLast('/'));target=File(destination,"${original.nameWithoutExtension} (${index++}).${original.extension}")};check(partial.renameTo(target));checkpoint.delete();return target
    }
}
