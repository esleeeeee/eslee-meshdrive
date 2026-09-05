package com.eslee.meshdrive

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.io.RandomAccessFile
import java.nio.file.Files
import com.eslee.quicksend.engine.MerkleAccumulator

class TransferIntegrityTest {
    @Test fun damagedStoredBytesResetResumeCheckpoint(){
        val directory=Files.createTempDirectory("meshdrive-integrity").toFile()
        try {
            val part=File(directory,"file.part").apply{writeText("changed")}
            val checkpoint=File(directory,"offset").apply{writeText("7")}
            val expected=MerkleAccumulator().apply{addChunk("correct".toByteArray())}.root()
            RandomAccessFile(part,"rw").use{file->
                assertThrows(java.io.IOException::class.java){AndroidTransfers.verifyOrReset(file,checkpoint,expected)}
                assertEquals(0L,file.length());assertEquals("0",checkpoint.readText())
                file.write("correct".toByteArray());AndroidTransfers.verifyOrReset(file,checkpoint,expected)
                assertEquals(7L,file.length())
            }
        } finally {directory.listFiles().orEmpty().forEach{it.delete()};directory.delete()}
    }
}
