package com.eslee.meshdrive

import android.content.Context
import android.net.Uri
import androidx.documentfile.provider.DocumentFile
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

object DocumentCopies {
    /** The URI must be newly created by ACTION_CREATE_DOCUMENT, never an existing user file. */
    fun publishNew(context:Context,source:File,destination:Uri,checkAccess:()->Unit={}){
        try {
            val expected=source.inputStream().use{digest(it)}
            context.contentResolver.openOutputStream(destination,"w")!!.use{out->source.inputStream().use{input->
                val buffer=ByteArray(65536);while(true){checkAccess();val count=input.read(buffer);if(count<0)break;out.write(buffer,0,count)}
            }}
            val actual=context.contentResolver.openInputStream(destination)!!.use{digest(it)}
            check(MessageDigest.isEqual(expected,actual)){"저장된 파일의 무결성 확인에 실패했습니다"}
            checkAccess()
        } catch(e:Exception){DocumentFile.fromSingleUri(context,destination)?.delete();throw e}
    }
    private fun digest(input:InputStream):ByteArray {
        val digest=MessageDigest.getInstance("SHA-256");val buffer=ByteArray(65536)
        while(true){val n=input.read(buffer);if(n<0)break;digest.update(buffer,0,n)}
        return digest.digest()
    }
}
