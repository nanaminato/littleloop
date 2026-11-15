package dev.littleloop.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

@Composable
internal fun ConversationTimeline(state: ChatState, room: Room, menu: Long?,
    discussionSummaries: Map<Long, ChannelDiscussionSummary>, modifier: Modifier = Modifier,
    onAction: (ConversationAction) -> Unit, onOlder: () -> Unit,
    onRetry: (PendingMessage) -> Unit, onDiscard: (PendingMessage) -> Unit) {
    val bySequence = remember(state.messages) { state.messages.associateBy(ChatMessage::sequence) }
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

    LazyColumn(
        modifier.fillMaxWidth().background(MaterialTheme.colorScheme.background),
        state = list,
        reverseLayout = true,
        contentPadding = PaddingValues(horizontal = 10.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp, Alignment.Bottom),
    ) {
        items(visibleQueue.asReversed(), key = PendingMessage::id) { message ->
            PendingBubble(message, state.upload, state.busy, { onRetry(message) }, { onDiscard(message) })
        }
        itemsIndexed(reversed, key = { _, message -> message.sequence }) { index, message ->
            val older = reversed.getOrNull(index + 1)
            MessageBubble(
                state.username, state.connected && !state.busy, room.messagePermissions(message, state.username), room, message, older,
                replyText = bySequence[message.replyTo]?.text?.ifBlank { null },
                highlighted = message.sequence == state.focus,
                menuOpen = menu == message.sequence,
                onReply = { onAction(ConversationAction.Reply(message.sequence)) },
                onMenu = { onAction(ConversationAction.Menu(message.sequence)) },
                onDismissMenu = { onAction(ConversationAction.DismissMenu) },
                onOperation = { operation -> onAction(ConversationAction.Operate(message.sequence, operation)) },
                onPreview = { onAction(ConversationAction.Preview(message.sequence)) },
                onCard = { onAction(ConversationAction.Details(message.sequence)) },
                discussionSummary = discussionSummaries[message.sequence],
                onComments = if (room.type == "channel" || message.discussionRoot) ({ onAction(ConversationAction.Comments(message.sequence)) }) else null,
            )
        }
        if ((state.messages.firstOrNull()?.sequence ?: 0) > 1) item {
            TextButton(onClick = onOlder, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.conv_load_earlier))
            }
        } else if (state.messages.isEmpty()) item {
            EmptyState("✦", stringResource(R.string.conv_empty))
        }
    }
}
