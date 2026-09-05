package com.eslee.meshdrive

import java.nio.ByteBuffer
import java.security.MessageDigest

object PairingProtocol {
    data class Side(val id: String, val fingerprint: String, val nonce: String)
    fun sas(a: Side, b: Side): String {
        val sides = listOf(a, b).sortedBy { it.id }
        val text = "MESHDRIVE-PAIR-1\n" + sides.joinToString("") { "${it.id}\n${it.fingerprint}\n${it.nonce}\n" }
        val number = (ByteBuffer.wrap(MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8))).int.toLong() and 0xffffffffL) % 1000000
        return number.toString().padStart(6, '0')
    }
    fun expires(remote: Long, now: Long): Long { require(remote > now) { "페어링 요청이 만료되었습니다" }; return minOf(remote, now + 120000) }
    fun safeParts(path: String): List<String> {
        require(!path.startsWith('/') && !path.contains('\\') && !path.contains(':')) { "공유 밖 경로" }
        return path.split('/').filter { it.isNotEmpty() }.also { parts -> require(parts.none { it == "." || it == ".." || it.endsWith('.') || it.endsWith(' ') || it.contains('\u0000') }) { "잘못된 경로" } }
    }
}
