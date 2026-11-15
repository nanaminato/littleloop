package dev.littleloop.chat

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@Composable
internal fun BotDetails(bot: BotChat, card: BotCard, dismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    val id = card.taskId
    var work by remember(id) { mutableStateOf<BotWork?>(null) }
    var busy by remember(id) { mutableStateOf(false) }
    var handled by remember(id) { mutableStateOf<String?>(null) }
    var loadError by remember(id) { mutableStateOf<String?>(null) }
    var decisionError by remember(id) { mutableStateOf<String?>(null) }
    var refresh by remember(id) { mutableIntStateOf(0) }
    LaunchedEffect(id, refresh) {
        if (id == null) return@LaunchedEffect
        while (true) {
            when (val result = botAttempt { bot.work(id) }) {
                is BotResult.Success -> {
                    work = result.value
                    loadError = null
                    if (result.value.terminal) break
                }
                is BotResult.Error -> {
                    work = null
                    loadError = result.message
                    if (result.status in setOf(401, 403, 404)) break
                }
            }
            delay(1500)
        }
    }
    AlertDialog(onDismissRequest = dismiss,
        title = { Text(cardTitle(card.kind)) },
        text = {
            Column(Modifier.heightIn(max = 470.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(5.dp)) {
                card.source?.let { citation ->
                    Evidence("源码引用 · ${citation.path}:${citation.start}–${citation.end}",
                        "SHA-256 ${citation.hash}\n${citation.text}")
                }
                if (id != null && work == null && loadError == null) LinearProgressIndicator(Modifier.fillMaxWidth())
                loadError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                work?.let { item ->
                    Text(workLabel(item.status), style = MaterialTheme.typography.titleMedium)
                    Text(providerName(item.provider), style = MaterialTheme.typography.labelMedium)
                    item.error?.let { Text(it) }
                    item.output?.takeIf(String::isNotBlank)?.let { Evidence("执行结果", it) }
                    item.approval?.takeUnless { it.nonce == handled }?.let { approval ->
                        Text("等待审批", style = MaterialTheme.typography.titleMedium)
                        Text(approval.method)
                        Text(approval.parameters, fontFamily = FontFamily.Monospace)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            for ((value, label) in listOf("accept" to "允许本次", "decline" to "拒绝")) {
                                TextButton(onClick = {
                                    val task = id ?: return@TextButton
                                    busy = true
                                    decisionError = null
                                    scope.launch {
                                        try {
                                            when (val result = botAttempt { bot.decide(task, approval, value) }) {
                                                is BotResult.Success -> handled = approval.nonce
                                                is BotResult.Error -> {
                                                    if (result.status == 409) handled = approval.nonce
                                                    decisionError = result.message
                                                }
                                            }
                                            refresh++
                                        } finally { busy = false }
                                    }
                                }, enabled = !busy) { Text(label) }
                            }
                        }
                    }
                    for ((path, patch) in item.patches) key(path) { Evidence("代码补丁 · $path", patch) }
                    for ((index, receipt) in item.receipts.withIndex()) key(index, receipt.id) {
                        Evidence("测试回执 · ${receipt.id} · 退出码 ${receipt.exitCode}",
                            listOf(receipt.stdout, receipt.stderr).filter(String::isNotBlank).joinToString("\n"))
                    }
                }
                decisionError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        }, confirmButton = { TextButton(onClick = dismiss) { Text("关闭") } })
}

@Composable
private fun Evidence(label: String, body: String) {
    var expanded by rememberSaveable { mutableStateOf(false) }
    TextButton(onClick = { expanded = !expanded }) { Text(label) }
    if (expanded) Text(body, Modifier.horizontalScroll(rememberScrollState()), fontFamily = FontFamily.Monospace)
}

@Composable
internal fun BotRequestDialog(bot: BotChat, room: String, providers: List<String>, anchor: ChatMessage?,
                              dismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    val asking = anchor == null
    val preferred = if ("littleloop" in providers) "littleloop" else providers.firstOrNull().orEmpty()
    var provider by rememberSaveable { mutableStateOf(preferred) }
    var paths by remember { mutableStateOf<List<String>?>(null) }
    var path by rememberSaveable { mutableStateOf("") }
    var picker by remember { mutableStateOf(false) }
    var filter by remember { mutableStateOf("") }
    var first by rememberSaveable { mutableStateOf("1") }
    var last by rememberSaveable { mutableStateOf("20") }
    var text by rememberSaveable { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val requestId = rememberSaveable(provider, path, first, last, text) { UUID.randomUUID().toString() }
    LaunchedEffect(room, asking) {
        if (asking) when (val result = botAttempt { bot.sources(room) }) {
            is BotResult.Success -> paths = result.value
            is BotResult.Error -> error = result.message
        }
    }
    if (picker) {
        AlertDialog(onDismissRequest = { picker = false }, title = { Text("选择源码文件") }, text = {
            Column {
                OutlinedTextField(filter, { filter = it }, label = { Text("筛选文件") }, singleLine = true)
                Column(Modifier.heightIn(max = 300.dp).verticalScroll(rememberScrollState())) {
                    paths.orEmpty().filter { it.contains(filter, ignoreCase = true) }.forEach { candidate ->
                        TextButton(onClick = { path = candidate; picker = false }) { Text(candidate) }
                    }
                }
            }
        }, confirmButton = { TextButton(onClick = { picker = false }) { Text("返回") } })
        return
    }
    AlertDialog(onDismissRequest = { if (!busy) dismiss() },
        title = { Text(if (asking) "引用源码提问" else "安排开发任务") },
        text = {
            Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (providers.size > 1) Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (name in providers) FilterChip(name == provider, { provider = name }, { Text(providerName(name)) }, enabled = !busy)
                }
                if (asking) {
                    TextButton(onClick = { picker = true }, enabled = !busy && paths != null) {
                        Text(path.ifBlank { "选择源码文件" }, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        OutlinedTextField(first, { first = it.filter(Char::isDigit).take(7) }, Modifier.weight(1f),
                            label = { Text("起始行") }, singleLine = true, enabled = !busy,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
                        OutlinedTextField(last, { last = it.filter(Char::isDigit).take(7) }, Modifier.weight(1f),
                            label = { Text("结束行") }, singleLine = true, enabled = !busy,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
                    }
                } else {
                    Text("来源消息 #${anchor?.sequence}")
                    Text(anchor?.text.orEmpty(), maxLines = 2, overflow = TextOverflow.Ellipsis)
                }
                OutlinedTextField(text, { if (it.length <= if (asking) 4000 else 6000) text = it },
                    Modifier.fillMaxWidth(), enabled = !busy, minLines = 3,
                    label = { Text(if (asking) "问题" else "任务目标和验收") })
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            val start = first.toIntOrNull() ?: 0
            val end = last.toIntOrNull() ?: 0
            val ready = provider.isNotBlank() && text.isNotBlank() && (!asking || (path.isNotBlank() && start > 0 && end - start in 0..79))
            Button(onClick = {
                busy = true
                error = null
                scope.launch {
                    try {
                        when (val result = botAttempt {
                            if (anchor == null) bot.ask(room, requestId, text, path, start, end, provider)
                            else bot.schedule(room, requestId, anchor.sequence, text, provider)
                        }) {
                            is BotResult.Success -> dismiss()
                            is BotResult.Error -> error = result.message
                        }
                    } finally { busy = false }
                }
            }, enabled = ready && !busy) { Text(if (asking) "提交源码提问" else "提交开发任务") }
        }, dismissButton = { TextButton(onClick = dismiss, enabled = !busy) { Text("取消") } })
}

internal sealed interface BotResult<out T> {
    data class Success<T>(val value: T) : BotResult<T>
    data class Error(val status: Int?, val message: String) : BotResult<Nothing>
}

internal suspend fun <T> botAttempt(action: suspend () -> T): BotResult<T> = try {
    BotResult.Success(action())
} catch (cancelled: CancellationException) {
    throw cancelled
} catch (error: ChatRequestException) {
    BotResult.Error(error.status, when (error.status) {
        400 -> runCatching { org.json.JSONTokener(error.body).nextValue() as? String }.getOrNull()
            ?.take(240) ?: "请检查输入"
        401, 403 -> "当前账号无权执行此操作"
        404 -> "会话或任务不可访问"
        409 -> "此操作已处理或已失效"
        else -> "机器人请求失败（${error.status}）"
    })
} catch (_: Exception) {
    BotResult.Error(null, "无法连接机器人，请重试")
}

internal fun cardTitle(kind: String) = when (kind) {
    "question" -> "源码问答"
    "citation" -> "源码引用"
    else -> "开发任务"
}

private fun providerName(name: String) = if (name == "source_review") "源码审阅" else "LittleLoop"

private fun workLabel(status: String) = when (status) {
    "queued" -> "排队中"
    "running" -> "执行中"
    "completed" -> "已完成"
    "failed" -> "失败"
    "refused" -> "核心拒绝"
    "disabled" -> "机器人已停用"
    "needs-reconciliation" -> "待核对"
    "needs-revision" -> "验收未通过"
    "stale-source" -> "源码已变更"
    else -> status
}
