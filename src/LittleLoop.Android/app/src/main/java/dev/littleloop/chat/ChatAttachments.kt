package dev.littleloop.chat

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.unit.dp
import java.io.ByteArrayOutputStream
import java.io.InputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

internal fun InputStream.readLimitedAttachment(): ByteArray {
    val output = ByteArrayOutputStream()
    val buffer = ByteArray(16_384)
    while (true) {
        val count = read(buffer)
        if (count < 0) break
        require(output.size() + count <= MAX_ATTACHMENT_BYTES) { "文件大小不能超过 5 MiB" }
        output.write(buffer, 0, count)
    }
    require(output.size() > 0) { "无法发送空文件" }
    return output.toByteArray()
}

@Composable
internal fun AttachmentDialog(chat: ChatRepository, room: String, message: ChatMessage, dismiss: () -> Unit) {
    val file = message.attachment ?: return
    val scope = rememberCoroutineScope()
    var bitmap by remember { mutableStateOf<Bitmap?>(null) }
    var text by remember { mutableStateOf<String?>(null) }
    var status by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(false) }
    val previewText = file.name.substringAfterLast('.', "").lowercase() in setOf("txt", "md", "log", "json", "csv")
    val destination = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument(file.mime)) { uri ->
        if (uri != null) scope.launch {
            loading = true
            status = runCatching { chat.saveAttachment(room, message.sequence, uri) }
                .fold({ "已保存附件" }, { attachmentError(it) })
            loading = false
        }
    }
    LaunchedEffect(room, message.sequence) {
        if (file.image || previewText) {
            loading = true
            runCatching {
                val bytes = chat.readAttachment(room, message.sequence)
                if (file.image) bitmap = withContext(Dispatchers.Default) { decodePreview(bytes) }
                    ?: error("Unsupported image")
                else text = bytes.decodeToString(endIndex = minOf(bytes.size, 32_768)).take(12_000)
            }.onFailure { status = attachmentError(it) }
            loading = false
        }
    }
    AlertDialog(onDismissRequest = dismiss, title = { Text(file.name) }, text = {
        Column(Modifier.heightIn(max = 400.dp).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("${file.size} B · ${file.mime}")
            if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
            bitmap?.let { Image(it.asImageBitmap(), "图片预览", Modifier.fillMaxWidth()) }
            text?.let { Text(it) }
            status?.let { Text(it) }
        }
    }, confirmButton = {
        TextButton(onClick = { destination.launch(file.name) }, enabled = !loading) { Text("下载") }
    }, dismissButton = { TextButton(onClick = dismiss) { Text("关闭") } })
}

private fun attachmentError(error: Throwable): String =
    if (error is ChatRequestException && error.status in setOf(401, 403, 404)) "附件当前不可访问"
    else "无法读取或保存附件，请重试"

private fun decodePreview(bytes: ByteArray): Bitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
    var sample = 1
    while (maxOf(bounds.outWidth, bounds.outHeight) / sample > 1600) sample *= 2
    return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })
}
