package com.eslee.meshdrive

import androidx.documentfile.provider.DocumentFile
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.Base64
import com.eslee.quicksend.engine.MerkleAccumulator

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28])
class StorageUploadTest {
    @Test fun completedCopyIsRevalidatedAndUserEditIsPreserved(){
        val context=RuntimeEnvironment.getApplication()
        val directory=File(context.filesDir,"upload-${java.util.UUID.randomUUID()}").apply{mkdirs()}
        val api=StorageApi(context){_,_,_->DocumentFile.fromFile(directory)}
        val bytes="verified bytes".toByteArray()
        val root=MerkleAccumulator().apply{addChunk(bytes)}.root()
        val metadata=JSONObject().put("shareId","files").put("path","").put("name","note.txt")
            .put("manifest",JSONObject().put("size",bytes.size).put("chunkSize",8*1024*1024).put("merkleRoot",Base64.getEncoder().encodeToString(root)))
        fun call(route:String,id:String?=null,body:ByteArray=ByteArray(0)):JSONObject {
            val r=HttpRequest("POST","/v1/secure/storage/$route",if(id==null)emptyMap() else mapOf("id" to id),emptyMap(),body,null,"127.0.0.1")
            return api.upload(r,"peer")!!.stream!!.bufferedReader().use{JSONObject(it.readText())}
        }
        val ticket=call("upload-start",body=metadata.toString().toByteArray());val id=ticket.getString("id")
        val chunk=ByteBuffer.allocate(60+bytes.size).put(id.chunked(2).map{it.toInt(16).toByte()}.toByteArray()).putLong(0).putInt(bytes.size).put(MessageDigest.getInstance("SHA-256").digest(bytes)).put(bytes).array()
        call("upload-chunk",id,chunk);call("upload-complete",id)
        assertArrayEquals(bytes,File(directory,"note.txt").readBytes())
        File(directory,"note.txt").writeText("user edit")
        assertThrows(IllegalStateException::class.java){call("upload-complete",id)}
        assertEquals(0L,call("upload-start",body=metadata.toString().toByteArray()).getLong("offset"))
        call("upload-chunk",id,chunk);call("upload-complete",id)
        assertEquals("user edit",File(directory,"note.txt").readText())
        assertTrue(directory.listFiles()!!.any{it.readBytes().contentEquals(bytes)})
    }
}
