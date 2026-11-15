package dev.littleloop.chat

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

private val messageTime = DateTimeFormatter.ofPattern("HH:mm")

class MainActivity : ComponentActivity() {
    private val chat get() = (application as ChatApplication).chat
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            val state by chat.state.collectAsState()
            val theme by chat.theme.collectAsState()
            val dark = theme == "dark" || theme == "system" && isSystemInDarkTheme()
            SideEffect {
                WindowCompat.getInsetsController(window, window.decorView).isAppearanceLightStatusBars = !dark
            }
            MaterialTheme(colorScheme = if (dark) darkColorScheme(primary = Color(0xff83ccff))
                else lightColorScheme(primary = Color(0xff237bb5))) {
                ChatScreen(chat, state, theme)
            }
        }
        openNotification(intent)
    }

    override fun onResume() { super.onResume(); chat.foreground(true) }
    override fun onPause() { chat.foreground(false); super.onPause() }
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        openNotification(intent)
    }
    private fun openNotification(intent: Intent?) {
        val supplied = intent?.getStringExtra("room") ?: return
        val room = runCatching { UUID.fromString(supplied).toString() }.getOrNull() ?: return
        val scope = intent.getStringExtra("pushScope")
        if (scope != null) chat.openNotification(room, intent.getLongExtra("sequence", 0), intent.getStringExtra("work"), scope)
        else chat.select(room)
        intent.removeExtra("room")
    }
}

@Composable
private fun ChatScreen(chat: ChatRepository, state: ChatState, theme: String) {
    val context = LocalContext.current
    val service = remember { Intent(context, ChatNotificationService::class.java) }
    val listening by chat.backgroundEnabled.collectAsState()
    val cloudEnabled by chat.push.enabled.collectAsState()
    val uiScope = rememberCoroutineScope()
    var cloudBusy by remember { mutableStateOf(false) }
    var cloudError by remember { mutableStateOf<String?>(null) }
    val setCloud: (Boolean) -> Unit = { value ->
        if (!cloudBusy) {
            cloudBusy = true
            cloudError = null
            uiScope.launch {
                try {
                    chat.push.setEnabled(value)
                    if (value && chat.push.enabled.value) context.stopService(service)
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (error: ChatRequestException) {
                    cloudError = if (error.status == 404 || error.status == 400) "服务器未配置匹配的 Firebase 项目"
                        else "云推送设置失败，请重试"
                } catch (_: Exception) {
                    cloudError = "无法注册云推送，请检查网络和 Google Play 服务"
                } finally { cloudBusy = false }
            }
        }
    }
    val cloudPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        if (it) setCloud(true) else cloudError = "请在 Android 设置中允许通知"
    }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        if (it) context.startForegroundService(service)
    }
    var settings by rememberSaveable { mutableStateOf(false) }
    var create by rememberSaveable { mutableStateOf(false) }
    var query by rememberSaveable { mutableStateOf("") }
    var searching by rememberSaveable { mutableStateOf(false) }
    var searchRoom by rememberSaveable { mutableStateOf<String?>(null) }
    var showChannels by rememberSaveable { mutableStateOf(false) }
    var showFriends by rememberSaveable { mutableStateOf(false) }
    var showMembers by rememberSaveable { mutableStateOf(false) }
    var showTopics by rememberSaveable { mutableStateOf(false) }
    var createSpace by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(state.notificationTarget) {
        state.notificationTarget?.let { id ->
            searching = false
            settings = false
            create = false
            showChannels = false
            showTopics = false
            showFriends = false
            showMembers = false
            createSpace = false
            chat.notificationLinkOpened(id)
        }
    }
    val room = state.rooms.firstOrNull { it.id == state.selected }
    val parent = state.rooms.firstOrNull { it.id == room?.parentId }
    val openBack = { chat.select(parent?.id) }
    val browsing = showChannels && state.username.isNotEmpty()
    DisposableEffect(chat, browsing) {
        chat.browseChannels(browsing)
        onDispose { chat.browseChannels(false) }
    }
    val memberRoom = room?.id.takeIf { showMembers && room?.type == "group" }
    LaunchedEffect(showFriends, memberRoom, state.username) {
        chat.browseSocial(showFriends && state.username.isNotEmpty(), memberRoom)
        if (memberRoom == null) showMembers = false
    }
    LaunchedEffect(room?.id, showChannels, showTopics, settings, searching, showFriends, showMembers) {
        chat.showingTimeline(room != null && !showChannels && !showTopics && !settings && !searching && !showFriends && !showMembers)
    }
    BackHandler(settings || searching || showChannels || showTopics || showFriends || showMembers || room != null) {
        when {
            searching -> searching = false
            settings -> settings = false
            showFriends -> showFriends = false
            showMembers -> showMembers = false
            showChannels -> showChannels = false
            showTopics -> showTopics = false
            else -> openBack()
        }
    }
    Surface(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize().safeDrawingPadding().imePadding()) {
            when {
                state.resuming -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                state.username.isEmpty() -> LoginScreen(chat, state)
                searching -> {
                    Header(if (searchRoom == null) "搜索消息" else "会话内搜索", "返回", { searching = false })
                    MessageSearch(chat, state.rooms, searchRoom) { searching = false }
                }
                settings -> {
                    Header("设置", "返回", { settings = false })
                    SettingsScreen(chat, state, theme, listening, cloudEnabled, { enabled ->
                        if (!enabled) context.stopService(service)
                        else if (Build.VERSION.SDK_INT >= 33 &&
                            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
                            permission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        else context.startForegroundService(service)
                    }, cloud = {
                        if (chat.push.configured) {
                            SettingToggle("后台通知（云推送）", cloudEnabled, !cloudBusy) { value ->
                                if (value && Build.VERSION.SDK_INT >= 33 &&
                                    context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
                                    cloudPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                                else setCloud(value)
                            }
                        } else Text("此安装包尚未配置 Firebase 云推送", style = MaterialTheme.typography.bodySmall)
                        cloudError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                    }, logout = {
                        context.stopService(service)
                        settings = false
                        chat.logout()
                    })
                }
                showFriends -> {
                    Header("好友", "返回", { showFriends = false })
                    FriendsPanel(chat, state) { showFriends = false }
                }
                showMembers && room?.type == "group" -> {
                    Header("群成员", "返回", { showMembers = false })
                    GroupMembersPanel(chat, state, room)
                }
                showChannels -> {
                    Header("频道目录", "返回", { showChannels = false }, "新建频道") { createSpace = true }
                    ChannelDirectory(state, subscribe = chat::subscribe) {
                        chat.select(it)
                        showChannels = false
                    }
                }
                showTopics && room?.type == "group" -> {
                    Header("群话题", "返回", { showTopics = false }, "新建话题") { createSpace = true }
                    val topics = state.rooms.filter { it.parentId == room.id && it.type == "topic" && !it.archived }
                    Text(room.title, Modifier.padding(12.dp), style = MaterialTheme.typography.titleMedium)
                    if (topics.isEmpty()) Text("暂无话题", Modifier.padding(12.dp))
                    LazyColumn(Modifier.weight(1f)) {
                        items(topics, key = Room::id) { topic ->
                            RoomRow(topic) { chat.select(topic.id); showTopics = false }
                        }
                    }
                }
                room != null -> key(room.id) {
                    Conversation(chat, state, room, openBack, { showTopics = true }, { showMembers = true }) { searchRoom = room.id; searching = true }
                }
                else -> {
                    Header("LittleLoop", "设置", { settings = true }, "新会话", extra = {
                        TextButton(onClick = chat::openSaved, enabled = !state.busy) { Text("收藏", color = Color.White) }
                    }) { create = true }
                    Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically) {
                        OutlinedTextField(query, { query = it }, Modifier.weight(1f),
                            singleLine = true, placeholder = { Text("搜索会话") }, shape = RoundedCornerShape(24.dp))
                        TextButton(onClick = { searchRoom = null; searching = true }) { Text("搜索消息") }
                        TextButton(onClick = { showFriends = true }) { Text("好友") }
                        TextButton(onClick = { showChannels = true }) { Text("频道") }
                    }
                    if (!state.connected) Text("正在重新连接", Modifier.padding(horizontal = 16.dp))
                    state.error?.let { Text(it, Modifier.padding(12.dp), color = MaterialTheme.colorScheme.error) }
                    LazyColumn(Modifier.weight(1f)) {
                        items(state.rooms.filter {
                            !it.archived && (it.type != "topic" || query.isNotBlank()) && it.title.contains(query, ignoreCase = true)
                        }, key = Room::id) {
                            RoomRow(it) { chat.select(it.id) }
                        }
                    }
                }
            }
        }
    }
    if (create && state.username.isNotEmpty()) CreateRoom(state.busy, state.error, { create = false }) { type, title, members ->
        chat.create(type, title, members) { create = false }
    }
    if (createSpace && (showChannels || showTopics && room?.type == "group")) {
        val parentId = room?.id.takeIf { showTopics && !showChannels }
        CreateChatSpace(parentId != null, state, { createSpace = false }) { title ->
            chat.createSpace(parentId, title) { createSpace = false; showChannels = false; showTopics = false }
        }
    }
}

@Composable
private fun Header(title: String, action: String, onAction: () -> Unit, end: String? = null,
                   extra: (@Composable () -> Unit)? = null, onEnd: () -> Unit = {}) {
    Row(Modifier.fillMaxWidth().background(Color(0xff2879ad)).padding(horizontal = 6.dp).heightIn(min = 56.dp),
        verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = onAction) { Text(action, color = Color.White) }
        Text(title, Modifier.weight(1f), style = MaterialTheme.typography.titleLarge, color = Color.White,
            maxLines = 1, overflow = TextOverflow.Ellipsis)
        extra?.invoke()
        if (end != null) TextButton(onClick = onEnd) { Text(end, color = Color.White) }
    }
}

@Composable
private fun LoginScreen(chat: ChatRepository, state: ChatState) {
    var origin by rememberSaveable { mutableStateOf(chat.origin.ifBlank { if (BuildConfig.DEBUG) "http://127.0.0.1:5190" else "" }) }
    var user by rememberSaveable { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically)) {
        Text("LittleLoop", style = MaterialTheme.typography.headlineLarge, color = MaterialTheme.colorScheme.primary)
        Text("登录你的聊天账户")
        OutlinedTextField(origin, { origin = it }, Modifier.fillMaxWidth(), label = { Text("服务器地址") },
            singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
        OutlinedTextField(user, { user = it }, Modifier.fillMaxWidth(), label = { Text("账号") }, singleLine = true)
        OutlinedTextField(password, { password = it }, Modifier.fillMaxWidth(), label = { Text("密码") }, singleLine = true,
            visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
        state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        Button(onClick = { chat.login(origin, user, password); password = "" }, Modifier.fillMaxWidth(),
            enabled = user.isNotBlank() && password.isNotEmpty() && !state.busy) { Text("登录") }
    }
}

@Composable
private fun RoomRow(room: Room, open: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = open).padding(horizontal = 14.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Box(Modifier.size(48.dp).background(if (room.type == "direct") Color(0xff3a93c4) else Color(0xff639b74), CircleShape),
            contentAlignment = Alignment.Center) { Text(room.title.take(1), color = Color.White, style = MaterialTheme.typography.titleLarge) }
        Column(Modifier.weight(1f)) {
            Text(room.title, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(room.preview.ifBlank { roomTypeLabel(room.type) }, maxLines = 1, overflow = TextOverflow.Ellipsis,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (room.unread > 0) Badge { Text(room.unread.toString()) }
    }
    HorizontalDivider(Modifier.padding(start = 74.dp))
}

@Composable
private fun ColumnScope.Conversation(chat: ChatRepository, state: ChatState, room: Room, onBack: () -> Unit,
                                     onTopics: () -> Unit, onMembers: () -> Unit, onSearch: () -> Unit) {
    var draft by rememberSaveable(room.id) { mutableStateOf("") }
    var replyTo by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    var file by remember(room.id) { mutableStateOf<SelectedAttachment?>(null) }
    var preview by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    var fileError by remember(room.id) { mutableStateOf<String?>(null) }
    var robot by remember(room.id) { mutableStateOf<BotRoom?>(null) }
    var botError by remember(room.id) { mutableStateOf<String?>(null) }
    var installing by remember(room.id) { mutableStateOf(false) }
    var asking by rememberSaveable(room.id) { mutableStateOf(false) }
    var scheduleFrom by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    var botDetails by rememberSaveable(room.id) { mutableStateOf<Long?>(null) }
    LaunchedEffect(state.openWork, state.focus) {
        if (state.openWork != null && state.focus != null) {
            botDetails = state.focus
            chat.workLinkOpened()
        }
    }
    val pickerScope = rememberCoroutineScope()
    LaunchedEffect(room.id, state.connected) {
        if (state.connected) {
            val result = botAttempt { chat.bot.room(room.id) }
            if (result is BotResult.Success) robot = result.value
        }
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) pickerScope.launch {
            runCatching { chat.selectedAttachment(uri) }.onSuccess {
                file = it
                fileError = null
            }.onFailure { fileError = "无法读取所选文件" }
        }
    }
    val bySequence = remember(state.messages) { state.messages.associateBy(ChatMessage::sequence) }
    var menu by remember(room.id) { mutableStateOf<Long?>(null) }
    var messageOperation by remember(room.id) { mutableStateOf<Pair<Long, MessageOperation>?>(null) }
    var operationResult by remember(room.id) { mutableStateOf<String?>(null) }
    val confirmedIds = remember(state.messages, state.username) {
        state.messages.filter { it.sender == state.username }.mapTo(hashSetOf(), ChatMessage::clientId)
    }
    val queued = state.outbox.filter { it.room == room.id && it.id !in confirmedIds }
    val list = rememberLazyListState()
    val visibleQueue = if (state.focus == null) queued else emptyList()
    LaunchedEffect(state.focus, state.notificationTarget) {
        val index = state.messages.asReversed().indexOfFirst { it.sequence == state.focus }
        if (index >= 0) list.scrollToItem(index)
    }
    LaunchedEffect(state.messages.lastOrNull()?.sequence, visibleQueue.lastOrNull()?.id) {
        if (state.focus == null && list.firstVisibleItemIndex <= 1) list.requestScrollToItem(0)
    }
    val dark = MaterialTheme.colorScheme.surface.red < 0.5f
    Header(room.title, "返回", onBack, if (room.muted) "取消静音" else "静音", extra = {
        TextButton(onClick = onSearch) { Text("搜索", color = Color.White) }
    }) { chat.roomPreference(room) }
    if (room.type == "group") {
        val unread = state.rooms.filter { it.type == "topic" && it.parentId == room.id }.sumOf(Room::unread)
        Row {
            TextButton(onClick = onTopics) { Text(if (unread == 0L) "群话题" else "群话题 · $unread 条未读") }
            TextButton(onClick = onMembers) { Text("群成员") }
        }
    }
    if (state.connected && robot?.canOperate == true && room.type in setOf("direct", "group", "topic")) {
        if (robot?.installed == true) {
            TextButton(onClick = { asking = true }) { Text("引用源码提问") }
        } else if (room.type != "topic") {
            TextButton(onClick = {
                installing = true
                pickerScope.launch {
                    try {
                        when (val result = botAttempt { chat.bot.install(room.id); chat.bot.room(room.id) }) {
                            is BotResult.Success -> { robot = result.value; botError = null }
                            is BotResult.Error -> botError = result.message
                        }
                    } finally { installing = false }
                }
            }, enabled = !installing) { Text("接入 LittleLoop") }
        }
    }
    if (state.focus != null) Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Text("已定位到历史消息", Modifier.weight(1f))
        TextButton(onClick = { chat.select(room.id) }) { Text("回到最新") }
    }
    room.pinnedMessage?.let { sequence ->
        TextButton(onClick = { chat.openPinned(room) }, modifier = Modifier.fillMaxWidth()) {
            Text("已置顶消息 #$sequence")
        }
    }
    if (!state.connected) Text("离线 · 消息将在连接恢复后发送", Modifier.padding(8.dp))
    state.error?.takeUnless { it == "Offline" }?.let { Text(it, Modifier.padding(8.dp), color = MaterialTheme.colorScheme.error) }
    fileError?.let { Text(it, Modifier.padding(8.dp), color = MaterialTheme.colorScheme.error) }
    botError?.let { Text(it, Modifier.padding(8.dp), color = MaterialTheme.colorScheme.error) }
    operationResult?.let { Text(it, Modifier.padding(horizontal = 12.dp, vertical = 4.dp)) }
    LazyColumn(Modifier.fillMaxWidth().weight(1f).background(if (dark) Color(0xff172330) else Color(0xffe2edda)),
        state = list, reverseLayout = true, contentPadding = PaddingValues(10.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp, Alignment.Bottom)) {
        items(visibleQueue.asReversed(), key = PendingMessage::id) { message ->
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                Column(Modifier.widthIn(max = 300.dp)
                    .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp)).padding(10.dp)) {
                    if (message.text.isNotBlank()) Text(message.text)
                    message.fileName?.let { Text(it, fontWeight = FontWeight.SemiBold) }
                    val uploading = state.upload?.takeIf { it.id == message.id }
                    if (uploading != null) {
                        Text("上传 ${uploading.percent}%")
                        LinearProgressIndicator(progress = { uploading.percent / 100f }, modifier = Modifier.width(160.dp))
                    } else if (message.failure == null) {
                        Text("待发送", style = MaterialTheme.typography.labelMedium)
                    } else {
                        Text("发送失败（${message.failure}）", color = MaterialTheme.colorScheme.error)
                        Row {
                            TextButton({ chat.retry(message) }, enabled = !state.busy) { Text("重试") }
                            TextButton({ chat.discard(message) }, enabled = !state.busy) { Text("移除") }
                        }
                    }
                }
            }
        }
        items(state.messages.asReversed(), key = ChatMessage::sequence) { message ->
            val own = message.sender == state.username
            Row(Modifier.fillMaxWidth(), horizontalArrangement = if (own) Arrangement.End else Arrangement.Start) {
                Column(Modifier.widthIn(max = 300.dp).background(
                    if (message.sequence == state.focus) MaterialTheme.colorScheme.primaryContainer
                    else if (own && dark) Color(0xff215775) else if (own) Color(0xffe2f9cf) else MaterialTheme.colorScheme.surface,
                    RoundedCornerShape(14.dp)).combinedClickable(
                    enabled = !message.deleted, onClick = {}, onLongClickLabel = "消息操作",
                    onLongClick = { menu = message.sequence; operationResult = null },
                ).padding(horizontal = 11.dp, vertical = 7.dp)) {
                    if (!own) Text(message.sender, color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelMedium)
                    message.forwardedFrom?.let { Text("转发自 $it", color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelSmall) }
                    message.replyTo?.let { sequence ->
                        val quoted = bySequence[sequence]?.takeUnless(ChatMessage::deleted)?.text
                        Text(quoted?.ifBlank { null } ?: "引用消息 #$sequence",
                            maxLines = 2, overflow = TextOverflow.Ellipsis, color = MaterialTheme.colorScheme.primary)
                    }
                    if (message.deleted) Text("消息已删除")
                    else {
                        if (message.text.isNotBlank()) Text(message.text)
                        message.attachment?.let { attachment ->
                            TextButton(onClick = { preview = message.sequence }) {
                                Text(attachment.name, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            }
                        }
                        message.card?.let {
                            TextButton(onClick = { botDetails = message.sequence }) { Text(cardTitle(it.kind)) }
                        }
                    }
                    val time = remember(message.sentAt) {
                        runCatching { messageTime.format(Instant.parse(message.sentAt).atZone(ZoneId.systemDefault())) }.getOrDefault("")
                    }
                    if (time.isNotEmpty()) Text(time, Modifier.align(Alignment.End), style = MaterialTheme.typography.labelSmall)
                    if (message.edited && !message.deleted) Text("已编辑", style = MaterialTheme.typography.labelSmall)
                    if (room.canPost && !message.deleted) Row {
                        TextButton(onClick = { replyTo = message.sequence }) { Text("回复") }
                        if (state.connected && robot?.installed == true && robot?.canOperate == true) {
                            TextButton(onClick = { scheduleFrom = message.sequence }) { Text("安排任务") }
                        }
                    }
                    if (menu == message.sequence && !message.deleted) {
                        MessageMenu(own && message.card == null, room.canPost, room.pinnedMessage == message.sequence,
                            enabled = state.connected && !state.busy, dismiss = { menu = null }) { operation ->
                            menu = null
                            if (operation == MessageOperation.Pin) chat.pinMessage(room, message.sequence)
                            else messageOperation = message.sequence to operation
                        }
                    }
                }
            }
        }
        if ((state.messages.firstOrNull()?.sequence ?: 0) > 1) item {
            TextButton(onClick = chat::older, modifier = Modifier.fillMaxWidth()) { Text("加载更早消息") }
        }
    }
    if (room.canPost && replyTo != null) Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Text(bySequence[replyTo]?.text?.ifBlank { null } ?: "回复消息 #$replyTo",
            Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
        TextButton(onClick = { replyTo = null }) { Text("取消回复") }
    }
    if (room.canPost && file != null) Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Text(file?.name.orEmpty(), Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
        TextButton(onClick = { file = null }, enabled = !state.busy) { Text("移除附件") }
    }
    if (room.canPost) Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = { picker.launch(arrayOf("*/*")) }, enabled = !state.busy) { Text("附件") }
        OutlinedTextField(draft, { if (it.length <= 4000) draft = it }, Modifier.weight(1f), placeholder = { Text("消息") },
            shape = RoundedCornerShape(24.dp), maxLines = 4)
        val outgoing = draft
        val quoted = replyTo
        val outgoingFile = file
        TextButton(onClick = {
            chat.send(room.id, outgoing, quoted, outgoingFile) {
                if (draft == outgoing) draft = ""
                if (replyTo == quoted) replyTo = null
                if (file == outgoingFile) file = null
            }
        },
            enabled = (draft.isNotBlank() || file != null) && !state.busy) { Text("发送") }
    }
    if (room.type == "channel" && !room.canPost)
        Text("此频道仅发布者可发消息。", Modifier.padding(horizontal = 16.dp, vertical = 12.dp))
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
private fun SettingsScreen(chat: ChatRepository, state: ChatState, theme: String, listening: Boolean,
                           cloudEnabled: Boolean, background: (Boolean) -> Unit, cloud: @Composable () -> Unit, logout: () -> Unit) {
    val profile = state.settings.optJSONObject("profile")
    val savedName = profile?.optString("displayName").orEmpty()
    val savedBio = profile?.optString("bio").orEmpty()
    var name by rememberSaveable(savedName) { mutableStateOf(savedName) }
    var bio by rememberSaveable(savedBio) { mutableStateOf(savedBio) }
    val notifications = state.settings.optJSONObject("notifications")
    Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("@${state.username}", style = MaterialTheme.typography.headlineSmall)
        Text("账户资料", color = MaterialTheme.colorScheme.primary)
        OutlinedTextField(name, { if (it.length <= 60) name = it }, Modifier.fillMaxWidth(), label = { Text("显示名称") }, singleLine = true)
        OutlinedTextField(bio, { if (it.length <= 240) bio = it }, Modifier.fillMaxWidth(), label = { Text("个人简介") }, maxLines = 3)
        Button(onClick = { chat.profile(name, bio) }, enabled = !state.busy &&
            (name != savedName || bio != savedBio)) { Text("保存资料") }
        HorizontalDivider()
        Text("通知", color = MaterialTheme.colorScheme.primary)
        SettingToggle("私聊通知", notifications?.optBoolean("direct", true) ?: true, !state.busy) { chat.save("notifications", "direct", it) }
        SettingToggle("群聊通知", notifications?.optBoolean("groups", true) ?: true, !state.busy) { chat.save("notifications", "groups", it) }
        SettingToggle("频道通知", notifications?.optBoolean("channels", true) ?: true, !state.busy) { chat.save("notifications", "channels", it) }
        SettingToggle("显示消息预览", notifications?.optBoolean("preview", true) ?: true, !state.busy) { chat.save("notifications", "preview", it) }
        cloud()
        if (!cloudEnabled) {
            SettingToggle("后台通知（本机）", listening, true, background)
            Text("后台接收期间，Android 会显示一条常驻连接通知。", style = MaterialTheme.typography.bodySmall)
        }
        HorizontalDivider()
        Text("外观（本机）", color = MaterialTheme.colorScheme.primary)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            for ((value, label) in listOf("system" to "跟随系统", "light" to "浅色", "dark" to "深色")) {
                FilterChip(selected = theme == value, onClick = { chat.theme(value) }, label = { Text(label) })
            }
        }
        state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        TextButton(onClick = logout, enabled = !state.busy) { Text("退出登录") }
    }
}

@Composable
private fun SettingToggle(label: String, checked: Boolean, enabled: Boolean, changed: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f))
        Switch(checked, changed, enabled = enabled)
    }
}

@Composable
private fun CreateRoom(busy: Boolean, error: String?, cancel: () -> Unit, confirm: (String, String, String) -> Unit) {
    var group by rememberSaveable { mutableStateOf(false) }
    var title by rememberSaveable { mutableStateOf("") }
    var members by rememberSaveable { mutableStateOf("") }
    AlertDialog(onDismissRequest = cancel, title = { Text("新会话") }, text = {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FilterChip(!group, { group = false }, { Text("私聊") })
                FilterChip(group, { group = true }, { Text("群聊") })
            }
            if (group) OutlinedTextField(title, { if (it.length <= 100) title = it }, label = { Text("群名称") }, singleLine = true)
            OutlinedTextField(members, { members = it }, label = { Text(if (group) "成员账号，逗号分隔" else "对方账号") }, singleLine = true)
            error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        }
    }, confirmButton = {
        TextButton(onClick = { confirm(if (group) "group" else "direct", title, members) },
            enabled = !busy && members.isNotBlank() && (!group || title.isNotBlank())) { Text("创建") }
    }, dismissButton = { TextButton(onClick = cancel) { Text("取消") } })
}
