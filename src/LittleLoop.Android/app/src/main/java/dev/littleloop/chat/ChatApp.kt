package dev.littleloop.chat

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

@Composable
internal fun ChatApp(
    chat: ChatRepository,
    state: ChatState,
    theme: String,
    onLanguage: (String) -> Unit,
) {
    var settings by rememberSaveable { mutableStateOf(false) }
    var create by rememberSaveable { mutableStateOf(false) }
    var searching by rememberSaveable { mutableStateOf(false) }
    var searchRoom by rememberSaveable { mutableStateOf<String?>(null) }
    var showChannels by rememberSaveable { mutableStateOf(false) }
    var showFriends by rememberSaveable { mutableStateOf(false) }
    var showMembers by rememberSaveable { mutableStateOf(false) }
    var showTopics by rememberSaveable { mutableStateOf(false) }
    var createSpace by rememberSaveable { mutableStateOf(false) }
    var navigationAccount by rememberSaveable { mutableStateOf(state.username) }

    LaunchedEffect(state.username) {
        if (navigationAccount != state.username) {
            settings = false
            create = false
            searching = false
            searchRoom = null
            showChannels = false
            showTopics = false
            showFriends = false
            showMembers = false
            createSpace = false
            navigationAccount = state.username
        }
    }

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

    Box(Modifier.fillMaxSize().safeDrawingPadding().imePadding()) {
        when {
            state.resuming -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            state.username.isEmpty() -> LoginScreen(chat, state)
            searching -> ChatScreenScaffold(
                title = stringResource(if (searchRoom == null) R.string.search_all else R.string.search_in),
                back = { searching = false },
            ) { MessageSearch(chat, state.rooms, searchRoom) { searching = false } }
            settings -> ChatScreenScaffold(title = stringResource(R.string.settings_title), back = { settings = false }) {
                SettingsScreen(chat, state, theme, onLanguage)
            }
            showFriends -> ChatScreenScaffold(title = stringResource(R.string.friends_title), back = { showFriends = false }) {
                FriendsPanel(chat, state) { showFriends = false }
            }
            showMembers && room?.type == "group" -> ChatScreenScaffold(
                title = stringResource(R.string.conv_members),
                subtitle = room.title,
                back = { showMembers = false },
            ) { GroupMembersPanel(chat, state, room) }
            showChannels -> ChatScreenScaffold(
                title = stringResource(R.string.channels_title),
                back = { showChannels = false },
                actions = { TextButton(onClick = { createSpace = true }) { Text(stringResource(R.string.channels_new)) } },
            ) {
                ChannelDirectory(state, subscribe = chat::subscribe) {
                    chat.select(it)
                    showChannels = false
                }
            }
            showTopics && room?.type == "group" -> ChatScreenScaffold(
                title = stringResource(R.string.conv_topics),
                subtitle = room.title,
                back = { showTopics = false },
                actions = { TextButton(onClick = { createSpace = true }) { Text(stringResource(R.string.topics_new)) } },
            ) {
                val topics = state.rooms.filter { it.parentId == room.id && it.type == "topic" && !it.archived }
                if (topics.isEmpty()) EmptyState("·", stringResource(R.string.topics_empty))
                LazyColumn(Modifier.weight(1f)) {
                    items(topics, key = Room::id) { topic ->
                        RoomRow(topic) { chat.select(topic.id); showTopics = false }
                    }
                }
            }
            room != null -> ConversationScreen(
                chat, state, room, openBack,
                onTopics = { showTopics = true },
                onMembers = { showMembers = true },
                onSearch = { searchRoom = room.id; searching = true },
            )
            else -> ChatHomeScreen(
                chat, state,
                onSearch = { searchRoom = null; searching = true },
                onFriends = { showFriends = true },
                onChannels = { showChannels = true },
                onSettings = { settings = true },
                onNewChat = { create = true },
            )
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
