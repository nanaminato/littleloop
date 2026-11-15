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
        intent?.getStringExtra("room")?.let { chat.select(it) }
    }
}

@Composable
private fun ChatScreen(chat: ChatRepository, state: ChatState, theme: String) {
    val context = LocalContext.current
    val service = remember { Intent(context, ChatNotificationService::class.java) }
    val listening by chat.backgroundEnabled.collectAsState()
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        if (it) context.startForegroundService(service)
    }
    var settings by rememberSaveable { mutableStateOf(false) }
    var create by rememberSaveable { mutableStateOf(false) }
    var query by rememberSaveable { mutableStateOf("") }
    val room = state.rooms.firstOrNull { it.id == state.selected }
    BackHandler(settings || room != null) {
        if (settings) settings = false else chat.select(null)
    }
    Surface(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize().safeDrawingPadding().imePadding()) {
            when {
                state.resuming -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                state.username.isEmpty() -> LoginScreen(chat, state)
                settings -> {
                    Header("设置", "返回", { settings = false })
                    SettingsScreen(chat, state, theme, listening, { enabled ->
                        if (!enabled) context.stopService(service)
                        else if (Build.VERSION.SDK_INT >= 33 &&
                            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
                            permission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        else context.startForegroundService(service)
                    }, {
                        context.stopService(service)
                        settings = false
                        chat.logout()
                    })
                }
                room != null -> Conversation(chat, state, room)
                else -> {
                    Header("LittleLoop", "设置", { settings = true }, "新会话", { create = true })
                    OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth().padding(12.dp),
                        singleLine = true, placeholder = { Text("搜索会话") }, shape = RoundedCornerShape(24.dp))
                    if (!state.connected) Text("正在重新连接", Modifier.padding(horizontal = 16.dp))
                    state.error?.let { Text(it, Modifier.padding(12.dp), color = MaterialTheme.colorScheme.error) }
                    LazyColumn(Modifier.weight(1f)) {
                        items(state.rooms.filter { !it.archived && it.title.contains(query, ignoreCase = true) }, key = Room::id) {
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
}

@Composable
private fun Header(title: String, action: String, onAction: () -> Unit, end: String? = null, onEnd: () -> Unit = {}) {
    Row(Modifier.fillMaxWidth().background(Color(0xff2879ad)).padding(horizontal = 6.dp).heightIn(min = 56.dp),
        verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = onAction) { Text(action, color = Color.White) }
        Text(title, Modifier.weight(1f), style = MaterialTheme.typography.titleLarge, color = Color.White,
            maxLines = 1, overflow = TextOverflow.Ellipsis)
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
            Text(room.preview.ifBlank { if (room.type == "direct") "私聊" else "群聊" }, maxLines = 1, overflow = TextOverflow.Ellipsis,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (room.unread > 0) Badge { Text(room.unread.toString()) }
    }
    HorizontalDivider(Modifier.padding(start = 74.dp))
}

@Composable
private fun ColumnScope.Conversation(chat: ChatRepository, state: ChatState, room: Room) {
    var draft by rememberSaveable(room.id) { mutableStateOf("") }
    val list = rememberLazyListState()
    LaunchedEffect(state.messages.lastOrNull()?.sequence) {
        if (list.firstVisibleItemIndex <= 1) list.requestScrollToItem(0)
    }
    val dark = MaterialTheme.colorScheme.surface.red < 0.5f
    Header(room.title, "返回", { chat.select(null) }, if (room.muted) "取消静音" else "静音", { chat.roomPreference(room) })
    state.error?.let { Text(it, Modifier.padding(8.dp), color = MaterialTheme.colorScheme.error) }
    LazyColumn(Modifier.fillMaxWidth().weight(1f).background(if (dark) Color(0xff172330) else Color(0xffe2edda)),
        state = list, reverseLayout = true, contentPadding = PaddingValues(10.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp, Alignment.Bottom)) {
        items(state.messages.asReversed(), key = ChatMessage::sequence) { message ->
            val own = message.sender == state.username
            Row(Modifier.fillMaxWidth(), horizontalArrangement = if (own) Arrangement.End else Arrangement.Start) {
                Column(Modifier.widthIn(max = 300.dp).background(
                    if (own && dark) Color(0xff215775) else if (own) Color(0xffe2f9cf) else MaterialTheme.colorScheme.surface,
                    RoundedCornerShape(14.dp)).padding(horizontal = 11.dp, vertical = 7.dp)) {
                    if (!own) Text(message.sender, color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelMedium)
                    Text(if (message.deleted) "消息已删除" else message.text.ifBlank { "附件或消息卡片" })
                    val time = remember(message.sentAt) {
                        runCatching { messageTime.format(Instant.parse(message.sentAt).atZone(ZoneId.systemDefault())) }.getOrDefault("")
                    }
                    if (time.isNotEmpty()) Text(time, Modifier.align(Alignment.End), style = MaterialTheme.typography.labelSmall)
                }
            }
        }
        if ((state.messages.firstOrNull()?.sequence ?: 0) > 1) item {
            TextButton(onClick = chat::older, modifier = Modifier.fillMaxWidth()) { Text("加载更早消息") }
        }
    }
    if (room.canPost) Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
        OutlinedTextField(draft, { draft = it }, Modifier.weight(1f), placeholder = { Text("消息") },
            shape = RoundedCornerShape(24.dp), maxLines = 4)
        val outgoing = draft
        TextButton(onClick = { chat.send(outgoing) { if (draft == outgoing) draft = "" } },
            enabled = draft.isNotBlank() && !state.busy) { Text("发送") }
    }
}

@Composable
private fun SettingsScreen(chat: ChatRepository, state: ChatState, theme: String, listening: Boolean,
                           background: (Boolean) -> Unit, logout: () -> Unit) {
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
        SettingToggle("显示消息预览", notifications?.optBoolean("preview", true) ?: true, !state.busy) { chat.save("notifications", "preview", it) }
        SettingToggle("后台通知（本机）", listening, true, background)
        Text("后台接收期间，Android 会显示一条常驻连接通知。", style = MaterialTheme.typography.bodySmall)
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
