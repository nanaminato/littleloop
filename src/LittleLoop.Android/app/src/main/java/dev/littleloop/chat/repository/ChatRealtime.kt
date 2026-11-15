package dev.littleloop.chat

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject

/** SignalR provides invalidations. HTTP remains the source of message order and content. */
internal class ChatRealtime(
    private val api: ChatApi, private val scope: CoroutineScope, private val onChange: () -> Unit,
) {
    private var socket: WebSocket? = null
    private var handshake = false

    fun connect() {
        if (socket != null) return
        socket = api.connect(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                webSocket.send("{\"protocol\":\"json\",\"version\":1}\u001e")
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                scope.launch {
                    if (socket != webSocket) return@launch
                    var changed = false
                    for (frame in text.split('\u001e').filter(String::isNotBlank)) {
                        val item = runCatching { JSONObject(frame) }.getOrNull() ?: continue
                        if (item.has("error") || item.optInt("type") == 7) {
                            disconnect()
                            break
                        }
                        handshake = true
                        if (item.optInt("type") == 1) changed = true
                    }
                    if (changed) onChange()
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = ended(webSocket)
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = ended(webSocket)
        })
    }

    fun heartbeat() {
        if (handshake) socket?.send("{\"type\":6}\u001e")
    }

    fun disconnect() {
        socket?.cancel()
        socket = null
        handshake = false
    }

    private fun ended(closed: WebSocket) {
        scope.launch { if (socket == closed) disconnect() }
    }
}
