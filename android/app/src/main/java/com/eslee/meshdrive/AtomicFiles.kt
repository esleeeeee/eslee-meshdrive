package com.eslee.meshdrive

import java.io.File
import java.io.FileOutputStream
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption

object AtomicFiles {
    fun writeText(file:File,text:String){
        val temporary=File(file.path+".tmp")
        FileOutputStream(temporary).use{it.write(text.toByteArray(Charsets.UTF_8));it.fd.sync()}
        try { Files.move(temporary.toPath(),file.toPath(),StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING) }
        catch(_:AtomicMoveNotSupportedException){Files.move(temporary.toPath(),file.toPath(),StandardCopyOption.REPLACE_EXISTING)}
    }
}
