package dev.littleloop.chat

import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.SaverScope
import org.junit.Assert.*
import org.junit.Test

class HomeStateTest {
    private fun room(id: String, type: String = "group") =
        Room(id, "Audit $id", type, 0, 0, 0, false, false, true, "", 0, null, null)
    private val access = CreationAccess("viewer", true, false, listOf(room("parent")))

    @Test fun homeFilterPreservesServerOrderAndOnlySearchExposesTopics() {
        val rooms = listOf(room("channel", "channel"), room("topic", "topic"), room("group"),
            room("archived").copy(archived = true), room("direct", "direct"), room("saved", "saved"), room("legacy", "legacy"))
        assertEquals(listOf("channel", "group", "direct", "saved", "legacy"), rooms.homeRooms("").map { it.id })
        assertEquals(listOf("topic"), rooms.homeRooms("TOPIC").map { it.id })
        assertTrue(rooms.homeRooms("archived").isEmpty())
        assertTrue(rooms.homeRooms("no match").isEmpty())
    }

    @Test fun topicsExcludeOtherParentsAndArchivedRooms() {
        val topic = room("topic", "topic").copy(parentId = "parent")
        assertEquals(listOf(topic), listOf(topic, topic.copy(id = "archived", archived = true),
            topic.copy(id = "other", parentId = "other"), room("group").copy(parentId = "parent")).topics("parent"))
    }

    @Test fun allGroupRolesIncludingMutedMembersCanCreateTopics() {
        for (parent in listOf(room("parent"), room("parent").copy(canManage = true),
            room("parent").copy(canModerate = true), room("parent").copy(canPost = false))) {
            assertTrue(access.copy(rooms = listOf(parent)).allows(CreationKind.Topic, "parent"))
        }
        assertFalse(access.allows(CreationKind.Topic, "missing"))
        assertFalse(access.copy(rooms = listOf(room("parent", "channel"))).allows(CreationKind.Topic, "parent"))
    }

    @Test fun loggedOutOfflineAndBusyStatesCannotSubmitAnyCreationKind() {
        for (kind in CreationKind.entries) {
            assertTrue(access.allows(kind, "parent"))
            for (blocked in listOf(access.copy(username = ""), access.copy(connected = false), access.copy(busy = true)))
                assertFalse(blocked.allows(kind, "parent"))
        }
    }

    @Test fun emptyGroupNeedsOnlyTitleAndDirectNeedsRecipientWithDraftsPreservedOnSwitch() {
        val state = mutableStateOf(access)
        val submitted = mutableListOf<CreationKind>()
        val controller = CreationController(CreationDraft(CreationKind.Direct), state, null) { kind, _, _, _ -> submitted += kind }
        controller.submit(); assertTrue(submitted.isEmpty())
        controller.members("peer"); controller.submit()
        controller.group(true); assertFalse(controller.canSubmit)
        controller.title("Group"); controller.members(""); controller.submit()
        assertEquals(listOf(CreationKind.Direct, CreationKind.Group), submitted)
        controller.group(false); assertFalse(controller.canSubmit)
        assertEquals("Group", controller.draft.title)
        controller.title("x".repeat(101)); assertEquals("Group", controller.draft.title)
    }

    @Test fun openDialogRechecksMembershipAndConnectionBeforeInvokingSubmission() {
        val state = mutableStateOf(access)
        var calls = 0
        val draft = CreationDraft(CreationKind.Topic).apply { title = "Topic" }
        val controller = CreationController(draft, state, "parent") { _, _, _, _ -> calls++ }
        assertTrue(controller.canSubmit)
        state.value = access.copy(rooms = emptyList()); controller.submit()
        state.value = access.copy(connected = false); controller.submit()
        state.value = access.copy(busy = true); controller.submit()
        assertEquals(0, calls)
        state.value = access; controller.submit(); assertEquals(1, calls)
    }

    @Test fun recreationPreservesTypeAndBothDraftFields() {
        val original = CreationDraft(CreationKind.Group).apply { title = "Draft"; members = "peer, guest" }
        val scope = object : SaverScope { override fun canBeSaved(value: Any) = true }
        val saved = with(CreationDraft.Saver) { scope.save(original) }!!
        val restored = CreationDraft.Saver.restore(saved)!!
        assertEquals(original.kind, restored.kind); assertEquals(original.title, restored.title)
        assertEquals(original.members, restored.members)
    }

    @Test fun directoryOwnerCannotUnsubscribeAndOnlySubscribersCanOpen() {
        for (owner in listOf(true, false)) for (subscribed in listOf(true, false)) {
            val permissions = ChatChannel("id", "Channel", subscribed, owner).directoryPermissions()
            assertEquals(subscribed, permissions.open); assertEquals(!owner, permissions.subscribe)
        }
    }
}
