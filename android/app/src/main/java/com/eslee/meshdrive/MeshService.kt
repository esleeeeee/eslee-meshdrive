package com.eslee.meshdrive
import android.app.*
import android.content.Intent
import android.os.IBinder
class MeshService:Service(){
    companion object { @Volatile var engine:MeshEngine?=null }
    override fun onCreate(){super.onCreate();val notifications=getSystemService(NotificationManager::class.java);notifications.createNotificationChannel(NotificationChannel("meshdrive","MeshDrive 연결",NotificationManager.IMPORTANCE_LOW));val open=PendingIntent.getActivity(this,0,Intent(this,MainActivity::class.java),PendingIntent.FLAG_IMMUTABLE);startForeground(1,Notification.Builder(this,"meshdrive").setContentTitle("MeshDrive 실행 중").setContentText("공유한 폴더에 연결할 수 있습니다").setSmallIcon(android.R.drawable.stat_sys_upload_done).setContentIntent(open).build());try{engine=MeshEngine(this).also{it.start()}}catch(e:Exception){stopSelf()}}
    override fun onStartCommand(intent:Intent?,flags:Int,startId:Int)=START_STICKY
    override fun onBind(intent:Intent?):IBinder?=null
    override fun onDestroy(){engine?.close();engine=null;super.onDestroy()}
}
