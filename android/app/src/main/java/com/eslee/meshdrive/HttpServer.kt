package com.eslee.meshdrive

import java.io.*
import java.net.*
import java.security.cert.X509Certificate
import java.util.concurrent.Executors
import javax.net.ssl.SSLSocket

data class HttpRequest(val method: String, val path: String, val query: Map<String,String>, val headers: Map<String,String>, val body: ByteArray, val peer: X509Certificate?, val address: String)
data class HttpReply(val status: Int = 200, val type: String = "application/json", val length: Long = 0, val stream: InputStream? = null, val headers: Map<String,String> = emptyMap()) {
    companion object { fun text(value: String, status: Int = 200, type: String = "application/json"): HttpReply { val bytes = value.toByteArray(); return HttpReply(status, type, bytes.size.toLong(), ByteArrayInputStream(bytes)) } }
}

class HttpServer(private val socket: ServerSocket, private val handle: (HttpRequest)->HttpReply): AutoCloseable {
    private val workers = java.util.concurrent.ThreadPoolExecutor(2, 8, 30, java.util.concurrent.TimeUnit.SECONDS, java.util.concurrent.ArrayBlockingQueue<Runnable>(32))
    private val accept = Executors.newSingleThreadExecutor()
    val port get() = socket.localPort
    fun start() { accept.execute { while (!socket.isClosed) { try { val client = socket.accept(); try { workers.execute { serve(client) } } catch (_: java.util.concurrent.RejectedExecutionException) { client.close() } } catch (_: IOException) { if (socket.isClosed) break } } } }
    private fun line(input: InputStream): String {
        val bytes = ByteArrayOutputStream(); while (bytes.size() < 16384) { val b = input.read(); if (b < 0) throw EOFException(); if (b == 10) return bytes.toString("ISO-8859-1").trimEnd('\r'); bytes.write(b) }; throw IOException("Header too large")
    }
    private fun serve(client: Socket) {
        client.use {
            try {
                client.soTimeout = 30000
                val peer = if (client is SSLSocket) { client.startHandshake(); client.session.peerCertificates[0] as X509Certificate } else null
                val input = BufferedInputStream(client.getInputStream()); val output = BufferedOutputStream(client.getOutputStream())
                val start = line(input).split(' '); require(start.size == 3)
                val headers = linkedMapOf<String,String>(); var total = 0
                while (true) { val h = line(input); if (h.isEmpty()) break; total += h.length; require(total <= 32768); val pos = h.indexOf(':'); require(pos > 0); headers[h.substring(0,pos).lowercase()] = h.substring(pos+1).trim() }
                require(!headers.containsKey("transfer-encoding")); val size = headers["content-length"]?.toInt() ?: 0; require(size in 0..(8*1024*1024+65536))
                val body = ByteArray(size); DataInputStream(input).readFully(body)
                val uri = URI(start[1]); val query = uri.rawQuery.orEmpty().split('&').filter { it.contains('=') }.associate { val p = it.split('=',limit=2); URLDecoder.decode(p[0],"UTF-8") to URLDecoder.decode(p[1],"UTF-8") }
                val request = HttpRequest(start[0], uri.path, query, headers, body, peer, client.inetAddress.hostAddress!!)
                val response = try { handle(request) } catch (e: SecurityException) { HttpReply.text("{\"error\":\"접근 권한이 없습니다\"}",403) } catch (e: Exception) { HttpReply.text("{\"error\":\"요청을 처리할 수 없습니다\"}",400) }
                val responseHeader = "HTTP/1.1 ${response.status} ${reason(response.status)}\r\nContent-Type: ${response.type}\r\nContent-Length: ${response.length}\r\nConnection: close\r\n" + response.headers.entries.joinToString("") { "${it.key}: ${it.value}\r\n" } + "\r\n"
                output.write(responseHeader.toByteArray(Charsets.ISO_8859_1));
                response.stream?.use { source -> if (request.method != "HEAD") { var left = response.length; val buffer=ByteArray(65536); while(left>0) { val n=source.read(buffer,0,minOf(left,buffer.size.toLong()).toInt()); if(n<0) throw EOFException(); output.write(buffer,0,n); left-=n } } }; output.flush()
            } catch (_: Exception) { }
        }
    }
    private fun reason(code:Int) = when(code) {200->"OK";206->"Partial Content";403->"Forbidden";404->"Not Found";410->"Gone";416->"Range Not Satisfiable";else->"Error"}
    override fun close() { socket.close(); accept.shutdownNow(); workers.shutdownNow() }
}
