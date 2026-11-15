package dev.littleloop.chat

import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

data class ChatMember(val username: String, val displayName: String, val robot: Boolean, val owner: Boolean,
    val administrator: Boolean = false, val silenced: Boolean = false)

internal data class PendingMemberAction(val username: String, val operation: GroupMemberOperation)

@Stable
internal class GroupMembersUiState {
    var username by mutableStateOf("")
    var pending by mutableStateOf<PendingMemberAction?>(null)
    var joinRequests by mutableStateOf<List<String>>(emptyList())
    var reviewingJoins by mutableStateOf(false)

    companion object {
        val Saver = listSaver<GroupMembersUiState, String>(
            save = { listOf(it.username, it.pending?.username.orEmpty(), it.pending?.operation?.name.orEmpty()) },
            restore = { values -> GroupMembersUiState().apply {
                username = values[0]
                val operation = GroupMemberOperation.entries.firstOrNull { it.name == values[2] }
                if (values[1].isNotEmpty() && operation != null) pending = PendingMemberAction(values[1], operation)
            } },
        )
    }
}

@Stable
internal class GroupMembersController(
    private val chat: ChatRepository, private val state: State<ChatState>, private val room: State<Room>,
    val ui: GroupMembersUiState,
) {
    val enabled get() = state.value.connected && !state.value.busy
    val permissions get() = room.value.groupPermissions()
    val members get() = state.value.members.takeIf { state.value.membersRoom == room.value.id }.orEmpty()
    fun operations(member: ChatMember) = room.value.memberOperations(member, state.value.username)
    val pending get() = ui.pending?.takeIf { action ->
        members.any { it.username == action.username && action.operation in operations(it) }
    }

    fun username(value: String) { if (value.length <= 32) ui.username = value }
    fun invite() {
        if (!enabled || !permissions.invite || ui.username.isBlank()) return
        val submitted = ui.username
        chat.changeMember(room.value.id, submitted, "invite") { if (ui.username == submitted) ui.username = "" }
    }
    fun select(username: String, operation: GroupMemberOperation) {
        val member = members.firstOrNull { it.username == username } ?: return
        if (enabled && operation in operations(member)) ui.pending = PendingMemberAction(username, operation)
    }
    fun dismiss() { ui.pending = null }
    fun confirm() {
        val action = pending ?: return
        if (!enabled) return
        chat.changeMember(room.value.id, action.username, action.operation.wire) {
            if (ui.pending == action) ui.pending = null
        }
    }
    fun review(open: Boolean) { ui.reviewingJoins = open && permissions.reviewJoins }
    fun respondJoin(applicant: String, accepted: Boolean) {
        if (!enabled || !permissions.reviewJoins || applicant !in ui.joinRequests) return
        chat.changeMember(room.value.id, applicant, if (accepted) "approve_join" else "decline_join") {
            ui.joinRequests = ui.joinRequests - applicant
        }
    }
    suspend fun observeJoins() {
        if (!permissions.reviewJoins) {
            ui.joinRequests = emptyList(); ui.reviewingJoins = false
            return
        }
        while (permissions.reviewJoins) {
            try {
                val requests = chat.discussionJoinRequests(room.value.id)
                if (permissions.reviewJoins) ui.joinRequests = requests
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { /* Preserve the last successful snapshot; the next refresh retries. */ }
            delay(5000)
        }
    }
}

@Composable
internal fun rememberGroupMembersController(chat: ChatRepository, state: ChatState, room: Room): GroupMembersController = key(state.username, room.id) {
    val ui = rememberSaveable(saver = GroupMembersUiState.Saver) { GroupMembersUiState() }
    val currentState = rememberUpdatedState(state)
    val currentRoom = rememberUpdatedState(room)
    val controller = remember(chat, ui) { GroupMembersController(chat, currentState, currentRoom, ui) }
    LaunchedEffect(controller, controller.permissions.reviewJoins) { controller.observeJoins() }
    return@key controller
}
