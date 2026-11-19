internal static class BotFriendAccessChecks
{
    public static async Task Run(ChatStore store, Account robot, Action<bool, string> check)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        async Task<Account> Register(string prefix)
        {
            var account = new Account(Guid.NewGuid().ToString(), prefix + tag, "!fixture");
            check(await store.Register(account), $"register friend-access fixture {prefix}");
            return account;
        }

        var listed = await Register("listed_");
        var pending = await Register("pending_");
        var rejected = await Register("rejected_");
        var late = await Register("late_");
        var historical = await Register("historic_");
        var human = await Register("human_");

        async Task<BotFriendAccessEntry> Entry(Account account) =>
            (await store.GetBotFriendAccess(robot.Id)).Single(entry => entry.UserId == account.Id);

        var preexisting = await store.CreateRoom(listed, new("direct", "", [robot.Name]));
        var listedBeforeRequest = await store.SetBotFriendAllowlist(robot.Id, listed.Name, true);
        check(listedBeforeRequest is { State: "none" } && (await Entry(listed)).Listed &&
              (await store.Friends(listed.Id)).Length == 0,
            "listed users still decide whether to request robot friendship");

        var simultaneous = await Task.WhenAll(Task.Run(() => store.RequestBotFriend(listed.Id, robot.Id)),
            Task.Run(() => store.RequestBotFriend(listed.Id, robot.Id)));
        check(simultaneous.All(grant => grant is { State: "automatic" } && grant.RoomId == preexisting.Id) &&
              (await store.Rooms(listed.Id)).Count(room => room.Id == preexisting.Id) == 1 &&
              (await store.Friends(listed.Id)).Single().State == "friend",
            "parallel repeated requests auto-accept once and reuse the existing direct room");
        check(await store.ReadBotFriendAccess(listed.Id, robot.Id, preexisting.Id) == BotFriendAccessState.Active &&
              (await store.BotRoom(preexisting.Id, listed.Id)).Installed,
            "automatic friendship activates access and installs the robot");
        var originalMessage = await store.Send(listed.Id, listed.Name, preexisting.Id,
            new(Guid.NewGuid().ToString(), "kept history", null));
        check(originalMessage is not null, "automatic private room can store normal chat history");

        var queued = await store.RequestBotFriend(pending.Id, robot.Id);
        check(queued is { State: "pending", RoomId: null } &&
              (await store.Friends(pending.Id)).Single().State == "outgoing" &&
              (await store.Rooms(pending.Id)).Length == 0 &&
              await store.ReadBotFriendAccess(pending.Id, robot.Id) == BotFriendAccessState.Inactive,
            "unlisted request stays pending without creating a usable robot room");
        var approved = await store.DecideBotFriend(robot.Id, pending.Id, true);
        check(approved is { State: "manual", RoomId: not null } &&
              (await store.Friends(pending.Id)).Single().State == "friend" &&
              await store.ReadBotFriendAccess(pending.Id, robot.Id, approved.RoomId) == BotFriendAccessState.Active,
            "administrator acceptance creates an active direct room for an unlisted user");

        await store.RequestBotFriend(rejected.Id, robot.Id);
        var declined = await store.DecideBotFriend(robot.Id, rejected.Id, false);
        check(declined is { State: "rejected", RoomId: null } &&
              (await store.Friends(rejected.Id)).Length == 0 &&
              (await store.Rooms(rejected.Id)).Length == 0 &&
              await store.ReadBotFriendAccess(rejected.Id, robot.Id) == BotFriendAccessState.Inactive,
            "administrator rejection removes the outstanding friend request without granting access");

        await store.RequestBotFriend(late.Id, robot.Id);
        var addedLater = await store.SetBotFriendAllowlist(robot.Id, late.Name, true);
        check(addedLater is { State: "automatic", RoomId: not null } &&
              await store.ReadBotFriendAccess(late.Id, robot.Id, addedLater.RoomId) == BotFriendAccessState.Active &&
              (await store.Friends(late.Id)).Single().State == "friend",
            "adding a pending applicant to the list accepts and activates it immediately");

        var removed = await store.SetBotFriendAllowlist(robot.Id, listed.Name, false);
        check(removed is { State: "revoked" } && !((await Entry(listed)).Listed) &&
              await store.ReadBotFriendAccess(listed.Id, robot.Id, preexisting.Id) == BotFriendAccessState.Inactive &&
              !(await store.BotRoom(preexisting.Id, listed.Id)).Installed,
            "allowlist removal immediately revokes an automatic grant");
        check((await store.FindMessage(preexisting.Id, originalMessage!.Message.Sequence, listed.Id))?.Text == "kept history",
            "revocation retains earlier messages in the original room");

        await store.SetBotFriendAllowlist(robot.Id, pending.Name, true);
        await store.SetBotFriendAllowlist(robot.Id, pending.Name, false);
        check(await store.ReadBotFriendAccess(pending.Id, robot.Id, approved!.RoomId) == BotFriendAccessState.Active,
            "manual grant survives an independent allowlist removal");
        var revoked = await store.RevokeBotFriend(robot.Id, pending.Id);
        check(revoked is { State: "revoked" } &&
              await store.ReadBotFriendAccess(pending.Id, robot.Id, approved.RoomId) == BotFriendAccessState.Inactive,
            "administrator can separately revoke a manual grant");

        var oldRoom = await store.CreateRoom(historical, new("direct", "", [robot.Name]));
        await store.InstallRobot(oldRoom.Id, historical.Id, [robot.Id], true);
        check(await store.RequestFriend(historical.Id, robot.Name) == robot.Id &&
              (await Entry(historical)).State == "pending" &&
              await store.ReadBotFriendAccess(historical.Id, robot.Id, oldRoom.Id) == BotFriendAccessState.Inactive,
            "pre-upgrade robot requests show in the admin queue and cannot use a historical room while pending");
        var historicalGrant = await store.DecideBotFriend(robot.Id, historical.Id, true);
        check(historicalGrant is { State: "manual" } && historicalGrant.RoomId == oldRoom.Id &&
              await store.ReadBotFriendAccess(historical.Id, robot.Id, oldRoom.Id) == BotFriendAccessState.Active,
            "approving a pre-upgrade request restores its original room as an active friendship");

        check(await store.RequestFriend(human.Id, listed.Name) == listed.Id &&
              await store.RespondFriend(listed.Id, human.Id, true) &&
              (await store.Friends(human.Id)).Single().State == "friend",
            "ordinary human friendship still uses its existing acceptance path");
    }
}
