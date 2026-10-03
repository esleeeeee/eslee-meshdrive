package com.eslee.meshdrive

import android.net.Uri
import androidx.documentfile.provider.DocumentFile
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.security.MessageDigest

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28])
class AndroidSyncTest {
    private fun hash(bytes:ByteArray)=MessageDigest.getInstance("SHA-256").digest(bytes).joinToString(""){"%02X".format(it)}
    private class Fixture {
        val context=RuntimeEnvironment.getApplication()
        val directory=File(context.filesDir,"selected-${java.util.UUID.randomUUID()}").apply{mkdirs()}
        val trees=mutableMapOf(Uri.fromFile(directory).toString() to directory)
        var trusted=true
        fun create()=AndroidSync(context,{trusted},{false}){uri->trees[uri.toString()]?.let{DocumentFile.fromFile(it)}}
        val sync=create().also{it.add(Uri.fromFile(directory),"Selected",listOf("peer"))}
        val root=sync.snapshot().let{it.getJSONObject(it.length()-1).getString("id")}
    }
    private fun request(path:String,body:JSONObject?=null,query:Map<String,String> = emptyMap(),bytes:ByteArray?=null)=HttpRequest(
        if(path=="upload-chunk")"PUT" else if(body==null)"GET" else "POST","/v1/secure/sync/$path",query,emptyMap(),bytes?:body?.toString()?.toByteArray()?:ByteArray(0),null,"127.0.0.1")
    private fun json(reply:HttpReply)=reply.stream!!.bufferedReader().use{JSONObject(it.readText())}
    private fun upload(sync:AndroidSync,root:String,bytes:ByteArray,expected:String?):JSONObject {
        val metadata=JSONObject().put("rootId",root).put("path","note.txt").put("expectedHash",expected?:JSONObject.NULL).put("newHash",hash(bytes)).put("size",bytes.size)
        val ticket=json(sync.handle(request("upload-start",metadata),"peer"))
        if(!ticket.getBoolean("completed")){
            sync.handle(request("upload-chunk",query=mapOf("id" to ticket.getString("id"),"offset" to "0"),bytes=bytes),"peer")
            sync.handle(request("upload-complete",query=mapOf("id" to ticket.getString("id"))),"peer")
        }
        return ticket
    }
    @Test fun overwriteDeleteRestoreAndRestartPreserveOriginalBytes(){
        val f=Fixture();val old="original".toByteArray();val newer="replacement".toByteArray()
        upload(f.sync,f.root,old,null);assertArrayEquals(old,File(f.directory,"note.txt").readBytes())
        upload(f.sync,f.root,newer,hash(old));assertArrayEquals(newer,File(f.directory,"note.txt").readBytes())
        val version=f.sync.versions(f.root).single();assertEquals(hash(old),version.getString("hash"))
        f.sync.handle(request("delete",JSONObject().put("rootId",f.root).put("path","note.txt").put("expectedHash",hash(newer))),"peer")
        assertFalse(File(f.directory,"note.txt").exists())
        val restarted=f.create();assertEquals(2,restarted.versions(f.root).size)
        restarted.restore(f.root,version.getString("id"));assertArrayEquals(old,File(f.directory,"note.txt").readBytes())
    }
    @Test fun replacementCrashBoundariesRecoverBeforeInventory(){
        for(boundary in listOf("before-delete","after-delete","after-rename")){
            val f=Fixture();val old="original".toByteArray();val newer="replacement".toByteArray()
            upload(f.sync,f.root,old,null)
            f.sync.replacementBoundary={if(it==boundary)throw AssertionError("simulated process death")}
            var crashed=false
            try{upload(f.sync,f.root,newer,hash(old))}catch(_:AssertionError){crashed=true}
            assertTrue("boundary was reached",crashed)
            val restarted=f.create()
            val reply=restarted.handle(request("inventory",query=mapOf("rootId" to f.root)),"peer")
            val inventory=org.json.JSONArray(reply.stream!!.bufferedReader().use{it.readText()})
            assertEquals(1,inventory.length())
            assertArrayEquals(if(boundary=="after-rename")newer else old,File(f.directory,"note.txt").readBytes())
            assertTrue(File(f.context.filesDir,"sync-replacements").listFiles().orEmpty().isEmpty())
        }
    }
    @Test fun removedRootRecoveryDoesNotBlockOtherRoots(){
        val f=Fixture();val old="original".toByteArray();upload(f.sync,f.root,old,null)
        f.sync.replacementBoundary={if(it=="after-delete")throw AssertionError("process death")}
        var crashed=false
        try{upload(f.sync,f.root,"new".toByteArray(),hash(old))}catch(_:AssertionError){crashed=true}
        assertTrue(crashed)
        f.sync.remove(f.root)
        val other=File(f.context.filesDir,"other-root").apply{mkdirs()};File(other,"keep.txt").writeText("kept")
        f.trees[Uri.fromFile(other).toString()]=other
        val restarted=f.create();restarted.add(Uri.fromFile(other),"Other",listOf("peer"))
        val id=restarted.snapshot().getJSONObject(0).getString("id")
        val reply=restarted.handle(request("inventory",query=mapOf("rootId" to id)),"peer")
        val inventory=org.json.JSONArray(reply.stream!!.bufferedReader().use{it.readText()})
        assertEquals("keep.txt",inventory.getJSONObject(0).getString("path"))
        assertEquals(1,File(f.context.filesDir,"sync-replacements").listFiles().orEmpty().size)
        try{restarted.handle(request("inventory",query=mapOf("rootId" to f.root)),"peer");fail()}catch(_:SecurityException){}
    }

    @Test fun exceptionAfterSuccessfulRenameKeepsPublishedFile(){
        val f=Fixture();val old="original".toByteArray();val newer="replacement".toByteArray();upload(f.sync,f.root,old,null)
        f.sync.replacementBoundary={if(it=="after-rename")throw java.io.IOException("journal cleanup failure")}
        try{upload(f.sync,f.root,newer,hash(old));fail()}catch(_:java.io.IOException){}
        assertArrayEquals(newer,File(f.directory,"note.txt").readBytes())
        assertEquals(1,f.sync.versions(f.root).size)
        val restarted=f.create();restarted.handle(request("inventory",query=mapOf("rootId" to f.root)),"peer").stream!!.close()
        assertArrayEquals(newer,File(f.directory,"note.txt").readBytes())
    }

    @Test fun thrownReplacementFailureRestoresCanonicalFile(){
        val f=Fixture();val old="original".toByteArray();upload(f.sync,f.root,old,null)
        f.sync.replacementBoundary={if(it=="after-delete")throw java.io.IOException("provider failure")}
        try{upload(f.sync,f.root,"replacement".toByteArray(),hash(old));fail()}catch(_:java.io.IOException){}
        assertArrayEquals(old,File(f.directory,"note.txt").readBytes())
        assertEquals(1,f.sync.versions(f.root).size)
    }

    @Test fun corruptionRevokedTrustAndUnapprovedRootsAreRejected(){
        val f=Fixture();val original="original".toByteArray();upload(f.sync,f.root,original,null)
        try{upload(f.sync,f.root,"changed".toByteArray(),"wrong");fail()}catch(_:IllegalStateException){}
        assertArrayEquals(original,File(f.directory,"note.txt").readBytes())
        val metadata=JSONObject().put("rootId",f.root).put("path","other.txt").put("expectedHash",JSONObject.NULL).put("newHash",hash(original)).put("size",original.size)
        val ticket=json(f.sync.handle(request("upload-start",metadata),"peer"));val bad=original.clone();bad[0]=(bad[0].toInt() xor 1).toByte()
        f.sync.handle(request("upload-chunk",query=mapOf("id" to ticket.getString("id"),"offset" to "0"),bytes=bad),"peer")
        try{f.sync.handle(request("upload-complete",query=mapOf("id" to ticket.getString("id"))),"peer");fail()}catch(_:java.io.IOException){}
        assertFalse(File(f.directory,"other.txt").exists())
        f.trusted=false
        try{f.sync.handle(request("inventory",query=mapOf("rootId" to f.root)),"peer");fail()}catch(_:SecurityException){}
        f.trusted=true
        try{f.sync.handle(request("inventory",query=mapOf("rootId" to "ordinary-share")),"peer");fail()}catch(_:SecurityException){}
        assertArrayEquals(original,File(f.directory,"note.txt").readBytes())
    }
    @Test fun rangeAndPersistedChunkResumeMatchWindowsSyncContract(){
        val f=Fixture();val chunk=8*1024*1024;val bytes=ByteArray(chunk+31).also{java.util.Random(91).nextBytes(it)}
        val metadata=JSONObject().put("rootId",f.root).put("path","large.bin").put("expectedHash",JSONObject.NULL).put("newHash",hash(bytes)).put("size",bytes.size)
        val ticket=json(f.sync.handle(request("upload-start",metadata),"peer"));val id=ticket.getString("id")
        f.sync.handle(request("upload-chunk",query=mapOf("id" to id,"offset" to "0"),bytes=bytes.copyOfRange(0,chunk)),"peer")
        val restarted=f.create();val resumed=json(restarted.handle(request("upload-start",metadata),"peer"));assertEquals(chunk.toLong(),resumed.getLong("offset"))
        restarted.handle(request("upload-chunk",query=mapOf("id" to id,"offset" to chunk.toString()),bytes=bytes.copyOfRange(chunk,bytes.size)),"peer")
        restarted.handle(request("upload-complete",query=mapOf("id" to id)),"peer")
        assertArrayEquals(bytes,File(f.directory,"large.bin").readBytes())
        val response=restarted.handle(request("content",query=mapOf("rootId" to f.root,"path" to "large.bin","hash" to hash(bytes))).copy(headers=mapOf("range" to "bytes=101-201")),"peer")
        assertEquals(206,response.status);assertEquals(101L,response.length)
        val actual=ByteArray(101);response.stream!!.use{java.io.DataInputStream(it).readFully(actual)}
        assertArrayEquals(bytes.copyOfRange(101,202),actual)
        assertEquals("bytes 101-201/${bytes.size}",response.headers["Content-Range"])
        val stale=restarted.handle(request("content",query=mapOf("rootId" to f.root,"path" to "large.bin","hash" to "wrong")),"peer")
        assertEquals(412,stale.status);stale.stream?.close()
    }
}
