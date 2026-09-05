package com.eslee.meshdrive

import android.app.*
import android.content.Intent
import android.net.Uri
import android.os.*
import android.widget.*
import androidx.core.content.FileProvider
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors

class MainActivity:Activity(){
    private val worker=Executors.newSingleThreadExecutor()
    private lateinit var body:LinearLayout
    private lateinit var message:TextView
    private var selected:MeshEngine.Peer?=null
    private var share:String?=null
    private var path=""
    private val handler=Handler(Looper.getMainLooper())
    private var displayedSession:String?=null
    private data class UploadTarget(val peer:MeshEngine.Peer,val share:String,val path:String)
    private var uploadTarget:UploadTarget?=null
    private var navigation=0
    private val refresh=object:Runnable{override fun run(){val p=MeshService.engine?.pairing;if(p!=null&&displayedSession!=p.offer.optString("sessionId")&&System.currentTimeMillis()<p.expires){displayedSession=p.offer.optString("sessionId");AlertDialog.Builder(this@MainActivity).setTitle("${p.peer.name} · ${p.sas}").setMessage("상대 기기에도 같은 6자리 번호가 보이면 승인하세요.").setPositiveButton("승인"){_,_->runWork{engine().decide(true)}}.setNegativeButton("거절"){_,_->runWork{engine().decide(false)}}.show()};handler.postDelayed(this,1500)}}
    override fun onCreate(savedInstanceState:Bundle?){super.onCreate(savedInstanceState);if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS),1);startForegroundService(Intent(this,MeshService::class.java));val scroll=ScrollView(this);body=LinearLayout(this).apply{orientation=LinearLayout.VERTICAL;setPadding(24,36,24,24)};scroll.addView(body);setContentView(scroll);message=TextView(this);home();handler.post(refresh)}
    private fun engine()=MeshService.engine?:throw IllegalStateException("Agent 준비 중입니다. 잠시 후 새로고침하세요.")
    private fun button(text:String,action:()->Unit){body.addView(Button(this).apply{this.text=text;setOnClickListener{action()}})}
    private fun label(text:String){body.addView(TextView(this).apply{this.text=text;textSize=18f;setPadding(4,12,4,12)})}
    private fun reset(title:String){body.removeAllViews();label(title);body.addView(message)}
    private fun runWork(action:()->Unit){message.text="처리 중…";worker.execute{try{action();runOnUiThread{message.text="완료"}}catch(e:Exception){runOnUiThread{message.text=e.message?:"작업 실패"}}}}
    private fun home(){selected=null;share=null;path="";reset("MeshDrive · 내 기기의 파일을 원본 그대로");button("기기 새로고침"){home()};button("공유 폴더 추가"){startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION),20)};button("공유 폴더 관리"){showShares()};val e=MeshService.engine;if(e==null){label("백그라운드 Agent 준비 중");return};label(e.status);e.peers.values.sortedBy{it.name}.forEach{p->button("${p.name} · ${if(e.trusted(p.id))"연결됨" else "페어링 필요"} · ${if(p.online)"온라인" else "오프라인"}"){if(e.trusted(p.id)){selected=p;runWork{val shares=e.json(p,"/v1/secure/storage/shares");runOnUiThread{reset(p.name);button("내 기기 목록"){home()};for(i in 0 until shares.length()){val s=shares.getJSONObject(i);button(s.getString("name")){share=s.getString("id");path="";browse()}};button("연결 해제"){e.unpair(p.id);home()}}}}else runWork{e.connect(p)}}};button(if(e.paused)"공유 다시 시작" else "공유 일시 중지"){e.paused=!e.paused;home()};button("MeshDrive 전체 종료"){stopService(Intent(this,MeshService::class.java));finish()}}
    private fun showShares(){reset("내 공유 폴더");button("뒤로"){home()};val shares=engine().localShares();for(i in 0 until shares.length()){val s=shares.getJSONObject(i);button(s.getString("name")){AlertDialog.Builder(this).setTitle("공유 권한").setItems(arrayOf("읽기 전용","양방향 복사","스트리밍 전용","공유 해제")){_,which->if(which==3)engine().removeShare(s.getString("id"))else engine().setSharePermissions(s.getString("id"),intArrayOf(7,15,3)[which]);showShares()}.show()}}}
    private fun browse(){
        val peer=selected?:return;val id=share?:return;val current=path;val generation=++navigation
        runWork {
            val entries=engine().json(peer,engine().resource("entries",id,current))
            runOnUiThread {
                if(generation!=navigation||selected!=peer||share!=id)return@runOnUiThread
                reset("${peer.name} / $current")
                button("기기 목록"){home()}
                button("상위 폴더"){path=path.substringBeforeLast('/',"");browse()}
                button("새로고침"){browse()}
                button("이 폴더로 파일 복사"){
                    uploadTarget=UploadTarget(peer,id,current)
                    startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE),21)
                }
                for(i in 0 until entries.length()){
                    val file=entries.getJSONObject(i);val directory=file.getBoolean("isDirectory")
                    val row=LinearLayout(this)
                    val thumbnail=ImageView(this).apply{layoutParams=LinearLayout.LayoutParams(96,96)}
                    row.addView(thumbnail)
                    row.addView(Button(this).apply{
                        text="${if(directory)"📁" else "📄"} ${file.getString("name")}${if(directory)"" else " · ${file.optLong("length")} bytes"}"
                        setOnClickListener{if(directory){path=file.getString("relativePath");browse()}else chooseFile(peer,id,file)}
                    })
                    body.addView(row)
                    if(!directory&&file.getString("name").substringAfterLast('.').lowercase() in listOf("jpg","jpeg","png","webp","gif","bmp")){
                        worker.execute {
                            try {
                                if(generation!=navigation)return@execute
                                val c=engine().connection(peer,engine().resource("thumbnail",id,file.getString("relativePath")))
                                val bitmap=try{require(c.responseCode==200);c.inputStream.use{android.graphics.BitmapFactory.decodeStream(it)}}finally{c.disconnect()}
                                runOnUiThread{if(generation==navigation&&selected==peer)thumbnail.setImageBitmap(bitmap)}
                            } catch(_:Exception) { /* A missing preview never prevents file access. */ }
                        }
                    }
                }
            }
        }
    }

    private fun uploadPicked(uri:Uri){
        val target=uploadTarget?:return;uploadTarget=null
        runWork {
            val document=androidx.documentfile.provider.DocumentFile.fromSingleUri(this,uri)?:throw java.io.IOException("파일을 열 수 없습니다")
            val name=document.name?:"file"
            require(name==name.substringAfterLast('/')&&!name.contains('\\')&&name!="."&&name!="..")
            val key=java.security.MessageDigest.getInstance("SHA-256").digest(uri.toString().toByteArray()).joinToString(""){"%02x".format(it)}
            val directory=File(filesDir,"outgoing/$key").apply{mkdirs()};val staged=File(directory,name)
            contentResolver.openInputStream(uri)!!.use{input->staged.outputStream().use{input.copyTo(it)}}
            staged.setLastModified(document.lastModified())
            AndroidTransfers.upload(engine(),target.peer,target.share,target.path,staged)
            staged.delete()
            runOnUiThread{if(selected==target.peer&&share==target.share&&path==target.path)browse()}
        }
    }
    private fun chooseFile(peer:MeshEngine.Peer,id:String,file:JSONObject){AlertDialog.Builder(this).setTitle(file.getString("name")).setItems(arrayOf("원본 열기","이 기기로 가져오기")){_,which->runWork{val path=file.getString("relativePath");val image=file.getString("name").substringAfterLast('.').lowercase() in listOf("jpg","jpeg","png","webp","gif","bmp");if(which==1){val saved=AndroidTransfers.download(engine(),peer,id,path,File(filesDir,"downloads"));runOnUiThread{openLocal(saved)}}else if(image){val saved=cachePhoto(peer,id,path);runOnUiThread{openLocal(saved)}}else{val url=engine().stream(peer,id,path);val type=if(path.substringAfterLast('.').lowercase() in listOf("mp3","flac","wav","m4a","ogg","opus"))"audio/*" else "video/*";runOnUiThread{try{startActivity(Intent.createChooser(Intent(Intent.ACTION_VIEW).setDataAndType(Uri.parse(url),type),"재생할 앱 선택"))}catch(e:Exception){message.text="URL 재생을 지원하는 플레이어 앱을 설치하세요"}}}}}.show()}
    private fun cachePhoto(peer:MeshEngine.Peer,id:String,path:String):File {val resource=engine().resource("content",id,path);val c=engine().connection(peer,resource);require(c.responseCode==200);require(c.contentLengthLong in 0..(256L*1024*1024));val dir=File(cacheDir,"photos").apply{mkdirs()};val key=java.security.MessageDigest.getInstance("SHA-256").digest((peer.id+resource+c.getHeaderField("ETag")+c.lastModified+c.contentLengthLong).toByteArray()).joinToString(""){"%02x".format(it)};val target=File(dir,key+"."+path.substringAfterLast('.'));try{if(!target.exists()){val temp=File(dir,"$key.tmp");c.inputStream.use{input->temp.outputStream().use{out->var total=0L;val buffer=ByteArray(65536);while(true){val n=input.read(buffer);if(n<0)break;total+=n;require(total<=256L*1024*1024);out.write(buffer,0,n)};require(total==c.contentLengthLong)}};check(temp.renameTo(target))};target.setLastModified(System.currentTimeMillis());var size=dir.listFiles().orEmpty().sumOf{it.length()};dir.listFiles().orEmpty().sortedBy{it.lastModified()}.forEach{if(size>1024L*1024*1024&&it!=target){val n=it.length();if(it.delete())size-=n}};return target}finally{c.disconnect()}}
    private fun openLocal(file:File){try{val uri=FileProvider.getUriForFile(this,"com.eslee.meshdrive.files",file);val mime=android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(file.extension.lowercase())?:"application/octet-stream";startActivity(Intent.createChooser(Intent(Intent.ACTION_VIEW).setDataAndType(uri,mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),"열 앱 선택"))}catch(e:Exception){message.text="파일 저장 완료: ${file.name}. 열 수 있는 앱이 없습니다."}}
    @android.annotation.SuppressLint("WrongConstant") // Persist only the URI grants actually returned by the document picker.
    @Deprecated("Platform activity result API") override fun onActivityResult(requestCode:Int,resultCode:Int,data:Intent?){super.onActivityResult(requestCode,resultCode,data);if(requestCode==21&&resultCode==RESULT_OK){data?.data?.let{uploadPicked(it)};return};if(requestCode==20&&resultCode==RESULT_OK){val uri=data?.data?:return;contentResolver.takePersistableUriPermission(uri,data.flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION));val input=EditText(this);AlertDialog.Builder(this).setTitle("공유 표시 이름").setView(input).setPositiveButton("읽기 전용으로 공유"){_,_->engine().addShare(uri,input.text.toString().ifBlank{"공유 폴더"});home()}.setNegativeButton("취소",null).show()}}
    override fun onDestroy(){handler.removeCallbacks(refresh);worker.shutdown();super.onDestroy()}
}
