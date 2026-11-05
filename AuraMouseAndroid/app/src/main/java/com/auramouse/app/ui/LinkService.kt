package com.auramouse.app.ui

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.IBinder
import com.auramouse.app.R

/** Keeps the connection alive while the app is in the background, as Android requires, with its notification. */
class LinkService : Service() {
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onCreate() {
        super.onCreate()
        val channel = NotificationChannel(CHANNEL, getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW)
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        // Without this the Wi-Fi naps between packets, and the cursor stutters.
        wifiLock = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "Aura Mouse:link").apply { acquire() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_DISCONNECT) {
            Link.disconnect()
            return START_NOT_STICKY
        }
        // Android insists on this even if it's no longer wanted by the time we get here.
        startForeground(ID, build(this), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        started = true
        if (!running) stopSelf()
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        wifiLock?.release()
        started = false
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        private const val CHANNEL = "link"
        private const val ID = 1
        private const val ACTION_DISCONNECT = "disconnect"
        @Volatile private var running = false
        @Volatile private var started = false

        fun start(context: Context) {
            if (running) return // already going, which also keeps a reconnect in the background legal
            running = true
            runCatching { context.startForegroundService(Intent(context, LinkService::class.java)) }
        }

        fun stop(context: Context) {
            running = false
            if (started) context.stopService(Intent(context, LinkService::class.java))
        }

        fun refresh(context: Context) {
            if (running) context.getSystemService(NotificationManager::class.java).notify(ID, build(context))
        }

        private fun build(context: Context): Notification {
            val name = Link.link?.hostName
            val text = if (Link.state == Link.State.CONNECTED && name != null) context.getString(R.string.connected_to, name)
                else context.getString(R.string.notify_searching)
            val open = PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
            val disconnect = PendingIntent.getService(context, 1,
                Intent(context, LinkService::class.java).setAction(ACTION_DISCONNECT), PendingIntent.FLAG_IMMUTABLE)
            return Notification.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_mouse)
                .setContentTitle(context.getString(R.string.app_name))
                .setContentText(text)
                .setContentIntent(open)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .addAction(Notification.Action.Builder(null, context.getString(R.string.disconnect), disconnect).build())
                .build()
        }
    }
}
