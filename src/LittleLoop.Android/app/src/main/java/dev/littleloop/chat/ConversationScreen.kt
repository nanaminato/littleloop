package dev.littleloop.chat

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.customActions
import android.text.format.Formatter
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import kotlinx.coroutines.CancellationException

internal data class ChannelDiscussionSummary(val post: Long, val count: Long, val unread: Long)

@Composable
internal fun ConversationScreen(
    chat: ChatRepository,
    state: ChatState,
    room: Room,
    theme: String,
    onBack: () -> Unit,
    onTopics: () -> Unit,
    onMembers: () -> Unit,
    onSearch: () -> Unit,
) {
    var draft by rememberSaveable(room.id) { mutableStateOf("") }
    var replyTo by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    var file by remember(room.id) { mutableStateOf<SelectedAttachment?>(null) }
    var commentPost by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    var preview by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    var fileError by remember(room.id) { mutableStateOf<String?>(null) }
    var robot by remember(room.id) { mutableStateOf<BotRoom?>(null) }
    var botError by remember(room.id) { mutableStateOf<String?>(null) }
    var installing by remember(room.id) { mutableStateOf(false) }
    var asking by rememberSaveable(room.id) { mutableStateOf(false) }
    var scheduleFrom by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    var botDetails by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    val pickerScope = rememberCoroutineScope()
    val context = LocalContext.current
    val pickerFailed = stringResource(R.string.attach_picker_failed)
    val searchLabel = stringResource(R.string.conv_search_in)
    val moreLabel = stringResource(R.string.conv_more_actions)

    var discussionSummaries by remember(room.id) { mutableStateOf<Map<Long, ChannelDiscussionSummary>>(emptyMap()) }
    val discussionPosts = if (room.type == "channel") state.messages.filterNot(ChatMessage::deleted)
        .takeLast(100).map(ChatMessage::sequence) else emptyList()
    LaunchedEffect(room.id, discussionPosts) {
        while (discussionPosts.isNotEmpty()) {
            try { discussionSummaries = chat.discussionSummaries(room.id, discussionPosts) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { discussionSummaries = emptyMap() }
            delay(5000)
        }
    }
    LaunchedEffect(state.openComments, room.type) {
        val post = state.openComments
        if (post != null && room.type == "channel") {
            commentPost = post
            chat.commentLinkOpened()
        }
    }
    LaunchedEffect(state.openWork, state.focus) {
        if (state.openWork != null && state.focus != null) {
            botDetails = state.focus
            chat.workLinkOpened()
        }
    }
    LaunchedEffect(room.id, state.connected) {
        if (state.connected) {
            val result = botAttempt(context) { chat.bot.room(room.id) }
            if (result is BotResult.Success) robot = result.value
        }
    }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) pickerScope.launch {
            runCatching { chat.selectedAttachment(uri) }.onSuccess {
                file = it
                fileError = null
            }.onFailure { fileError = pickerFailed }
        }
    }

    val bySequence = remember(state.messages) { state.messages.associateBy(ChatMessage::sequence) }
    var menu by remember(room.id) { mutableStateOf<Long?>(null) }
    var channelSettings by remember(room.id) { mutableStateOf(false) }
    var headerMenu by remember(room.id) { mutableStateOf(false) }
    var messageOperation by remember(room.id) { mutableStateOf<Pair<Long, MessageOperation>?>(null) }
    var operationResult by remember(room.id) { mutableStateOf<String?>(null) }
    val confirmedIds = remember(state.messages, state.username) {
        state.messages.filter { it.sender == state.username }.mapTo(hashSetOf(), ChatMessage::clientId)
    }
    val queued = state.outbox.filter { it.room == room.id && it.id !in confirmedIds }
    val reversed = state.messages.asReversed()
    val list = rememberLazyListState()
    val visibleQueue = if (state.focus == null) queued else emptyList()
    LaunchedEffect(state.focus, state.notificationTarget) {
        val index = state.messages.asReversed().indexOfFirst { it.sequence == state.focus }
        if (index >= 0) list.scrollToItem(index)
    }
    LaunchedEffect(state.messages.lastOrNull()?.sequence, visibleQueue.lastOrNull()?.id) {
        if (state.focus == null && list.firstVisibleItemIndex <= 1) list.requestScrollToItem(0)
    }

    val dark = theme == "dark" || theme == "system" && androidx.compose.foundation.isSystemInDarkTheme()

    Column(Modifier.fillMaxSize()) {
        ChatTopBar(
            title = room.title,
            subtitle = roomTypeLabel(room.type).takeUnless { room.type == "saved" || it == room.title },
            back = onBack,
            actions = {
                TextButton(onClick = onSearch, modifier = Modifier.semantics {
                    contentDescription = searchLabel
                }) { Text("⌕", style = MaterialTheme.typography.titleLarge) }
                Box {
                    TextButton(onClick = { headerMenu = true }, modifier = Modifier.semantics {
                        contentDescription = moreLabel
                    }) { Text("⋮", style = MaterialTheme.typography.titleLarge) }
                    DropdownMenu(
                        expanded = headerMenu,
                        onDismissRequest = { headerMenu = false },
                        containerColor = MaterialTheme.colorScheme.surface,
                    ) {
                        DropdownMenuItem(
                            text = { Text(stringResource(if (room.muted) R.string.action_unmute else R.string.action_mute)) },
                            onClick = { headerMenu = false; chat.roomPreference(room) },
                        )
                        if (room.type == "channel") DropdownMenuItem(
                            text = { Text(stringResource(R.string.channel_settings)) },
                            onClick = { headerMenu = false; channelSettings = true },
                        )
                        if (room.type == "group") {
                            val unread = state.rooms.filter { it.type == "topic" && it.parentId == room.id }.sumOf(Room::unread)
                            DropdownMenuItem(
                                text = {
                                    Text(
                                        if (unread == 0L) stringResource(R.string.conv_topics)
                                        else stringResource(R.string.conv_topics_unread, unread.toInt()),
                                    )
                                },
                                onClick = { headerMenu = false; onTopics() },
                            )
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.conv_members)) },
                                onClick = { headerMenu = false; onMembers() },
                            )
                        }
                        if (state.connected && robot?.canOperate == true) {
                            if (robot?.installed == true) {
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.conv_bot_ask)) },
                                    onClick = { headerMenu = false; asking = true },
                                )
                            } else if (room.type != "topic") {
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.conv_bot_install)) },
                                    enabled = !installing,
                                    onClick = {
                                        headerMenu = false
                                        installing = true
                                        pickerScope.launch {
                                            try {
                                                when (val result = botAttempt(context) { chat.bot.install(room.id); chat.bot.room(room.id) }) {
                                                    is BotResult.Success -> { robot = result.value; botError = null }
                                                    is BotResult.Error -> botError = result.message
                                                }
                                            } finally {
                                                installing = false
                                            }
                                        }
                                    },
                                )
                            }
                        }
                    }
                }
            },
        )

        if (state.focus != null) {
            Surface(color = MaterialTheme.colorScheme.primaryContainer, tonalElevation = 0.dp) {
                Row(Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.conv_focused), Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                    TextButton(onClick = { chat.select(room.id) }) { Text(stringResource(R.string.conv_latest)) }
                }
            }
        }

        room.pinnedMessage?.let { sequence ->
            Surface(color = MaterialTheme.colorScheme.secondaryContainer, tonalElevation = 0.dp) {
                TextButton(onClick = { chat.openPinned(room) }, modifier = Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.conv_pinned, sequence.toInt()))
                }
            }
        }

        if (!state.connected) {
            Text(
                stringResource(R.string.conv_offline),
                Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 6.dp),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        state.error?.takeUnless { it == "Offline" }?.let {
            Text(it, Modifier.padding(horizontal = 14.dp, vertical = 4.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
        }
        fileError?.let { Text(it, Modifier.padding(horizontal = 14.dp, vertical = 4.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        botError?.let { Text(it, Modifier.padding(horizontal = 14.dp, vertical = 4.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        operationResult?.let { Text(it, Modifier.padding(horizontal = 14.dp, vertical = 4.dp), style = MaterialTheme.typography.bodySmall) }

        LazyColumn(
            Modifier.weight(1f).fillMaxWidth().background(MaterialTheme.colorScheme.background),
            state = list,
            reverseLayout = true,
            contentPadding = PaddingValues(horizontal = 10.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(3.dp, Alignment.Bottom),
        ) {
            items(visibleQueue.asReversed(), key = PendingMessage::id) { message ->
                PendingBubble(chat, state, message)
            }
            itemsIndexed(reversed, key = { _, message -> message.sequence }) { index, message ->
                val older = reversed.getOrNull(index + 1)
                MessageBubble(
                    chat, state, room, message, older,
                    replyText = bySequence[message.replyTo]?.text?.ifBlank { null },
                    highlighted = message.sequence == state.focus,
                    menuOpen = menu == message.sequence,
                    onReply = { replyTo = message.sequence },
                    onMenu = { menu = message.sequence; operationResult = null },
                    onDismissMenu = { menu = null },
                    onOperation = { operation ->
                        menu = null
                        if (operation == MessageOperation.Pin) chat.pinMessage(room, message.sequence)
                        else messageOperation = message.sequence to operation
                    },
                    onPreview = { preview = message.sequence },
                    onCard = { botDetails = message.sequence },
                    discussionSummary = discussionSummaries[message.sequence],
                    onComments = if (room.type == "channel" || message.discussionRoot) ({ commentPost = message.sequence }) else null,
                )
            }
            if ((state.messages.firstOrNull()?.sequence ?: 0) > 1) item {
                TextButton(onClick = chat::older, modifier = Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.conv_load_earlier))
                }
            } else if (state.messages.isEmpty()) item {
                EmptyState("✦", stringResource(R.string.conv_empty))
            }
        }

        if (room.canPost && replyTo != null) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    bySequence[replyTo]?.text?.ifBlank { null } ?: stringResource(R.string.conv_replying_to, (replyTo ?: 0).toInt()),
                    Modifier.weight(1f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    style = MaterialTheme.typography.bodySmall,
                )
                TextButton(onClick = { replyTo = null }) { Text(stringResource(R.string.conv_cancel_reply)) }
            }
        }
        if (room.canPost && file != null) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(file?.name.orEmpty(), Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                TextButton(onClick = { file = null }, enabled = !state.busy) { Text(stringResource(R.string.conv_remove_attachment)) }
            }
        }

        if (room.canPost) {
            Surface(color = MaterialTheme.colorScheme.surface, shadowElevation = 1.dp) {
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.Bottom,
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    IconButton(onClick = { picker.launch(arrayOf("*/*")) }, enabled = !state.busy) {
                        Text("＋", style = MaterialTheme.typography.titleLarge, color = MaterialTheme.colorScheme.primary)
                    }
                    BasicTextField(
                        value = draft,
                        onValueChange = { if (it.length <= 4000) draft = it },
                        modifier = Modifier.weight(1f).heightIn(min = 48.dp).padding(vertical = 12.dp),
                        textStyle = MaterialTheme.typography.bodyLarge.copy(color = MaterialTheme.colorScheme.onSurface),
                        cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
                        maxLines = 4,
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Default),
                        decorationBox = { inner ->
                            Box {
                                if (draft.isEmpty()) Text(stringResource(R.string.conv_message_hint), color = MaterialTheme.colorScheme.onSurfaceVariant)
                                inner()
                            }
                        },
                    )
                    val outgoing = draft
                    val quoted = replyTo
                    val outgoingFile = file
                    IconButton(
                        onClick = {
                            chat.send(room.id, outgoing, quoted, outgoingFile) {
                                if (draft == outgoing) draft = ""
                                if (replyTo == quoted) replyTo = null
                                if (file == outgoingFile) file = null
                            }
                        },
                        enabled = (draft.isNotBlank() || file != null) && !state.busy,
                        modifier = Modifier.size(48.dp),
                    ) {
                        Text("➤", style = MaterialTheme.typography.titleLarge)
                    }
                }
            }
        } else {
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    stringResource(if (room.type == "channel") R.string.conv_channel_read_only else R.string.conv_group_silenced),
                    Modifier.weight(1f),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                if (room.type == "channel") TextButton(onClick = { channelSettings = true }) {
                    Text(stringResource(R.string.channel_discussion_entry))
                }
            }
        }
    }

    if (channelSettings) ChannelAdministration(chat, state, room) { channelSettings = false }
    bySequence[commentPost]?.let { ChannelComments(chat, state, room, it) { commentPost = null } }
    bySequence[preview]?.takeUnless(ChatMessage::deleted)?.let { message ->
        AttachmentDialog(chat, room.id, message) { preview = null }
    }
    messageOperation?.let { (sequence, operation) ->
        bySequence[sequence]?.let { message ->
            MessageOperationDialog(chat, state, room, message, operation, dismiss = { messageOperation = null }) { result ->
                messageOperation = null
                operationResult = result
            }
        }
    }
    bySequence[botDetails]?.takeUnless(ChatMessage::deleted)?.card?.let { card ->
        BotDetails(chat.bot, card) { botDetails = null }
    }
    robot?.providers?.takeIf { it.isNotEmpty() }?.let { providers ->
        if (asking) BotRequestDialog(chat.bot, room.id, providers, null) { asking = false }
        bySequence[scheduleFrom]?.takeUnless(ChatMessage::deleted)?.let { message ->
            BotRequestDialog(chat.bot, room.id, providers, message) { scheduleFrom = null }
        }
    }
}

@Composable
private fun PendingBubble(chat: ChatRepository, state: ChatState, message: PendingMessage) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
        Column(
            Modifier.widthIn(max = 300.dp)
                .clip(RoundedCornerShape(16.dp, 16.dp, 4.dp, 16.dp))
                .background(MaterialTheme.colorScheme.primaryContainer)
                .padding(horizontal = 12.dp, vertical = 8.dp),
        ) {
            if (message.text.isNotBlank()) Text(message.text, style = MaterialTheme.typography.bodyLarge)
            message.fileName?.let { Text(it, style = MaterialTheme.typography.labelMedium) }
            val uploading = state.upload?.takeIf { it.id == message.id }
            if (uploading != null) {
                Text(stringResource(R.string.conv_uploading, uploading.percent), style = MaterialTheme.typography.labelMedium)
                LinearProgressIndicator(progress = { uploading.percent / 100f }, modifier = Modifier.width(160.dp))
            } else if (message.failure == null) {
                Text(stringResource(R.string.conv_pending), style = MaterialTheme.typography.labelMedium)
            } else {
                Text(stringResource(R.string.conv_failed, message.failure.toString()), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.labelMedium)
                Row {
                    TextButton({ chat.retry(message) }, enabled = !state.busy) { Text(stringResource(R.string.action_retry)) }
                    TextButton({ chat.discard(message) }, enabled = !state.busy) { Text(stringResource(R.string.action_remove)) }
                }
            }
        }
    }
}

@Composable
private fun MessageBubble(
    chat: ChatRepository,
    state: ChatState,
    room: Room,
    message: ChatMessage,
    older: ChatMessage?,
    replyText: String?,
    highlighted: Boolean,
    menuOpen: Boolean,
    onReply: () -> Unit,
    onMenu: () -> Unit,
    onDismissMenu: () -> Unit,
    onOperation: (MessageOperation) -> Unit,
    onPreview: () -> Unit,
    onCard: () -> Unit,
    discussionSummary: ChannelDiscussionSummary?,
    onComments: (() -> Unit)?,
) {
    val context = LocalContext.current
    val actionsLabel = stringResource(R.string.status_message_actions)
    val broadcast = room.type == "channel"
    val own = !broadcast && message.sender == state.username
    val sender = if (broadcast) room.title else message.sender
    val dayLabel = messageDay(message.sentAt)
    val olderDayLabel = older?.let { messageDay(it.sentAt) }
    val continued = dayLabel.isNotBlank() && older != null &&
        (broadcast || older.sender == message.sender) && olderDayLabel == dayLabel
    val newDay = dayLabel.isNotBlank() && olderDayLabel != dayLabel
    val dark = MaterialTheme.colorScheme.background.luminance() < .5f
    val shape = when {
        own && continued -> RoundedCornerShape(16.dp, 6.dp, 6.dp, 16.dp)
        own -> RoundedCornerShape(16.dp, 16.dp, 4.dp, 16.dp)
        continued -> RoundedCornerShape(6.dp, 16.dp, 16.dp, 6.dp)
        else -> RoundedCornerShape(16.dp, 16.dp, 16.dp, 4.dp)
    }
    val bubbleColor = when {
        highlighted -> MaterialTheme.colorScheme.primaryContainer
        own && dark -> Color(0xff24506a)
        own -> Color(0xffe2f4d7)
        else -> MaterialTheme.colorScheme.surface
    }

    Column(Modifier.fillMaxWidth()) {
        if (newDay) {
            Box(Modifier.fillMaxWidth().padding(vertical = 8.dp), contentAlignment = Alignment.Center) {
                Surface(color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = .78f), shape = CircleShape) {
                    Text(
                        dayLabel,
                        color = MaterialTheme.colorScheme.surface,
                        style = MaterialTheme.typography.labelMedium,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 3.dp),
                    )
                }
            }
        }
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = if (own) Arrangement.End else Arrangement.Start,
            verticalAlignment = Alignment.Bottom,
        ) {
            if (!own) {
                if (!continued) RoomAvatar(sender, if (broadcast) "channel" else "direct", 28.dp) else Spacer(Modifier.width(28.dp))
                Spacer(Modifier.width(7.dp))
            }
            Column(
                Modifier.widthIn(max = 316.dp)
                    .clip(shape)
                    .background(bubbleColor)
                    .semantics {
                        if (!message.deleted) customActions = listOf(CustomAccessibilityAction(actionsLabel) { onMenu(); true })
                    }
                    .combinedClickable(
                        enabled = !message.deleted,
                        onClick = {},
                        onLongClickLabel = stringResource(R.string.status_message_actions),
                        onLongClick = onMenu,
                    )
                    .padding(horizontal = 10.dp, vertical = 6.dp),
            ) {
                if (!own && !continued) {
                    Text(sender, color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelMedium)
                }
                message.forwardedFrom?.let {
                    Text(
                        stringResource(R.string.conv_forwarded_from, it),
                        color = MaterialTheme.colorScheme.primary,
                        style = MaterialTheme.typography.labelSmall,
                    )
                }
                message.replyTo?.let { sequence ->
                    Text(
                        replyText ?: stringResource(R.string.conv_quoted, sequence.toInt()),
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        color = MaterialTheme.colorScheme.primary,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(bottom = 3.dp),
                    )
                }
                if (message.deleted) {
                    Text(stringResource(R.string.conv_deleted), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
                    if (message.discussionRoot && onComments != null) TextButton(onClick = onComments) {
                        Text(stringResource(R.string.channel_comments))
                    }
                } else {
                    if (message.text.isNotBlank()) Text(message.text, style = MaterialTheme.typography.bodyLarge)
                    message.attachment?.let { attachment ->
                        Surface(
                            onClick = onPreview,
                            color = Color.Transparent,
                            shape = RoundedCornerShape(10.dp),
                            modifier = Modifier.widthIn(max = 260.dp),
                        ) {
                            Row(Modifier.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                Box(Modifier.size(42.dp).background(MaterialTheme.colorScheme.primary.copy(alpha = .12f), CircleShape), contentAlignment = Alignment.Center) {
                                    Text("↓", color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.titleLarge)
                                }
                                Column(Modifier.weight(1f, fill = false)) {
                                    Text(attachment.name, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.primary)
                                    Text(Formatter.formatShortFileSize(context, attachment.size), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                }
                            }
                        }
                    }
                    message.card?.let {
                        TextButton(onClick = onCard) { Text(cardTitle(it.kind)) }
                    }
                    if (message.forwardedFrom == null && message.text.isBlank() && message.attachment == null && message.card == null) {
                        Text(" ", style = MaterialTheme.typography.bodyLarge)
                    }
                    if (onComments != null) TextButton(onClick = onComments) {
                        Text(stringResource(R.string.channel_comments))
                        if (discussionSummary != null) {
                            Text(" · " + discussionSummary.count)
                            if (discussionSummary.unread > 0) Text(" · " + stringResource(
                                R.string.channel_comments_unread, discussionSummary.unread))
                        }
                    }
                    Row(
                        modifier = Modifier.align(Alignment.End).padding(top = 2.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                    ) {
                        if (broadcast && room.signPosts) Text(message.sender,
                            color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
                        if (message.edited) Text(stringResource(R.string.conv_edited), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
                        messageTime(message.sentAt).takeIf(String::isNotEmpty)?.let {
                            Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
                        }
                    }
                    if (menuOpen) {
                        MessageMenu(
                            canEdit = message.card == null && if (room.type == "channel") room.channelRights and 2 != 0 else own,
                            canDelete = message.card == null && if (room.type == "channel") room.channelRights and 4 != 0 else own,
                            canPin = if (room.type == "channel") room.channelRights and 8 != 0 else room.canPost,
                            pinned = room.pinnedMessage == message.sequence,
                            enabled = state.connected && !state.busy,
                            dismiss = onDismissMenu,
                            reply = if (room.canPost) ({ onDismissMenu(); onReply() }) else null,
                        ) { operation -> onOperation(operation) }
                    }
                }
            }
        }
    }
}

private fun Color.luminance(): Float = .2126f * red + .7152f * green + .0722f * blue
