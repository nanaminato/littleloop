package dev.littleloop.chat

import android.content.Context
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
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
    val context = LocalContext.current
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
            when (val result = botAttempt(context) { bot.work(id) }) {
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
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text(cardTitle(card.kind)) },
        text = {
            Column(
                Modifier.heightIn(max = 470.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(5.dp),
            ) {
                card.source?.let { citation ->
                    Evidence(
                        stringResource(R.string.bot_source_ref, citation.path, citation.start, citation.end),
                        "SHA-256 ${citation.hash}\n${citation.text}",
                    )
                }
                if (id != null && work == null && loadError == null) LinearProgressIndicator(Modifier.fillMaxWidth())
                loadError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                work?.let { item ->
                    Text(workLabel(item.status), style = MaterialTheme.typography.titleMedium)
                    Text(providerName(item.provider), style = MaterialTheme.typography.labelMedium)
                    item.error?.let { Text(it) }
                    item.output?.takeIf(String::isNotBlank)?.let { Evidence(stringResource(R.string.bot_result), it) }
                    item.approval?.takeUnless { it.nonce == handled }?.let { approval ->
                        Text(stringResource(R.string.bot_awaiting), style = MaterialTheme.typography.titleMedium)
                        Text(approval.method)
                        Text(approval.parameters, fontFamily = FontFamily.Monospace)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            for ((value, label) in listOf(
                                "accept" to stringResource(R.string.bot_allow),
                                "decline" to stringResource(R.string.bot_decline),
                            )) {
                                TextButton(onClick = {
                                    val task = id ?: return@TextButton
                                    busy = true
                                    decisionError = null
                                    scope.launch {
                                        try {
                                            when (val result = botAttempt(context) { bot.decide(task, approval, value) }) {
                                                is BotResult.Success -> handled = approval.nonce
                                                is BotResult.Error -> {
                                                    if (result.status == 409) handled = approval.nonce
                                                    decisionError = result.message
                                                }
                                            }
                                            refresh++
                                        } finally {
                                            busy = false
                                        }
                                    }
                                }, enabled = !busy) { Text(label) }
                            }
                        }
                    }
                    for ((path, patch) in item.patches) key(path) {
                        Evidence(stringResource(R.string.bot_patch, path), patch)
                    }
                    for ((index, receipt) in item.receipts.withIndex()) key(index, receipt.id) {
                        Evidence(
                            stringResource(R.string.bot_receipt, receipt.id, receipt.exitCode),
                            listOf(receipt.stdout, receipt.stderr).filter(String::isNotBlank).joinToString("\n"),
                        )
                    }
                }
                decisionError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.action_close)) } },
    )
}

@Composable
private fun Evidence(label: String, body: String) {
    var expanded by rememberSaveable { mutableStateOf(false) }
    TextButton(onClick = { expanded = !expanded }, modifier = Modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.small) { Text(label, Modifier.fillMaxWidth()) }
    if (expanded) Text(body, Modifier.horizontalScroll(rememberScrollState()), fontFamily = FontFamily.Monospace)
}

@Composable
internal fun BotRequestDialog(bot: BotChat, room: String, providers: List<String>, anchor: ChatMessage?,
                              dismiss: () -> Unit) {
    val context = LocalContext.current
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
        if (asking) when (val result = botAttempt(context) { bot.sources(room) }) {
            is BotResult.Success -> paths = result.value
            is BotResult.Error -> error = result.message
        }
    }
    if (picker) {
        AlertDialog(
            onDismissRequest = { picker = false },
            title = { Text(stringResource(R.string.bot_select_source)) },
            text = {
                Column {
                    OutlinedTextField(
                        filter, { filter = it }, label = { Text(stringResource(R.string.bot_filter_files)) },
                        singleLine = true, shape = MaterialTheme.shapes.medium,
                    )
                    Column(Modifier.heightIn(max = 300.dp).verticalScroll(rememberScrollState())) {
                        paths.orEmpty().filter { it.contains(filter, ignoreCase = true) }.forEach { candidate ->
                            TextButton(onClick = { path = candidate; picker = false }, shape = MaterialTheme.shapes.small) { Text(candidate) }
                        }
                    }
                }
            },
            confirmButton = { TextButton(onClick = { picker = false }) { Text(stringResource(R.string.action_back)) } },
        )
        return
    }
    AlertDialog(
        onDismissRequest = { if (!busy) dismiss() },
        title = { Text(stringResource(if (asking) R.string.bot_ask else R.string.bot_schedule)) },
        text = {
            Column(
                Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (providers.size > 1) Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (name in providers) {
                        FilterChip(name == provider, { provider = name }, { Text(providerName(name)) }, enabled = !busy)
                    }
                }
                if (asking) {
                    TextButton(onClick = { picker = true }, enabled = !busy && paths != null) {
                        Text(path.ifBlank { stringResource(R.string.bot_select_source) }, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        OutlinedTextField(
                            first, { first = it.filter(Char::isDigit).take(7) }, Modifier.weight(1f),
                            label = { Text(stringResource(R.string.bot_start_line)) }, singleLine = true, enabled = !busy,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            shape = MaterialTheme.shapes.medium,
                        )
                        OutlinedTextField(
                            last, { last = it.filter(Char::isDigit).take(7) }, Modifier.weight(1f),
                            label = { Text(stringResource(R.string.bot_end_line)) }, singleLine = true, enabled = !busy,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            shape = MaterialTheme.shapes.medium,
                        )
                    }
                } else {
                    Text(stringResource(R.string.bot_source_message, (anchor?.sequence ?: 0).toInt()))
                    Text(anchor?.text.orEmpty(), maxLines = 2, overflow = TextOverflow.Ellipsis)
                }
                OutlinedTextField(
                    text, { if (it.length <= if (asking) 4000 else 6000) text = it },
                    Modifier.fillMaxWidth(), enabled = !busy, minLines = 3,
                    label = { Text(stringResource(if (asking) R.string.bot_question else R.string.bot_goal)) },
                    shape = MaterialTheme.shapes.medium,
                )
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
                        when (val result = botAttempt(context) {
                            if (anchor == null) bot.ask(room, requestId, text, path, start, end, provider)
                            else bot.schedule(room, requestId, anchor.sequence, text, provider)
                        }) {
                            is BotResult.Success -> dismiss()
                            is BotResult.Error -> error = result.message
                        }
                    } finally {
                        busy = false
                    }
                }
            }, enabled = ready && !busy) {
                Text(stringResource(if (asking) R.string.bot_submit_question else R.string.bot_submit_task))
            }
        },
        dismissButton = { TextButton(onClick = dismiss, enabled = !busy) { Text(stringResource(R.string.action_cancel)) } },
    )
}

internal sealed interface BotResult<out T> {
    data class Success<T>(val value: T) : BotResult<T>
    data class Error(val status: Int?, val message: String) : BotResult<Nothing>
}

internal suspend fun <T> botAttempt(context: Context, action: suspend () -> T): BotResult<T> = try {
    BotResult.Success(action())
} catch (cancelled: CancellationException) {
    throw cancelled
} catch (error: ChatRequestException) {
    BotResult.Error(error.status, when (error.status) {
        400 -> runCatching { org.json.JSONTokener(error.body).nextValue() as? String }.getOrNull()
            ?.take(240) ?: context.getString(R.string.bot_check_input)
        401, 403 -> context.getString(R.string.bot_denied)
        404 -> context.getString(R.string.bot_unavailable)
        409 -> context.getString(R.string.bot_handled)
        else -> context.getString(R.string.bot_failed, error.status)
    })
} catch (_: Exception) {
    BotResult.Error(null, context.getString(R.string.bot_offline))
}

@Composable
internal fun cardTitle(kind: String) = when (kind) {
    "question" -> stringResource(R.string.bot_title_question)
    "citation" -> stringResource(R.string.bot_title_citation)
    else -> stringResource(R.string.bot_title_task)
}

@Composable
private fun providerName(name: String) = if (name == "source_review") stringResource(R.string.bot_provider_review) else "LittleLoop"

@Composable
private fun workLabel(status: String) = when (status) {
    "queued" -> stringResource(R.string.bot_status_queued)
    "running" -> stringResource(R.string.bot_status_running)
    "paused", "suspended" -> stringResource(R.string.bot_status_paused)
    "cancelled" -> stringResource(R.string.bot_status_cancelled)
    "waiting-input" -> stringResource(R.string.bot_status_waiting_input)
    "completed" -> stringResource(R.string.bot_status_completed)
    "failed" -> stringResource(R.string.bot_status_failed)
    "refused" -> stringResource(R.string.bot_status_refused)
    "disabled" -> stringResource(R.string.bot_status_disabled)
    "needs-reconciliation" -> stringResource(R.string.bot_status_needs_reconciliation)
    "needs-revision" -> stringResource(R.string.bot_status_needs_revision)
    "stale-source" -> stringResource(R.string.bot_status_stale_source)
    else -> status
}
