package com.eslee.meshdrive

import android.content.Context
import android.net.Uri
import android.net.nsd.*
import android.os.Build
import androidx.documentfile.provider.DocumentFile
import org.json.JSONArray
import org.json.JSONObject
import java.io.*
import java.net.*
import java.security.SecureRandom
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.time.Instant
import java.util.*
import java.util.concurrent.ConcurrentHashMap
import javax.net.ssl.*

class MeshEngine(val context: Context): AutoCloseable {
    val security = DeviceSecurity(context)
    private val settings = context.getSharedPreferences("meshdrive",0)
    val name get() = settings.getString("name",Build.MODEL)!!
    val peers = ConcurrentHashMap<String, Peer>()
    private val trust = JSONObject(settings.getString("trust","{}")!!)
    private val shares = JSONArray(settings.getString("shares","[]")!!)
    private val nsd = context.getSystemService(NsdManager::class.java)
    private val tls = (security.context().serverSocketFactory.createServerSocket(0) as SSLServerSocket).apply { needClientAuth=true }
    private val server = HttpServer(tls, ::serve)
    private val bridge = HttpServer(ServerSocket(0,16,InetAddress.getByName("127.0.0.1")), ::relay)
    private val streams = ConcurrentHashMap<String, Triple<String,String,Long>>()
    private val storageApi = StorageApi(context, ::document)
    @Volatile var pairing: PairSession? = null
    @Volatile var status = "준비 중"
    @Volatile var paused = false
    data class Peer(val id:String,val name:String,val address:String,val port:Int,var online:Boolean=true)
    data class PairSession(val offer:JSONObject,val remote:JSONObject,val peer:Peer,val expires:Long,val sas:String,var localAccepted:Boolean=false,var remoteAccepted:Boolean=false,var rejected:Boolean=false)
    private val registration = object:NsdManager.RegistrationListener { override fun onServiceRegistered(s:NsdServiceInfo){};override fun onRegistrationFailed(s:NsdServiceInfo,e:Int){status="기기 광고 실패: $e"};override fun onServiceUnregistered(s:NsdServiceInfo){};override fun onUnregistrationFailed(s:NsdServiceInfo,e:Int){} }
    private val discovery = object:NsdManager.DiscoveryListener {
        override fun onDiscoveryStarted(s:String){};override fun onDiscoveryStopped(s:String){};override fun onStartDiscoveryFailed(s:String,e:Int){status="자동 발견 실패: $e"};override fun onStopDiscoveryFailed(s:String,e:Int){}
        override fun onServiceLost(s:NsdServiceInfo){peers[s.serviceName]?.online=false}
        override fun onServiceFound(s:NsdServiceInfo) { if(s.serviceName==security.id)return; @Suppress("DEPRECATION") nsd.resolveService(s,object:NsdManager.ResolveListener { override fun onResolveFailed(s:NsdServiceInfo,e:Int){};override fun onServiceResolved(s:NsdServiceInfo){val id=s.attributes["id"]?.toString(Charsets.UTF_8)?:s.serviceName;if(id!=security.id){@Suppress("DEPRECATION") val address=s.host?.hostAddress ?: return; if(!address.contains(':'))peers[id]=Peer(id,s.attributes["name"]?.toString(Charsets.UTF_8)?:id,address,s.port)}} }) }
    }
    fun start() { server.start(); bridge.start(); nsd.registerService(NsdServiceInfo().apply { serviceName=security.id;serviceType="_meshdrive._tcp.";port=server.port;setAttribute("id",security.id);setAttribute("name",name);setAttribute("v","0.2.0") },NsdManager.PROTOCOL_DNS_SD,registration);nsd.discoverServices("_meshdrive._tcp.",NsdManager.PROTOCOL_DNS_SD,discovery);status="같은 네트워크의 기기를 찾는 중" }
    @Synchronized fun trusted(id:String)=trust.has(id)
    @Synchronized fun unpair(id:String){trust.remove(id);settings.edit().putString("trust",trust.toString()).commit()}
    @Synchronized private fun fingerprint(id:String):String=trust.optString(id,"")
    private fun offer(id:String,expires:Long)=JSONObject().put("protocolVersion",1).put("sessionId",id).put("deviceId",security.id).put("deviceName",name).put("fingerprint",security.fingerprint).put("certificateDer",Base64.getEncoder().encodeToString(security.certificate.encoded)).put("nonce",random()).put("expiresAt",Instant.ofEpochMilli(expires).toString()).put("listenPort",server.port)
    fun connect(peer:Peer) { synchronized(this){ require(pairing?.let { !it.rejected && System.currentTimeMillis()<it.expires && !(it.localAccepted&&it.remoteAccepted) } != true) };val local=offer(UUID.randomUUID().toString().replace("-",""),System.currentTimeMillis()+120000);val connection=connection(peer,"/v1/pairing/offer","POST",local.toString().toByteArray(),false);val remote=connection.inputStream.use{JSONObject(it.readBytes().toString(Charsets.UTF_8))};val cert=connection.serverCertificates[0] as X509Certificate;connection.disconnect();validate(remote,cert);require(remote.getString("sessionId")==local.getString("sessionId"));pairing=session(local,remote,peer) }
    private fun session(local:JSONObject,remote:JSONObject,peer:Peer):PairSession { val expires=PairingProtocol.expires(Instant.parse(local.getString("expiresAt")).toEpochMilli(),System.currentTimeMillis());return PairSession(local,remote,peer,expires,PairingProtocol.sas(PairingProtocol.Side(security.id,security.fingerprint,local.getString("nonce")),PairingProtocol.Side(remote.getString("deviceId"),remote.getString("fingerprint"),remote.getString("nonce")))) }
    @Synchronized private fun validate(offer:JSONObject,cert:X509Certificate){require(offer.getInt("protocolVersion")==1);require(offer.getString("deviceId").matches(Regex("[A-Za-z0-9-]{1,63}")));require(offer.getString("fingerprint")==DeviceSecurity.fingerprint(cert));val embedded=CertificateFactory.getInstance("X.509").generateCertificate(ByteArrayInputStream(Base64.getDecoder().decode(offer.getString("certificateDer")))) as X509Certificate;require(DeviceSecurity.fingerprint(embedded)==DeviceSecurity.fingerprint(cert))}
    fun decide(accepted:Boolean){ val p=pairing?:return; synchronized(this){require(System.currentTimeMillis()<p.expires && !p.rejected);p.localAccepted=accepted;p.rejected=!accepted};val c=connection(p.peer,"/v1/pairing/decision","POST",JSONObject().put("sessionId",p.offer.getString("sessionId")).put("deviceId",security.id).put("accepted",accepted).toString().toByteArray(),false);require(c.responseCode in 200..299);c.disconnect();complete(p) }
    @Synchronized private fun complete(p:PairSession){if(p.localAccepted&&p.remoteAccepted&&!p.rejected&&System.currentTimeMillis()<p.expires){trust.put(p.peer.id,p.remote.getString("fingerprint"));settings.edit().putString("trust",trust.toString()).commit();status="${p.peer.name} 페어링 완료"}}
    fun connection(peer:Peer,path:String,method:String="GET",body:ByteArray?=null,secure:Boolean=true):HttpsURLConnection {
        val fp=if(secure) fingerprint(peer.id).also{require(it.isNotEmpty()){ "먼저 페어링하세요" }} else null
        return (URL("https://${peer.address}:${peer.port}$path").openConnection() as HttpsURLConnection).apply { sslSocketFactory=security.context(fp).socketFactory;hostnameVerifier=HostnameVerifier{_,_->true};instanceFollowRedirects=false;connectTimeout=8000;readTimeout=30000;requestMethod=method;if(body!=null){doOutput=true;setFixedLengthStreamingMode(body.size);setRequestProperty("Content-Type","application/json");outputStream.use{it.write(body)}} }
    }
    fun json(peer:Peer,path:String):JSONArray {val c=connection(peer,path);return try{require(c.responseCode==200){"접근 실패 (${c.responseCode})"};c.inputStream.use{JSONArray(it.readBytes().toString(Charsets.UTF_8))}}finally{c.disconnect()}}
    fun resource(kind:String,share:String,path:String)="/v1/secure/storage/$kind?shareId=${URLEncoder.encode(share,"UTF-8")}&path=${URLEncoder.encode(path,"UTF-8")}"
    fun stream(peer:Peer,share:String,path:String):String {val token=random();streams.entries.removeIf{System.currentTimeMillis()-it.value.third>900000};require(streams.size<64);streams[token]=Triple(peer.id,resource("content",share,path),System.currentTimeMillis());return "http://127.0.0.1:${bridge.port}/stream/$token/${URLEncoder.encode(path.substringAfterLast('/'),"UTF-8")}"}
    private fun relay(r:HttpRequest):HttpReply {val token=r.path.split('/').getOrNull(2)?:return HttpReply.text("",410);val s=streams[token]?:return HttpReply.text("",410);if(System.currentTimeMillis()-s.third>900000){streams.remove(token);return HttpReply.text("",410)};streams[token]=s.copy(third=System.currentTimeMillis());val peer=peers[s.first]?:return HttpReply.text("",404);val c=connection(peer,s.second,r.method);r.headers["range"]?.let{c.setRequestProperty("Range",it)};val code=c.responseCode;val headers=mutableMapOf<String,String>();listOf("Content-Range","Accept-Ranges","ETag","Last-Modified").forEach{k->c.getHeaderField(k)?.let{headers[k]=it}};val input=if(code in 200..299)c.inputStream else c.errorStream;return HttpReply(code,c.contentType?:"application/octet-stream",maxOf(0,c.contentLengthLong),input?.let{object:FilterInputStream(it){override fun close(){super.close();c.disconnect()};override fun read(b:ByteArray,o:Int,l:Int):Int{streams[token]=s.copy(third=System.currentTimeMillis());return super.read(b,o,l)}}},headers)}
    @Synchronized fun addShare(uri:Uri,name:String){ val id=UUID.randomUUID().toString().replace("-","");shares.put(JSONObject().put("id",id).put("name",name).put("uri",uri.toString()).put("permissions",7));settings.edit().putString("shares",shares.toString()).commit() }
    @Synchronized fun localShares()=JSONArray(shares.toString())
    @Synchronized fun setSharePermissions(id:String,value:Int){require(value in 0..15);for(i in 0 until shares.length()){val s=shares.getJSONObject(i);if(s.getString("id")==id)s.put("permissions",value)};settings.edit().putString("shares",shares.toString()).commit()}
    @Synchronized fun removeShare(id:String){val index=(0 until shares.length()).firstOrNull{shares.getJSONObject(it).getString("id")==id}?:return;shares.remove(index);settings.edit().putString("shares",shares.toString()).commit()}
    @Synchronized private fun document(share:String,path:String,permission:Int):DocumentFile {if(paused)throw SecurityException();val s=(0 until shares.length()).map{shares.getJSONObject(it)}.first{it.getString("id")==share};if(s.getInt("permissions") and permission != permission)throw SecurityException();var doc=DocumentFile.fromTreeUri(context,Uri.parse(s.getString("uri")))?:throw IOException();for(part in PairingProtocol.safeParts(path)){if(part.startsWith('.'))throw SecurityException();doc=doc.findFile(part)?:throw FileNotFoundException()};return doc}
    private fun serve(r:HttpRequest):HttpReply {
        val cert=r.peer?:throw SecurityException();val fp=DeviceSecurity.fingerprint(cert)
        if(r.path.startsWith("/v1/secure/")){synchronized(this){if((trust.keys().asSequence().map{trust.getString(it)}).none{it==fp})throw SecurityException()}}
        if(r.path=="/v1/pairing/offer"&&r.method=="POST") {val remote=JSONObject(r.body.toString(Charsets.UTF_8));validate(remote,cert);val now=System.currentTimeMillis();val expires=PairingProtocol.expires(Instant.parse(remote.getString("expiresAt")).toEpochMilli(),now);val peer=Peer(remote.getString("deviceId"),remote.getString("deviceName"),r.address,remote.getInt("listenPort"));synchronized(this){require(!trusted(peer.id));require(pairing?.let{!it.rejected&&now<it.expires&&!(it.localAccepted&&it.remoteAccepted)}!=true);val local=offer(remote.getString("sessionId"),expires);pairing=session(local,remote,peer);peers[peer.id]=peer;return HttpReply.text(local.toString())}}
        if(r.path=="/v1/pairing/decision"&&r.method=="POST"){val d=JSONObject(r.body.toString(Charsets.UTF_8));synchronized(this){val p=pairing?:throw SecurityException();require(d.getString("sessionId")==p.offer.getString("sessionId")&&d.getString("deviceId")==p.peer.id&&fp==p.remote.getString("fingerprint"));require(System.currentTimeMillis()<p.expires&&!p.rejected);p.remoteAccepted=d.getBoolean("accepted");p.rejected=!p.remoteAccepted;complete(p)};return HttpReply.text("{}")}
        if(r.path=="/v1/secure/ping")return HttpReply.text(JSONObject().put("deviceId",security.id).put("deviceName",name).toString())
        if(r.path=="/v1/secure/storage/shares"){if(paused)throw SecurityException();val array=JSONArray();synchronized(this){for(i in 0 until shares.length()){val s=shares.getJSONObject(i);array.put(JSONObject().put("id",s.getString("id")).put("name",s.getString("name")).put("permissions",s.getInt("permissions")))}};return HttpReply.text(array.toString())}
        if(r.path.startsWith("/v1/secure/storage/")){
            val device=synchronized(this){trust.keys().asSequence().firstOrNull{trust.getString(it)==fp}}?:throw SecurityException()
            storageApi.upload(r,device)?.let{return it}
            storageApi.get(r)?.let{return it}
        }
        val share=r.query["shareId"]?:return HttpReply.text("",404);val path=r.query["path"].orEmpty()
        if(r.path=="/v1/secure/storage/entries"){val dir=document(share,path,1);val array=JSONArray();dir.listFiles().filter{!it.name.orEmpty().startsWith('.')}.sortedWith(compareBy<DocumentFile>{!it.isDirectory}.thenBy{it.name}).forEach{d->array.put(JSONObject().put("name",d.name).put("relativePath",if(path.isEmpty())d.name else "$path/${d.name}").put("isDirectory",d.isDirectory).put("length",d.length()).put("modifiedAt",Instant.ofEpochMilli(d.lastModified()).toString()))};return HttpReply.text(array.toString())}
        if(r.path=="/v1/secure/storage/content"){val doc=document(share,path,if(r.query["purpose"]=="download")4 else 2);val length=doc.length();var start=0L;var end=length-1;var code=200;val headers=mutableMapOf("Accept-Ranges" to "bytes");r.headers["range"]?.let{range->val m=Regex("bytes=(\\d*)-(\\d*)").matchEntire(range)?:return HttpReply.text("",416);start=if(m.groupValues[1].isEmpty())maxOf(0,length-m.groupValues[2].toLong())else m.groupValues[1].toLong();end=if(m.groupValues[1].isEmpty()||m.groupValues[2].isEmpty())length-1 else minOf(end,m.groupValues[2].toLong());if(start>end||start>=length)return HttpReply(416,headers=mapOf("Content-Range" to "bytes */$length"));code=206;headers["Content-Range"]="bytes $start-$end/$length"};val input=context.contentResolver.openInputStream(doc.uri)?:throw IOException();try{StorageApi.skip(input,start)}catch(e:Exception){input.close();throw e};return HttpReply(code,doc.type?:"application/octet-stream",maxOf(0,end-start+1),input,headers)}
        return HttpReply.text("",404)
    }
    private fun random()=ByteArray(32).also{SecureRandom().nextBytes(it)}.joinToString(""){"%02X".format(it)}
    override fun close(){try{nsd.stopServiceDiscovery(discovery);nsd.unregisterService(registration)}catch(_:Exception){};server.close();bridge.close()}
}
