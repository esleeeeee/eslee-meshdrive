package com.eslee.meshdrive

import android.app.*
import android.content.Intent
import android.net.Uri
import android.os.*
import android.widget.*
import androidx.core.content.FileProvider
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors

class MainActivity:ComponentActivity(){
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
    private val directJobs=mutableListOf<Pair<MeshEngine.Peer,String>>()
    private data class DownloadTarget(val peer:MeshEngine.Peer,val share:String,val path:String)
    private var downloadTarget:DownloadTarget?=null
    private val refresh=object:Runnable{override fun run(){val p=MeshService.engine?.pairing;if(p!=null&&displayedSession!=p.offer.optString("sessionId")&&System.currentTimeMillis()<p.expires){displayedSession=p.offer.optString("sessionId");AlertDialog.Builder(this@MainActivity).setTitle("${p.peer.name} · ${p.sas}").setMessage("상대 기기에도 같은 6자리 번호가 보이면 승인하세요.").setPositiveButton("승인"){_,_->runWork{engine().decide(true)}}.setNegativeButton("거절"){_,_->runWork{engine().decide(false)}}.show()};handler.postDelayed(this,1500)}}
    override fun onCreate(savedInstanceState:Bundle?){super.onCreate(savedInstanceState);if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS),1);startForegroundService(Intent(this,MeshService::class.java));val scroll=ScrollView(this);body=LinearLayout(this).apply{orientation=LinearLayout.VERTICAL;setPadding(24,36,24,24)};scroll.addView(body);setContentView(scroll);message=TextView(this);home();handler.post(refresh)}
    private fun engine()=MeshService.engine?:throw IllegalStateException("Agent 준비 중입니다. 잠시 후 새로고침하세요.")
    private fun button(text:String,action:()->Unit){body.addView(Button(this).apply{this.text=text;setOnClickListener{action()}})}
    private fun label(text:String){body.addView(TextView(this).apply{this.text=text;textSize=18f;setPadding(4,12,4,12)})}
    private fun reset(title:String){body.removeAllViews();label(title);body.addView(message);button("선택 폴더 동기화 · 이전 버전"){showSync()}}
    private fun runWork(action:()->Unit){message.text="처리 중…";worker.execute{try{action();runOnUiThread{message.text="완료"}}catch(e:Exception){runOnUiThread{message.text=e.message?:"작업 실패"}}}}
    private fun home(){selected=null;share=null;path="";reset("MeshDrive · 내 기기의 파일을 원본 그대로");button("기기 새로고침"){home()};button("공유 폴더 추가"){startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION),20)};button("공유 폴더 관리"){showShares()};val e=MeshService.engine;if(e==null){label("백그라운드 Agent 준비 중");return};label(e.status);e.peers.values.sortedBy{it.name}.forEach{p->button("${p.name} · ${if(e.trusted(p.id))"연결됨" else "페어링 필요"} · ${if(p.online)"온라인" else "오프라인"}"){if(e.trusted(p.id)){selected=p;runWork{val shares=e.json(p,"/v1/secure/storage/shares");runOnUiThread{reset(p.name);button("내 기기 목록"){home()};for(i in 0 until shares.length()){val s=shares.getJSONObject(i);button(s.getString("name")){share=s.getString("id");path="";browse()}};button("연결 해제"){e.unpair(p.id);home()}}}}else runWork{e.connect(p)}}};button(if(e.paused)"공유 다시 시작" else "공유 일시 중지"){e.paused=!e.paused;home()};button("MeshDrive 전체 종료"){stopService(Intent(this,MeshService::class.java));finish()}}
    private fun showShares(){reset("내 공유 폴더");button("뒤로"){home()};val shares=engine().localShares();for(i in 0 until shares.length()){val s=shares.getJSONObject(i);button(s.getString("name")){AlertDialog.Builder(this).setTitle("공유 권한").setItems(arrayOf("읽기 전용","양방향 복사","스트리밍 전용","기기별 권한","공유 해제")){_,which->if(which==3){devicePermissions(s);return@setItems};if(which==4)engine().removeShare(s.getString("id"))else engine().setSharePermissions(s.getString("id"),intArrayOf(7,15,3)[which]);showShares()}.show()}}}
    private fun devicePermissions(share:JSONObject){
        val devices=engine().peers.values.filter{engine().trusted(it.id)}.sortedBy{it.name}
        AlertDialog.Builder(this).setTitle("기기별 공유 권한").setItems(devices.map{it.name}.toTypedArray()){_,index->
            val peer=devices[index]
            AlertDialog.Builder(this).setTitle(peer.name).setItems(arrayOf("폴더 기본값 사용","접근 불가","읽기 전용","양방향 복사","스트리밍 전용")){_,choice->
                engine().setDevicePermissions(share.getString("id"),peer.id,arrayOf<Int?>(null,0,7,15,3)[choice]);showShares()
            }.setNegativeButton("취소",null).show()
        }.setNegativeButton("취소",null).show()
    }
    private fun showSync(){
        reset("선택 폴더 동기화")
        button("기기 목록"){home()}
        label("일반 공유와 별개입니다. 양쪽 폴더를 허용한 뒤 Windows의 ‘선택 폴더 동기화’에서 자동 규칙과 방향을 설정하세요.")
        button("동기화 허용 폴더 추가"){
            startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION),22)
        }
        button("이전 버전 보관 설정"){
            val fields=LinearLayout(this).apply{orientation=LinearLayout.VERTICAL}
            val count=EditText(this).apply{hint="파일당 보관 개수 (1~1000)";inputType=android.text.InputType.TYPE_CLASS_NUMBER;setText(engine().sync.retentionCount().toString())}
            val days=EditText(this).apply{hint="보관 일수 (1~3650)";inputType=android.text.InputType.TYPE_CLASS_NUMBER;setText(engine().sync.retentionDays().toString())}
            fields.addView(count);fields.addView(days)
            AlertDialog.Builder(this).setTitle("이전 버전 보관").setView(fields).setPositiveButton("저장"){_,_->runWork{engine().sync.retention(count.text.toString().toInt(),days.text.toString().toInt())}}.setNegativeButton("취소",null).show()
        }
        val roots=engine().sync.snapshot()
        for(i in 0 until roots.length()){
            val root=roots.getJSONObject(i);val id=root.getString("id")
            button(root.getString("name")){
                AlertDialog.Builder(this).setTitle(root.getString("name")).setItems(arrayOf("이전 버전 복원","동기화 허용 해제")){_,which->
                    if(which==1){AlertDialog.Builder(this).setTitle("동기화 허용을 해제할까요?").setMessage("원본 파일은 삭제하지 않습니다. Windows의 연결된 규칙은 더 이상 이 폴더에 접근할 수 없습니다.").setPositiveButton("해제"){_,_->engine().sync.remove(id);showSync()}.setNegativeButton("취소",null).show()}
                    else runWork {
                        val versions=engine().sync.versions(id)
                        runOnUiThread {
                            AlertDialog.Builder(this).setTitle("복원할 이전 버전").setItems(versions.map{"${it.getString("path")} · ${it.getString("createdAt")}"}.toTypedArray()){_,index->
                                AlertDialog.Builder(this).setTitle("이 버전으로 복원할까요?").setMessage("현재 파일도 이전 버전으로 보관합니다. 활성 동기화 규칙이 복원된 변경을 다른 기기에 반영할 수 있습니다.").setPositiveButton("복원"){_,_->runWork{engine().sync.restore(id,versions[index].getString("id"))}}.setNegativeButton("취소",null).show()
                            }.setNegativeButton("닫기",null).show()
                        }
                    }
                }.show()
            }
        }
    }
    private fun configureSyncRoot(uri:Uri){
        val name=EditText(this).apply{hint="동기화 폴더 이름"}
        AlertDialog.Builder(this).setTitle("동기화 폴더 별칭").setView(name).setPositiveButton("허용 기기 선택"){_,_->
            val peers=engine().peers.values.filter{engine().trusted(it.id)}.sortedBy{it.name};val selected=BooleanArray(peers.size)
            AlertDialog.Builder(this).setTitle("이 폴더의 파일 수정·삭제를 허용할 기기").setMultiChoiceItems(peers.map{it.name}.toTypedArray(),selected){_,index,checked->selected[index]=checked}
                .setPositiveButton("동기화 허용"){_,_->runWork{engine().sync.add(uri,name.text.toString().ifBlank{"동기화 폴더"},peers.filterIndexed{index,_->selected[index]}.map{it.id});runOnUiThread{showSync()}}}
                .setNegativeButton("취소",null).show()
        }.setNegativeButton("취소",null).show()
    }
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
                        text="${if(directory)"📁" else "📄"} ${file.getString("name")}${if(directory)"" else " · ${file.optLong("length")} bytes · ${file.optString("modifiedAt").take(19).replace('T',' ')}"}"
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
    private fun chooseFile(peer:MeshEngine.Peer,id:String,file:JSONObject){
        AlertDialog.Builder(this).setTitle(file.getString("name")).setItems(arrayOf("원본 열기","이 기기로 가져오기","다른 기기로 직접 복사","직접 복사 상태")){_,which->
            if(which==1){
                downloadTarget=DownloadTarget(peer,id,file.getString("relativePath"))
                val mime=android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(file.getString("name").substringAfterLast('.').lowercase())?:"application/octet-stream"
                startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType(mime).putExtra(Intent.EXTRA_TITLE,file.getString("name")),23)
                return@setItems
            }
            if(which==2){chooseCopyTarget(peer,id,file);return@setItems}
            if(which==3){runWork{val state=directJobs.toList().map{(p,job)->engine().objectRequest(p,"/v1/secure/storage/copy-progress?id=$job")}.joinToString("\n"){"${it.optString("name")} · ${it.optString("state")}"};runOnUiThread{AlertDialog.Builder(this).setTitle("직접 복사 상태").setMessage(state.ifEmpty{"아직 지시한 전송이 없습니다"}).setPositiveButton("확인",null).show()}};return@setItems}
            runWork {
                val path=file.getString("relativePath")
                val image=file.getString("name").substringAfterLast('.').lowercase() in listOf("jpg","jpeg","png","webp","gif","bmp")
                if(image){val saved=cachePhoto(peer,id,path);runOnUiThread{openLocal(saved)}}
                else {
                    val url=engine().stream(peer,id,path)
                    val type=if(path.substringAfterLast('.').lowercase() in listOf("mp3","flac","wav","m4a","ogg","opus"))"audio/*" else "video/*"
                    runOnUiThread{try{startActivity(Intent.createChooser(Intent(Intent.ACTION_VIEW).setDataAndType(Uri.parse(url),type),"재생할 앱 선택"))}catch(e:Exception){message.text="URL 재생을 지원하는 플레이어 앱을 설치하세요"}}
                }
            }
        }.show()
    }
    private fun chooseCopyTarget(source:MeshEngine.Peer,share:String,file:JSONObject){
        val candidates=engine().peers.values.filter{it.id!=source.id&&engine().trusted(it.id)}.sortedBy{it.name}
        if(candidates.isEmpty()){message.text="다른 받을 기기를 먼저 페어링하세요";return}
        AlertDialog.Builder(this).setTitle("받을 기기 · 세 기기 모두 서로 페어링 필요").setItems(candidates.map{it.name}.toTypedArray()){_,index->
            val target=candidates[index]
            runWork {
                val shares=engine().json(target,"/v1/secure/storage/shares")
                val writable=(0 until shares.length()).map{shares.getJSONObject(it)}.filter{it.getInt("permissions") and 8 != 0}
                runOnUiThread {
                    AlertDialog.Builder(this).setTitle("복사할 공유 폴더").setItems(writable.map{it.getString("name")}.toTypedArray()){_,selected->
                        runWork {
                            val ticket=engine().objectRequest(source,"/v1/secure/storage/copy-authorize",JSONObject().put("targetDeviceId",target.id).put("shareId",share).put("path",file.getString("relativePath")))
                            val job=engine().objectRequest(target,"/v1/secure/storage/copy-receive",JSONObject().put("sourceDeviceId",source.id).put("token",ticket.getString("token")).put("shareId",writable[selected].getString("id")).put("path",""))
                            runOnUiThread{directJobs.add(target to job.getString("id"))}
                        }
                    }.setNegativeButton("취소",null).show()
                }
            }
        }.setNegativeButton("취소",null).show()
    }
    private fun downloadPicked(uri:Uri){
        val target=downloadTarget?:return;downloadTarget=null
        runWork {
            try {
                val staged=AndroidTransfers.download(engine(),target.peer,target.share,target.path,File(filesDir,"download-staging"))
                DocumentCopies.publishNew(this,staged,uri);staged.delete()
                runOnUiThread{AlertDialog.Builder(this).setTitle("가져오기 완료").setMessage("선택한 위치에 원본 파일을 저장하고 무결성을 확인했습니다.").setPositiveButton("확인",null).show()}
            }catch(e:Exception){androidx.documentfile.provider.DocumentFile.fromSingleUri(this,uri)?.delete();throw e}
        }
    }
    private fun cachePhoto(peer:MeshEngine.Peer,id:String,path:String):File {val resource=engine().resource("content",id,path);val c=engine().connection(peer,resource);require(c.responseCode==200);require(c.contentLengthLong in 0..(256L*1024*1024));val dir=File(cacheDir,"photos").apply{mkdirs()};val key=java.security.MessageDigest.getInstance("SHA-256").digest((peer.id+resource+c.getHeaderField("ETag")+c.lastModified+c.contentLengthLong).toByteArray()).joinToString(""){"%02x".format(it)};val target=File(dir,key+"."+path.substringAfterLast('.'));try{if(!target.exists()){val temp=File(dir,"$key.tmp");c.inputStream.use{input->temp.outputStream().use{out->var total=0L;val buffer=ByteArray(65536);while(true){val n=input.read(buffer);if(n<0)break;total+=n;require(total<=256L*1024*1024);out.write(buffer,0,n)};require(total==c.contentLengthLong)}};check(temp.renameTo(target))};target.setLastModified(System.currentTimeMillis());var size=dir.listFiles().orEmpty().sumOf{it.length()};dir.listFiles().orEmpty().sortedBy{it.lastModified()}.forEach{if(size>1024L*1024*1024&&it!=target){val n=it.length();if(it.delete())size-=n}};return target}finally{c.disconnect()}}
    private fun openLocal(file:File){try{val uri=FileProvider.getUriForFile(this,"com.eslee.meshdrive.files",file);val mime=android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(file.extension.lowercase())?:"application/octet-stream";startActivity(Intent.createChooser(Intent(Intent.ACTION_VIEW).setDataAndType(uri,mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),"열 앱 선택"))}catch(e:Exception){message.text="파일 저장 완료: ${file.name}. 열 수 있는 앱이 없습니다."}}
    @android.annotation.SuppressLint("WrongConstant") // Persist only the URI grants actually returned by the document picker.
    @Deprecated("Platform activity result API") override fun onActivityResult(requestCode:Int,resultCode:Int,data:Intent?){super.onActivityResult(requestCode,resultCode,data);if(requestCode==23&&resultCode==RESULT_OK){data?.data?.let{downloadPicked(it)};return};if(requestCode==21&&resultCode==RESULT_OK){data?.data?.let{uploadPicked(it)};return};if(requestCode==22&&resultCode==RESULT_OK){val uri=data?.data?:return;contentResolver.takePersistableUriPermission(uri,data.flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION));configureSyncRoot(uri);return};if(requestCode==20&&resultCode==RESULT_OK){val uri=data?.data?:return;contentResolver.takePersistableUriPermission(uri,data.flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION));val input=EditText(this);AlertDialog.Builder(this).setTitle("공유 표시 이름").setView(input).setPositiveButton("읽기 전용으로 공유"){_,_->engine().addShare(uri,input.text.toString().ifBlank{"공유 폴더"});home()}.setNegativeButton("취소",null).show()}}
    override fun onStart(){super.onStart();if(!backRegistered){backRegistered=true;onBackPressedDispatcher.addCallback(this,object:OnBackPressedCallback(true){override fun handleOnBackPressed(){if(selected!=null&&path.isNotEmpty()){path=path.substringBeforeLast('/',"");browse()}else if(selected!=null)home()else finish()}})}}
    private var backRegistered=false
    override fun onDestroy(){handler.removeCallbacks(refresh);worker.shutdown();super.onDestroy()}
}
