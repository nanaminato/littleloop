package dev.littleloop.chat

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class ChatNotificationService : Service() {
    private val chat get() = (application as ChatApplication).chat
    private val notifications get() = getSystemService(NotificationManager::class.java)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    override fun onCreate() {
        super.onCreate()
        notifications.createNotificationChannels(listOf(
            NotificationChannel("connection", "后台消息连接", NotificationManager.IMPORTANCE_LOW),
            NotificationChannel("messages", "新聊天消息", NotificationManager.IMPORTANCE_DEFAULT),
            NotificationChannel("silent_messages", "静默聊天消息", NotificationManager.IMPORTANCE_LOW),
        ))
        startForeground(6001, notification("connection", null).setContentTitle("LittleLoop")
            .setContentText("后台消息连接已启用").setOngoing(true).build())
        val seen = chat.state.value.rooms.associateBy(Room::id).toMutableMap()
        chat.listening(true)
        scope.launch {
            chat.state.collect { state ->
                if (state.username.isEmpty()) {
                    stopSelf()
                    return@collect
                }
                val settings = state.settings.optJSONObject("notifications")
                for (room in state.rooms) {
                    val previous = seen.put(room.id, room)
                    val category = when (room.type) {
                        "direct" -> "direct"
                        "channel" -> "channels"
                        else -> "groups"
                    }
                    val newMessage = room.unread > 0 && (previous == null ||
                        room.latest > previous.latest && room.unread > previous.unread)
                    if (!chat.foreground && newMessage && !room.muted && settings?.optBoolean(category, true) != false) {
                        val preview = if (settings?.optBoolean("preview", true) == false || room.preview.isBlank())
                            "你有一条新消息" else room.preview
                        val channel = if (settings?.optBoolean("sound", true) == false) "silent_messages" else "messages"
                        notifications.notify(room.id, 1, notification(channel, room.id).setContentTitle(room.title)
                            .setContentText(preview).setAutoCancel(true).setVisibility(Notification.VISIBILITY_PRIVATE).build())
                    }
                    if (room.unread == 0L) notifications.cancel(room.id, 1)
                }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int) = START_NOT_STICKY
    override fun onBind(intent: Intent?): IBinder? = null
    override fun onDestroy() {
        scope.cancel()
        chat.listening(false)
        notifications.cancelAll()
        super.onDestroy()
    }

    private fun notification(channel: String, room: String?): Notification.Builder {
        val launch = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        if (room != null) {
            launch.data = android.net.Uri.Builder().scheme("littleloop").authority("chat").appendPath(room).build()
            launch.putExtra("room", room)
        }
        val pending = PendingIntent.getActivity(this, 0, launch, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Builder(this, channel).setSmallIcon(R.drawable.ic_chat).setContentIntent(pending)
    }
}
