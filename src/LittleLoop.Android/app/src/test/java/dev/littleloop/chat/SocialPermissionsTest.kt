package dev.littleloop.chat

import androidx.compose.runtime.saveable.SaverScope
import org.junit.Assert.*
import org.junit.Test

class SocialPermissionsTest {
    private fun room(owner: Boolean = false, admin: Boolean = false, type: String = "group") =
        Room("group", "Group", type, 0, 0, 0, false, false, true, "", 0, null, null,
            canManage = owner, canModerate = owner || admin)
    private val member = ChatMember("peer", "Peer", robot = false, owner = false)

    @Test fun friendStateDeterminesAllowedActions() {
        for (state in listOf("incoming", "outgoing", "friend", "unknown")) {
            val permissions = ChatFriend("id", "peer", "Peer", state).permissions()
            assertEquals(state == "incoming", permissions.respond)
            assertEquals(state == "friend", permissions.openChat)
        }
    }
    @Test fun onlyGroupOwnerCanInviteAndReviewJoins() {
        assertTrue(room(owner = true).groupPermissions().invite)
        assertTrue(room(owner = true).groupPermissions().reviewJoins)
        for (room in listOf(room(), room(admin = true), room(owner = true, type = "topic"), room(owner = true, type = "channel"))) {
            assertFalse(room.groupPermissions().invite); assertFalse(room.groupPermissions().reviewJoins)
        }
    }
    @Test fun ordinaryAndSilencedMembersCannotManagePeers() {
        assertTrue(room().memberOperations(member, "viewer").isEmpty())
        assertTrue(room().copy(canPost = false).memberOperations(member, "viewer").isEmpty())
    }
    @Test fun administratorCanOnlyRemoveOrChangeMuteForOrdinaryMembers() {
        assertEquals(listOf(GroupMemberOperation.Remove, GroupMemberOperation.Silence), room(admin = true).memberOperations(member, "viewer"))
        assertEquals(listOf(GroupMemberOperation.Remove, GroupMemberOperation.Unsilence), room(admin = true).memberOperations(member.copy(silenced = true), "viewer"))
        assertTrue(room(admin = true).memberOperations(member.copy(administrator = true), "viewer").isEmpty())
    }
    @Test fun ownerCanPromoteDemoteAndTransferButCannotTargetSelfOwnerOrRobot() {
        val owner = room(owner = true)
        assertEquals(listOf(GroupMemberOperation.Remove, GroupMemberOperation.Silence, GroupMemberOperation.Promote, GroupMemberOperation.Transfer), owner.memberOperations(member, "viewer"))
        assertTrue(GroupMemberOperation.Demote in owner.memberOperations(member.copy(administrator = true), "viewer"))
        for (target in listOf(member.copy(username = "viewer"), member.copy(owner = true), member.copy(robot = true))) {
            assertTrue(owner.memberOperations(target, "viewer").isEmpty())
        }
    }
    @Test fun latestActorAndTargetRolesInvalidatePreviouslyAllowedActions() {
        val administrator = room(admin = true)
        assertTrue(GroupMemberOperation.Remove in administrator.memberOperations(member, "viewer"))
        assertFalse(GroupMemberOperation.Remove in administrator.memberOperations(member.copy(administrator = true), "viewer"))
        assertFalse(GroupMemberOperation.Remove in room().memberOperations(member, "viewer"))
        assertFalse(GroupMemberOperation.Transfer in administrator.memberOperations(member, "viewer"))
        assertFalse(GroupMemberOperation.Silence in room(owner = true).memberOperations(member.copy(silenced = true), "viewer"))
    }
    @Test fun recreationPreservesInputAndConfirmationButReloadsJoinRequests() {
        val original = GroupMembersUiState().apply {
            username = "invitee"; pending = PendingMemberAction("peer", GroupMemberOperation.Transfer)
            joinRequests = listOf("applicant"); reviewingJoins = true
        }
        val scope = object : SaverScope { override fun canBeSaved(value: Any) = true }
        val saved = with(GroupMembersUiState.Saver) { scope.save(original) }!!
        val restored = GroupMembersUiState.Saver.restore(saved)!!
        assertEquals(original.username, restored.username); assertEquals(original.pending, restored.pending)
        assertTrue(restored.joinRequests.isEmpty()); assertFalse(restored.reviewingJoins)
    }
}
