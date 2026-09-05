package com.eslee.meshdrive
import org.junit.Test
import org.junit.Assert.*
import java.net.*
import java.io.*
class HttpServerTest {
    @Test fun getHeadAndQueryAreHandled() {
        val server=HttpServer(ServerSocket(0,8,InetAddress.getLoopbackAddress())) { r -> assertEquals("한 글",r.query["path"]);HttpReply.text("original",type="audio/mpeg") }
        server.use { it.start();val url=URL("http://127.0.0.1:${it.port}/content?path="+URLEncoder.encode("한 글","UTF-8"));val get=url.openConnection() as HttpURLConnection;assertEquals("original",get.inputStream.readBytes().toString(Charsets.UTF_8));get.disconnect();val head=url.openConnection() as HttpURLConnection;head.requestMethod="HEAD";assertEquals(8,head.contentLength);assertEquals(0,head.inputStream.readBytes().size);head.disconnect() }
    }
    @Test fun merkleMatchesQuickSendEmptyVector(){assertEquals("6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d",AndroidTransfers.root(emptyList()).joinToString(""){"%02x".format(it)})}
}
