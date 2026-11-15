package dev.littleloop.chat

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.io.IOException
import java.security.KeyStore
import java.util.concurrent.TimeUnit
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONArray
import org.json.JSONObject

data class Session(val base: HttpUrl, val cookie: String)
data class Room(val id: String, val title: String, val type: String, val latest: Long, val unread: Long,
                val read: Long, val muted: Boolean, val archived: Boolean, val canPost: Boolean, val preview: String)
data class ChatMessage(val sequence: Long, val sender: String, val text: String, val deleted: Boolean, val sentAt: String)
class ChatRequestException(val status: Int, val body: String) : IOException("HTTP $status")

class SessionStorage(context: Context) {
    private val prefs = context.getSharedPreferences("chat_session", Context.MODE_PRIVATE)
    private val keyName = "littleloop_chat_session_v1"
    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").also { it.load(null) }
        (store.getKey(keyName, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(keyName, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
        }.generateKey()
    }

    fun read(): Session? {
        val saved = prefs.getString("session", null)?.split('.') ?: return null
        return try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, Base64.decode(saved[0], Base64.NO_WRAP)))
            val json = JSONObject(String(cipher.doFinal(Base64.decode(saved[1], Base64.NO_WRAP)), Charsets.UTF_8))
            Session(ChatApi.validateUrl(json.getString("base")), json.getString("cookie"))
        } catch (_: Exception) {
            clear()
            null
        }
    }

    fun save(session: Session) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val payload = json("base" to session.base.toString(), "cookie" to session.cookie).toString().toByteArray()
        val encoded = Base64.encodeToString(cipher.iv, Base64.NO_WRAP) + "." +
            Base64.encodeToString(cipher.doFinal(payload), Base64.NO_WRAP)
        check(prefs.edit().putString("session", encoded).commit())
    }

    fun clear() { prefs.edit().remove("session").commit() }
}

class ChatApi(private val storage: SessionStorage) {
    private val http = OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .callTimeout(12, TimeUnit.SECONDS).build()
    @Volatile var session: Session? = storage.read()
        private set

    companion object {
        fun validateUrl(raw: String): HttpUrl {
            val url = raw.trim().toHttpUrlOrNull() ?: throw IllegalArgumentException("Invalid server URL")
            val local = BuildConfig.DEBUG && url.scheme == "http" && url.host in setOf("127.0.0.1", "10.0.2.2", "localhost")
            require((url.isHttps || local) && url.encodedPath == "/" && url.query == null &&
                url.fragment == null && url.username.isEmpty() && url.password.isEmpty()) { "Use the server's HTTPS origin." }
            return url
        }
    }

    suspend fun login(origin: String, name: String, password: String) = withContext(Dispatchers.IO) {
        val base = validateUrl(origin)
        val request = build(base, null, "/login", json("name" to name, "password" to password))
        http.newCall(request).execute().use { response ->
            val body = response.body?.string().orEmpty()
            if (!response.isSuccessful) throw ChatRequestException(response.code, body)
            val cookie = response.headers("Set-Cookie").map { it.substringBefore(';') }
                .firstOrNull { it.startsWith("LittleLoop.Chat=") && it.length > "LittleLoop.Chat=".length }
                ?: throw IOException("Server did not return a session.")
            session = Session(base, cookie).also(storage::save)
        }
    }

    suspend fun logout() {
        try { request("/logout", JSONObject()) }
        catch (error: ChatRequestException) { if (error.status != 401) throw error }
        clear()
    }

    fun clear() { session = null; storage.clear() }

    suspend fun request(path: String, body: JSONObject? = null): String = withContext(Dispatchers.IO) {
        val current = session ?: throw ChatRequestException(401, "")
        http.newCall(build(current.base, current.cookie, path, body)).execute().use { response ->
            val text = response.body?.string().orEmpty()
            if (!response.isSuccessful) throw ChatRequestException(response.code, text)
            text
        }
    }

    fun connect(listener: WebSocketListener): WebSocket? {
        val current = session ?: return null
        val url = current.base.resolve("/hubs/chat") ?: return null
        return http.newWebSocket(Request.Builder().url(url).header("Cookie", current.cookie).build(), listener)
    }

    private fun build(base: HttpUrl, cookie: String?, path: String, body: JSONObject?): Request {
        val url = base.resolve("/api/chat$path") ?: throw IOException("Invalid API path.")
        val request = Request.Builder().url(url).header("Accept", "application/json")
        if (cookie != null) request.header("Cookie", cookie)
        if (body != null) request.header("X-LittleLoop-Chat", "1")
            .post(body.toString().toRequestBody("application/json".toMediaType()))
        return request.build()
    }
}

fun json(vararg fields: Pair<String, Any?>) = JSONObject().apply { fields.forEach { (key, value) -> put(key, value ?: JSONObject.NULL) } }

inline fun <T> JSONArray.items(read: (JSONObject) -> T): List<T> = (0 until length()).map { read(getJSONObject(it)) }
