package dev.littleloop.chat

import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable

@Stable
internal class CreationDraft(kind: CreationKind) {
    var kind by mutableStateOf(kind)
    var title by mutableStateOf("")
    var members by mutableStateOf("")
    val valid get() = if (kind == CreationKind.Direct) members.isNotBlank() else title.isNotBlank()

    companion object {
        val Saver = listSaver<CreationDraft, String>(
            save = { listOf(it.kind.name, it.title, it.members) },
            restore = { CreationDraft(CreationKind.valueOf(it[0])).apply { title = it[1]; members = it[2] } },
        )
    }
}

@Stable
internal class CreationController(
    val draft: CreationDraft, private val access: State<CreationAccess>, private val parent: String?,
    private val create: (CreationKind, String, String, String?) -> Unit,
) {
    val canSubmit get() = draft.valid && access.value.allows(draft.kind, parent)
    fun group(value: Boolean) {
        if (draft.kind == CreationKind.Direct || draft.kind == CreationKind.Group)
            draft.kind = if (value) CreationKind.Group else CreationKind.Direct
    }
    fun title(value: String) { if (value.length <= 100) draft.title = value }
    fun members(value: String) { draft.members = value }
    fun submit() {
        // Recheck the current session and parent membership even for an already-open dialog.
        if (canSubmit) create(draft.kind, draft.title, draft.members, parent)
    }
}

@Composable
internal fun rememberCreationController(
    chat: ChatRepository, state: ChatState, kind: CreationKind, parent: String?, created: () -> Unit,
): CreationController = key(state.username, kind, parent) {
    val draft = rememberSaveable(saver = CreationDraft.Saver) { CreationDraft(kind) }
    val access = rememberUpdatedState(state.creationAccess())
    val onCreated = rememberUpdatedState(created)
    remember(chat) {
        CreationController(draft, access, parent) { type, title, members, parentId ->
            when (type) {
                CreationKind.Direct -> chat.create("direct", title, members) { onCreated.value() }
                CreationKind.Group -> chat.create("group", title, members) { onCreated.value() }
                CreationKind.Channel, CreationKind.Topic -> chat.createSpace(parentId, title) { onCreated.value() }
            }
        }
    }
}
