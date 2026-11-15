package dev.littleloop.chat

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

data class ChatFriend(val id: String, val username: String, val displayName: String, val state: String)
data class ChatMember(val username: String, val displayName: String, val robot: Boolean, val owner: Boolean)

@Composable
internal fun FriendsPanel(chat: ChatRepository, state: ChatState, openChat: () -> Unit) {
    var username by rememberSaveable { mutableStateOf("") }
    val enabled = state.connected && !state.busy
    SocialUsername(username, { username = it }, "好友用户名", "添加好友", enabled) {
        chat.requestFriend(username) { username = "" }
    }
    SocialError(state.error)
    LazyColumn {
        if (state.friends.isEmpty()) item { Text("暂无好友或申请", Modifier.padding(16.dp)) }
        items(state.friends, key = { it.id }) { friend ->
            SocialRow(friend.displayName, friend.username) {
                when (friend.state) {
                    "incoming" -> {
                        TextButton(enabled = enabled, onClick = { chat.respondFriend(friend, true) }) { Text("接受") }
                        TextButton(enabled = enabled, onClick = { chat.respondFriend(friend, false) }) { Text("拒绝") }
                    }
                    "outgoing" -> Text("等待对方接受")
                    "friend" -> TextButton(enabled = enabled,
                        onClick = { chat.create("direct", "", friend.username, openChat) }) { Text("发起私聊") }
                }
            }
        }
    }
}

@Composable
internal fun GroupMembersPanel(chat: ChatRepository, state: ChatState, room: Room) {
    var username by rememberSaveable(room.id) { mutableStateOf("") }
    var target by rememberSaveable(room.id) { mutableStateOf<String?>(null) }
    var operation by rememberSaveable(room.id) { mutableStateOf("") }
    val enabled = state.connected && !state.busy
    if (room.canManage) {
        SocialUsername(username, { username = it }, "成员用户名", "邀请成员", enabled) {
            chat.changeMember(room.id, username, "invite") { username = "" }
        }
    }
    SocialError(state.error)
    LazyColumn {
        if (state.membersRoom == room.id) items(state.members, key = { it.username }) { member ->
            SocialRow(member.displayName, member.username) {
                if (member.owner) Text("群主")
                if (room.canManage && !member.owner && !member.robot) {
                    TextButton(enabled = enabled, onClick = { target = member.username; operation = "remove" }) { Text("移除") }
                    TextButton(enabled = enabled, onClick = { target = member.username; operation = "transfer" }) { Text("转让群主") }
                }
            }
        }
    }
    val pending = target
    if (room.canManage && pending != null) {
        val title = if (operation == "remove") "移除成员" else "转让群主"
        AlertDialog(onDismissRequest = { target = null }, title = { Text(title) }, text = { Text("@$pending") },
            dismissButton = { TextButton(onClick = { target = null }) { Text("取消") } },
            confirmButton = { TextButton(enabled = enabled, onClick = {
                chat.changeMember(room.id, pending, operation) { target = null }
            }) { Text("确认") } })
    }
}

@Composable
private fun SocialUsername(value: String, update: (String) -> Unit, label: String, action: String,
                           enabled: Boolean, submit: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedTextField(value, { if (it.length <= 32) update(it) }, Modifier.weight(1f),
            label = { Text(label) }, singleLine = true)
        TextButton(enabled = enabled && value.isNotBlank(), onClick = submit) { Text(action) }
    }
}

@Composable
private fun SocialRow(name: String, username: String, actions: @Composable () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(name)
            Text("@$username", style = MaterialTheme.typography.bodySmall)
        }
        actions()
    }
    HorizontalDivider()
}

@Composable
private fun SocialError(error: String?) {
    if (error != null) {
        Text(error, Modifier.padding(horizontal = 14.dp), color = MaterialTheme.colorScheme.error)
    }
}
