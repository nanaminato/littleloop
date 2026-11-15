package dev.littleloop.chat

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import androidx.work.*
import com.google.android.gms.tasks.Tasks
import com.google.firebase.FirebaseApp
import com.google.firebase.messaging.FirebaseMessaging
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import java.io.IOException
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.withContext
import org.json.JSONObject

internal class NativePush(private val context: Context, private val api: ChatApi) {
    private val preferences = context.getSharedPreferences("fcm_device", Context.MODE_PRIVATE)
    private val seen = context.getSharedPreferences("fcm_notices", Context.MODE_PRIVATE)
    private val notifications get() = context.getSystemService(NotificationManager::class.java)
    private val strings get() = Localization.localized(context)
    private val lock = Any()
    val configured: Boolean get() = FirebaseApp.getApps(context).isNotEmpty()
    val enabled = MutableStateFlow(isEnabled(api.session))

    private fun isEnabled(session: Session?) = session != null &&
        preferences.getString("scope", null) == session.outboxKey

    fun sessionChanged() {
        synchronized(lock) {
            if (!isEnabled(api.session)) {
                preferences.edit().remove("scope").commit()
                cancelNotices()
            }
            enabled.value = isEnabled(api.session)
        }
    }

    suspend fun setEnabled(value: Boolean) {
        val session = api.session ?: throw ChatRequestException(401, "")
        if (value) {
            check(configured) { strings.getString(R.string.error_firebase_missing) }
            register(session)
            synchronized(lock) {
                if (api.session !== session) return
                check(preferences.edit().putString("scope", session.outboxKey).commit())
                enabled.value = true
            }
        } else {
            synchronized(lock) {
                preferences.edit().remove("scope").commit()
                enabled.value = false
                cancelNotices()
            }
            api.request("/settings/fcm/remove", JSONObject(), session)
        }
    }

    // HTTP v1 targets a registration token in Message.token.
    @Suppress("DEPRECATION")
    private suspend fun register(session: Session) {
        val project = FirebaseApp.getInstance().options.projectId ?: error(strings.getString(R.string.error_firebase_project))
        val server = JSONObject(api.request("/settings/fcm", expected = session)).stringOrNull("project")
        if (server != project) throw ChatRequestException(if (server == null) 404 else 400, "")
        val token = withContext(Dispatchers.IO) {
            Tasks.await(FirebaseMessaging.getInstance().token, 25, TimeUnit.SECONDS)
        }
        if (api.session !== session) return
        api.request("/settings/fcm", json("token" to token, "project" to project), session)
    }

    fun refreshRegistration() {
        val session = api.session ?: return
        if (!configured || !isEnabled(session)) return
        val request = OneTimeWorkRequestBuilder<FcmRegistrationWorker>().setInputData(workDataOf("scope" to session.outboxKey))
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()
        WorkManager.getInstance(context).enqueueUniqueWork("littleloop-fcm-registration", ExistingWorkPolicy.KEEP, request)
    }

    suspend fun refreshFor(scope: String) {
        val session = api.session ?: return
        if (session.outboxKey == scope && configured && isEnabled(session)) register(session)
    }

    fun receive(id: Long) {
        val session = api.session ?: return
        if (!isEnabled(session)) return
        val builder = OneTimeWorkRequestBuilder<FcmNoticeWorker>()
            .setInputData(workDataOf("id" to id, "scope" to session.outboxKey))
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
        if (Build.VERSION.SDK_INT >= 31) builder.setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
        WorkManager.getInstance(context).enqueueUniqueWork("littleloop-fcm-${session.outboxKey}-$id", ExistingWorkPolicy.KEEP, builder.build())
    }

    suspend fun show(id: Long, scope: String) {
        val session = api.session ?: return
        if (session.outboxKey != scope || !isEnabled(session)) return
        val key = "$scope:$id"
        if (seen.contains(key)) return
        val notice = JSONObject(api.request("/settings/fcm/notices/$id", expected = session))
        val room = notice.getString("room").asGuid() ?: return
        val sequence = notice.getLong("sequence").takeIf { it > 0 } ?: return
        val work = notice.stringOrNull("work")?.asGuid()
        synchronized(lock) {
            if (api.session !== session || !isEnabled(session) || seen.contains(key)) return
            if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED) return
            if (!notifications.areNotificationsEnabled()) return
            val normalId = if (work == null) "fcm_messages" else "fcm_approvals"
            val channelId = if (notice.optBoolean("sound", true)) normalId else "${normalId}_silent"
            notifications.createNotificationChannel(NotificationChannel(channelId,
                if (work == null) strings.getString(R.string.notification_cloud_message)
                else strings.getString(R.string.notification_cloud_work),
                if (channelId == normalId) NotificationManager.IMPORTANCE_DEFAULT else NotificationManager.IMPORTANCE_LOW).apply {
                if (channelId != normalId) setSound(null, null)
            })
            val tag = "fcm:$id"
            val target = Intent(context, MainActivity::class.java)
                .setData(Uri.parse("littleloop://notice/$id"))
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra("room", room).putExtra("sequence", sequence).putExtra("work", work)
                .putExtra("pushScope", scope)
            val pending = PendingIntent.getActivity(context, 0, target,
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val body = notice.getString("body")
            val notification = Notification.Builder(context, channelId).setSmallIcon(R.drawable.ic_chat)
                .setContentTitle(notice.getString("title")).setContentText(body)
                .setStyle(Notification.BigTextStyle().bigText(body)).setContentIntent(pending)
                .setAutoCancel(true).setOnlyAlertOnce(true).setVisibility(Notification.VISIBILITY_PRIVATE).build()
            notifications.notify(tag, 1, notification)
            val now = System.currentTimeMillis()
            val entries = seen.edit().putLong(key, now)
            seen.all.filterValues { value -> value is Long && now - value > DAY_MS }.keys.forEach(entries::remove)
            entries.commit()
        }
    }

    fun acceptsLink(scope: String) = api.session?.let { it.outboxKey == scope && isEnabled(it) } == true

    private fun cancelNotices() {
        notifications.activeNotifications.filter { it.tag?.startsWith("fcm:") == true }.forEach { notifications.cancel(it.tag, it.id) }
    }

    private fun String.asGuid() = runCatching { UUID.fromString(this).toString() }.getOrNull()

    private companion object { const val DAY_MS = 86_400_000L }
}

class ChatFcmMessagingService : FirebaseMessagingService() {
    @Suppress("OVERRIDE_DEPRECATION")
    override fun onNewToken(token: String) {
        (application as ChatApplication).chat.push.refreshRegistration()
    }

    override fun onMessageReceived(message: RemoteMessage) {
        val id = message.data["littleloop"]?.toLongOrNull()?.takeIf { it > 0 } ?: return
        (application as ChatApplication).chat.push.receive(id)
    }
}

class FcmNoticeWorker(context: Context, args: WorkerParameters) : CoroutineWorker(context, args) {
    override suspend fun doWork(): Result {
        val id = inputData.getLong("id", 0)
        val scope = inputData.getString("scope") ?: return Result.failure()
        val push = (applicationContext as ChatApplication).chat.push
        return try {
            push.show(id, scope)
            Result.success()
        } catch (error: ChatRequestException) {
            if (error.status == 429 || error.status >= 500) Result.retry() else Result.success()
        } catch (_: IOException) {
            Result.retry()
        }
    }
}

class FcmRegistrationWorker(context: Context, args: WorkerParameters) : CoroutineWorker(context, args) {
    override suspend fun doWork(): Result {
        val scope = inputData.getString("scope") ?: return Result.failure()
        return try {
            (applicationContext as ChatApplication).chat.push.refreshFor(scope)
            Result.success()
        } catch (error: ChatRequestException) {
            if (error.status in setOf(400, 401, 403, 404)) Result.failure() else Result.retry()
        } catch (error: Exception) {
            if (error is kotlinx.coroutines.CancellationException) throw error
            if (runAttemptCount >= 5) Result.failure() else Result.retry()
        }
    }
}
