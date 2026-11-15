package dev.littleloop.chat

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

@Composable
internal fun ColumnScope.FriendsContent(friends: List<ChatFriend>, username: String, enabled: Boolean, error: String?,
    onUsername: (String) -> Unit, onRequest: () -> Unit, onRespond: (String, Boolean) -> Unit, onOpen: (String) -> Unit) {
    SocialUsername(username, onUsername, stringResource(R.string.friends_username), stringResource(R.string.friends_add), enabled, onRequest)
    SocialError(error)
    LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
        if (friends.isEmpty()) item { EmptyState("♧", stringResource(R.string.friends_empty)) }
        items(friends, key = ChatFriend::id) { friend ->
            FriendRow(friend, enabled, onRespond, onOpen)
            HorizontalDivider(Modifier.padding(start = 68.dp))
        }
    }
}

@Composable
private fun FriendRow(friend: ChatFriend, enabled: Boolean, onRespond: (String, Boolean) -> Unit, onOpen: (String) -> Unit) {
    val permissions = friend.permissions()
    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        RoomAvatar(friend.displayName, "direct", 40.dp)
        Column(Modifier.weight(1f)) {
            Text(friend.displayName, style = MaterialTheme.typography.titleMedium)
            Text("@${friend.username}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (permissions.respond) {
                TextButton(enabled = enabled, onClick = { onRespond(friend.id, true) }) { Text(stringResource(R.string.friends_accept)) }
                TextButton(enabled = enabled, onClick = { onRespond(friend.id, false) }) { Text(stringResource(R.string.friends_decline)) }
            }
            if (friend.state == "outgoing") Text(stringResource(R.string.friends_pending), style = MaterialTheme.typography.bodySmall)
            if (permissions.openChat) TextButton(enabled = enabled, onClick = { onOpen(friend.id) }) { Text(stringResource(R.string.friends_direct)) }
        }
    }
}
