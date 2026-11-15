package dev.littleloop.chat

import androidx.compose.runtime.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

internal data class ChannelAuditEntry(val id: Long, val actor: String, val action: String,
    val detail: String, val createdAt: String)
internal data class ChannelGrant(val name: String, val rights: Int)
internal data class ChannelSettings(val owner: Boolean, val group: String?, val grants: List<ChannelGrant>)

internal data class ChannelAdministrationUiState(
    val settings: ChannelSettings? = null, val error: Boolean = false, val busy: Boolean = false,
    val title: String, val signPosts: Boolean, val newOwner: String = "", val transferTarget: String? = null,
    val confirmingDeletion: Boolean = false, val subscriber: String = "", val name: String = "",
    val editing: String? = null, val rights: Int = 0, val requestedGroup: String? = null,
    val auditing: Boolean = false, val audit: List<ChannelAuditEntry>? = null,
    val auditMore: Boolean = false, val picker: Boolean = false,
)

/** Events consumed by the state holder; form components never call the repository. */
internal interface ChannelAdministrationActions {
    fun title(value: String)
    fun signatures(value: Boolean)
    fun administrator(value: String)
    fun editGrant(grant: ChannelGrant)
    fun toggleRight(bit: Int)
    fun saveProfile()
    fun saveGrant()
    fun revokeGrant()
    fun newOwner(value: String)
    fun requestTransfer()
    fun cancelTransfer()
    fun transfer()
    fun subscriber(value: String)
    fun removeSubscriber()
    fun loadAudit(before: Long? = null)
    fun openDiscussion()
    fun requestMembership()
    fun togglePicker()
    fun linkDiscussion(group: String?)
    fun requestDeletion()
    fun cancelDeletion()
    fun deleteChannel()
}

@Stable
internal class ChannelAdministrationController(
    private val chat: ChatRepository, private val room: State<Room>, private val username: State<String>,
    private val scope: CoroutineScope, private val dismiss: State<() -> Unit>,
) : ChannelAdministrationActions {
    var ui by mutableStateOf(ChannelAdministrationUiState(title = room.value.title, signPosts = room.value.signPosts))
        private set
    val permissions get() = room.value.channelPermissions(ui.settings)
    private val mutations = Mutex()

    suspend fun refresh() = mutations.withLock {
        try { ui = ui.copy(settings = chat.channelSettings(room.value.id, username.value), error = false) }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { ui = ui.copy(error = true) }
    }

    fun synchronizeProfile(title: String, signatures: Boolean) {
        ui = ui.copy(title = title, signPosts = signatures)
    }

    private fun change(refresh: Boolean = true, action: suspend () -> Unit) {
        if (ui.busy) return
        ui = ui.copy(busy = true, error = false)
        scope.launch {
            try {
                mutations.withLock {
                    action()
                    if (refresh) ui = ui.copy(settings = chat.channelSettings(room.value.id, username.value))
                }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { ui = ui.copy(error = true) }
            finally { ui = ui.copy(busy = false) }
        }
    }

    override fun title(value: String) { ui = ui.copy(title = value.take(100)) }
    override fun signatures(value: Boolean) { ui = ui.copy(signPosts = value) }
    override fun administrator(value: String) { ui = ui.copy(name = value.take(32)) }
    override fun editGrant(grant: ChannelGrant) { ui = ui.copy(name = grant.name, editing = grant.name, rights = grant.rights) }
    override fun toggleRight(bit: Int) { ui = ui.copy(rights = ui.rights xor bit) }
    override fun newOwner(value: String) { ui = ui.copy(newOwner = value.take(32)) }
    override fun subscriber(value: String) { ui = ui.copy(subscriber = value.take(32)) }
    override fun requestTransfer() { if (permissions.owner) ui = ui.copy(transferTarget = ui.newOwner.trim()) }
    override fun cancelTransfer() { ui = ui.copy(transferTarget = null) }
    override fun togglePicker() { ui = ui.copy(picker = !ui.picker) }
    override fun requestDeletion() { if (permissions.owner) ui = ui.copy(confirmingDeletion = true) }
    override fun cancelDeletion() { ui = ui.copy(confirmingDeletion = false) }

    override fun saveProfile() {
        if (!permissions.profile || ui.title.isBlank()) return
        val title = ui.title.trim(); val signed = ui.signPosts
        change { chat.updateChannelProfile(room.value.id, title, signed) }
    }
    override fun saveGrant() {
        if (!permissions.owner || ui.name.isBlank() || ui.rights == 0) return
        val name = ui.name.trim(); val rights = ui.rights
        change { chat.setChannelGrant(room.value.id, name, rights) }
    }
    override fun revokeGrant() {
        if (!permissions.owner) return
        val target = ui.editing ?: return
        change { chat.setChannelGrant(room.value.id, target, 0); ui = ui.copy(editing = null, name = "", rights = 0) }
    }
    override fun transfer() {
        if (!permissions.owner) return
        val target = ui.transferTarget ?: return
        change { chat.transferChannelOwnership(room.value.id, target); ui = ui.copy(newOwner = "", transferTarget = null) }
    }
    override fun removeSubscriber() {
        if (!permissions.subscribers || ui.subscriber.isBlank()) return
        val target = ui.subscriber.trim()
        change { chat.removeChannelSubscriber(room.value.id, target); ui = ui.copy(subscriber = "") }
    }
    override fun loadAudit(before: Long?) {
        if (!permissions.owner || ui.auditing) return
        ui = ui.copy(auditing = true, error = false)
        scope.launch {
            try {
                val page = chat.channelAudit(room.value.id, before)
                ui = ui.copy(audit = if (before == null) page else ui.audit.orEmpty() + page, auditMore = page.size == 50)
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { ui = ui.copy(error = true) }
            finally { ui = ui.copy(auditing = false) }
        }
    }
    override fun openDiscussion() { ui.settings?.group?.let { dismiss.value(); chat.select(it) } }
    override fun requestMembership() {
        val group = ui.settings?.group ?: return
        change { chat.requestDiscussionMembership(room.value.id); ui = ui.copy(requestedGroup = group) }
    }
    override fun linkDiscussion(group: String?) {
        if (permissions.discussion) change { chat.linkDiscussion(room.value.id, group); ui = ui.copy(picker = false) }
    }
    override fun deleteChannel() {
        if (permissions.owner) change(refresh = false) {
            chat.deleteChannel(room.value.id); ui = ui.copy(confirmingDeletion = false); dismiss.value()
        }
    }
}

@Composable
internal fun rememberChannelAdministration(chat: ChatRepository, state: ChatState, room: Room,
    dismiss: () -> Unit): ChannelAdministrationController = key(room.id) {
    val currentRoom = rememberUpdatedState(room)
    val username = rememberUpdatedState(state.username)
    val onDismiss = rememberUpdatedState(dismiss)
    val scope = rememberCoroutineScope()
    val controller = remember(chat) { ChannelAdministrationController(chat, currentRoom, username, scope, onDismiss) }
    LaunchedEffect(room.channelRights) { controller.refresh() }
    LaunchedEffect(room.title, room.signPosts) { controller.synchronizeProfile(room.title, room.signPosts) }
    controller
}
