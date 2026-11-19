using LittleLoop.Tools;

internal static class NaturalMemoryChecks
{
    public static async Task Run(string connection, string provider, ProjectFiles files, VerifiedGoalExperience acceptedPatch,
        Action<bool, string> check)
    {
        var store = new ChatStore(connection, false, provider);
        var robot = await store.GetRobot(ChatStore.BotName);
        string tag = Guid.NewGuid().ToString("N")[..8];
        var owner = new Account(Guid.NewGuid().ToString(), "memory_" + tag, "!fixture");
        var peer = new Account(Guid.NewGuid().ToString(), "peer_" + tag, "!fixture");
        await store.Register(owner);
        await store.Register(peer);
        var room = await store.CreateRoom(owner, new("direct", null, [robot.Name]));
        var otherRoom = await store.CreateRoom(peer, new("direct", null, [robot.Name]));
        var group = await store.CreateRoom(owner, new("group", "Memory context", [peer.Name]));
        await store.InstallRobot(room.Id, owner.Id, [robot.Id], true);
        await store.InstallRobot(otherRoom.Id, peer.Id, [robot.Id], true);
        await store.InstallRobot(group.Id, owner.Id, [robot.Id], true);
        await using var lease = await store.AcquireBotLease();
        var replies = new List<ChatMessage>();

        async Task Deliver()
        {
            while (await store.NextGoalNotice() is { } notice)
            {
                var sent = await store.Send(robot.Id, robot.Name, notice.Room, new(notice.Id, notice.Text, notice.ReplyTo),
                    card: notice.TaskId is null ? null : new("task", TaskId: notice.TaskId));
                replies.Add(sent!.Message);
                await store.CompleteGoalNotice(notice.Id);
            }
        }
        async Task<ChatMessage> Say(string text, long? reply = null, string? client = null, string? to = null, Account? actor = null)
        {
            var user = actor ?? owner;
            var sent = await store.Send(user.Id, user.Name, to ?? room.Id,
                new(client ?? Guid.NewGuid().ToString(), text, reply), goalRobot: robot.Id);
            if (sent is null) throw new Exception("Memory fixture message was not stored");
            while (await store.ProcessGoalMessage(robot.Id, id => id == owner.Id || id == peer.Id, files.PolicyHash)) { }
            await Deliver();
            return sent.Message;
        }
        ChatMessage Reply(ChatMessage source) => replies.Last(m => m.RoomId == source.RoomId && m.ReplyTo == source.Sequence);
        async Task<BotWork> TaskFor(string objective, string? to = null, Account? actor = null, bool natural = false)
        {
            string prefix = natural ? "" : "任务：";
            var message = await Say((to == group.Id ? "@littleloop " : "") + prefix + objective, to: to, actor: actor);
            var card = await store.FindMessage(message.RoomId, message.Sequence, (actor ?? owner).Id);
            return await store.ClaimBotWork(lease, w => w.Id == card?.Card?.TaskId)
                ?? throw new Exception("Memory fixture task did not reach dispatch: " + objective);
        }
        async Task<string> Complete(BotWork work, VerifiedGoalExperience? patch = null)
        {
            await store.FinishGoal(work, "completed", experience: patch);
            await Deliver();
            return replies.Last(m => m.RoomId == work.Room && m.ReplyTo == work.Sequence && m.Card?.TaskId == work.Id).Text;
        }
        const string detailedOnce = "这次完成通知请附上原任务目标";
        const string concise = "以后做完告诉我结果就行";
        await Say(concise);
        store = new ChatStore(connection, false, provider);
        check(Reply(await Say("查看偏好")).Text.Contains("当前偏好：完成通知简洁"),
            "ordinary durable preference survives reopening the store");

        var first = await TaskFor("检查第一份测试数据");
        var note = await Say(detailedOnce, first.Sequence);
        int beforeDuplicate = replies.Count;
        await Say(detailedOnce, first.Sequence, note.ClientId);
        check(replies.Count == beforeDuplicate && (await store.ReadBotWork(first.Id, owner.Id))?.Goal?.Version == 1,
            "retry of a local preference neither duplicates its reply nor revises the task");
        check((await Complete(first)).Contains("任务目标：" + first.Goal!.Objective),
            "task-specific notification overrides the durable preference");
        var noOverride = await TaskFor("检查默认通知");
        check(!(await Complete(noOverride)).Contains("任务目标："), "one task's preference does not affect later work");

        var second = await TaskFor("检查第二份测试数据");
        var ambiguous = await TaskFor("独立的并行任务");
        await Say(detailedOnce);
        check(!(await Complete(ambiguous)).Contains("任务目标："),
            "unanchored one-time preference does not bind to an arbitrary one of two tasks");
        await Say(detailedOnce, second.Sequence);
        check((await Complete(second)).Contains("任务目标："), "reply to task card selects the intended notification");
        var third = await TaskFor("检查第三份测试数据");
        var thirdNote = await Say(detailedOnce, third.Sequence);
        var suggestion = Reply(thirdNote);
        await Complete(third);
        check(suggestion.Text.Contains("确认") || suggestion.Text.Contains('？'),
            "consistent instructions on distinct tasks produce an explicit preference proposal");
        check(Reply(await Say("查看偏好")).Text.Contains("当前偏好：完成通知简洁"),
            "observed repetition alone leaves the durable preference unchanged");
        await Say("确认偏好", actor: peer, to: otherRoom.Id);
        check(Reply(await Say("查看偏好", actor: peer, to: otherRoom.Id)).Text.Contains("尚未保存偏好") &&
            Reply(await Say("查看偏好")).Text.Contains("当前偏好：完成通知简洁"),
            "another user cannot confirm someone else's pending preference");
        await Say("确认偏好", suggestion.Sequence);
        store = new ChatStore(connection, false, provider);
        check((await Complete(await TaskFor("确认以后的通知"))).Contains("任务目标："),
            "confirmed proposal changes the next notification after restart");
        var feedbackReplies = new List<ChatMessage>();
        for (int sample = 1; sample <= 3; sample++)
        {
            var job = await TaskFor("验收通知样例 " + sample);
            await Complete(job);
            var delivered = replies.Last(m => m.Card?.TaskId == job.Id && m.ReplyTo == job.Sequence);
            var feedback = await Say("完成通知简短一点", delivered.Sequence);
            feedbackReplies.Add(Reply(feedback));
            var original = await store.FindMessage(room.Id, delivered.Sequence, owner.Id);
            check(original?.Text == delivered.Text && delivered.Text.Contains("任务目标："),
                "post-completion feedback preserves the delivered notification " + sample);
        }
        var reverseSuggestion = feedbackReplies[^1];
        check(reverseSuggestion.Text.Contains("确认") || reverseSuggestion.Text.Contains('？'),
            "feedback on three different delivered notifications can propose a preference");
        await Say("不用", reverseSuggestion.Sequence);
        await Say("是", reverseSuggestion.Sequence);
        check((await Complete(await TaskFor("已拒绝建议后的通知"))).Contains("任务目标："),
            "declining a proposal and replying to its stale message preserve the saved preference");

        const string correction = "这类文档的尾随空格有意义。";
        var currentDraft = await TaskFor("复制今天的文档");
        var source = await Say(correction);
        var corrected = await store.ReadBotWork(currentDraft.Id, owner.Id);
        check(corrected?.Goal is { Version: 2, Status: "queued" } correctedGoal &&
            correctedGoal.Requirements.Contains(correction),
            "unanchored correction updates the sole applicable task and preserves its requirement");
        await store.FinishGoal(currentDraft, "completed");
        await Complete(await store.ClaimBotWork(lease, w => w.Id == currentDraft.Id)
            ?? throw new Exception("Corrected document task did not resume"));
        store = new ChatStore(connection, false, provider);
        await Complete(await TaskFor("修复登录超时问题"));
        var discussion = await Say("如果把另一份样稿复制到存档目录会怎样？");
        check((await store.FindMessage(room.Id, discussion.Sequence, owner.Id))?.Card is null &&
            await store.ClaimBotWork(lease, w => w.Room == room.Id) is null,
            "discussing the same document operation does not create a task");
        var neutral = await TaskFor("把另一份样稿复制到存档目录", natural: true);
        check((await store.PrepareGoalMemory(neutral, files)).LessonHints.Any(l => l.SourceSequence == source.Sequence),
            "a document-copy task can recall formatting guidance without restating the rule");
        await Complete(neutral);
        var copy = await TaskFor("把另一份样稿原样搬过去");
        var prepared = await store.PrepareGoalMemory(copy, files);
        check(prepared.LessonHints is [{ Value: "preserve" } lesson] && lesson.SourceSequence == source.Sequence &&
            LittleLoopChatAdapter.BuildPrompt(prepared) != LittleLoopChatAdapter.BuildPrompt(copy),
            "new task gets a sourced document lesson in the production prompt after an unrelated task and restart");
        check((await store.ReadGoalLessons(copy, new string('A', 64))).Length == 0, "lesson cannot cross project grants");
        await Say("补充：这份样稿请清理行尾空格", copy.Sequence);
        check((await store.PrepareGoalMemory(copy, files)).LessonHints.Length == 0,
            "stale dispatch no longer receives the previous version's lesson");
        var changed = await store.ClaimBotWork(lease, w => w.Id == copy.Id) ?? throw new Exception("Revised copy was not ready");
        check((await store.PrepareGoalMemory(changed, files)).LessonHints.Length == 0,
            "a newer conflicting requirement suppresses prior lessons");
        await Complete(changed);
        var clean = await TaskFor("这份样稿请清理行尾空格");
        check((await store.PrepareGoalMemory(clean, files)).LessonHints.Length == 0, "explicit opposite goal overrides learning");
        await Complete(clean);
        foreach (var (target, actor) in new[] { (otherRoom.Id, peer), (group.Id, owner) })
        {
            var isolated = await TaskFor("把另一份样稿原样搬过去", target, actor);
            check((await store.PrepareGoalMemory(isolated, files)).LessonHints.Length == 0,
                "document lesson stays within its private user and room: " + (target == group.Id ? "group" : "other user"));
            await Complete(isolated);
        }
        await Say("撤回经验", source.Sequence);
        var revoked = await TaskFor("把另一份样稿原样搬过去");
        check((await store.PrepareGoalMemory(revoked, files)).LessonHints.Length == 0,
            "revoking a correction at its source prevents future recall");
        await Complete(revoked);

        const string procedure = "以后处理发布说明时，先核对版本号";
        var procedureSource = await Say(procedure);
        await Say("以后处理PeerLedger时，先核对金额", to: otherRoom.Id, actor: peer);
        var release = await TaskFor("请撰写这次的发布说明", natural: true);
        var releaseMemory = await store.PrepareGoalMemory(release, files);
        check(releaseMemory.LessonHints.Any(h => h.SourceSequence == procedureSource.Sequence && h.Value.Contains("核对版本号")),
            "bounded open-vocabulary procedure is recalled with its original source");
        await Complete(release);
        var contrary = await TaskFor("请撰写这次的发布说明，这次不要核对版本号");
        var contrarySelector = new SelectionModel(() => Task.FromResult(ChatStore.ToJson(new
            { source = procedureSource.Sequence, quote = "发布说明" })));
        check((await store.PrepareGoalMemory(contrary, files, contrarySelector)).LessonHints.Length == 0 && contrarySelector.Calls == 0,
            "current prohibition suppresses an otherwise matching project procedure");
        await Complete(contrary);
        var unrelated = await TaskFor("修复登录超时问题");
        check((await store.PrepareGoalMemory(unrelated, files)).LessonHints.Length == 0,
            "unrelated task receives no release procedure");
        await Complete(unrelated);

        const string paraphrasedTopic = "面向用户的本次更新清单";
        var semanticGoal = await TaskFor("整理" + paraphrasedTopic);
        string Selection(long sourceSequence, string quote) => ChatStore.ToJson(new { source = sourceSequence, quote });
        string selected = Selection(procedureSource.Sequence, paraphrasedTopic);
        var selector = new SelectionModel(() => Task.FromResult(selected));
        check((await store.PrepareGoalMemory(semanticGoal, files)).LessonHints.Length == 0,
            "unrecognized paraphrase has no rule-only match");
        var semantic = await store.PrepareGoalMemory(semanticGoal, files, selector);
        check(selector.Calls == 1 && semantic.LessonHints is [var selectedLesson] &&
            selectedLesson.SourceSequence == procedureSource.Sequence && selectedLesson.Value.Contains("核对版本号") &&
            !selector.Input.Contains("PeerLedger", StringComparison.OrdinalIgnoreCase),
            "optional selector links a paraphrased topic to a stored instruction by source and quote");
        foreach (string invalid in new[] { Selection(procedureSource.Sequence + 10000, paraphrasedTopic),
            Selection(procedureSource.Sequence, "a quotation the user never wrote"), "invalid json" })
        {
            var invalidSelector = new SelectionModel(() => Task.FromResult(invalid));
            check((await store.PrepareGoalMemory(semanticGoal, files, invalidSelector)).LessonHints.Length == 0 &&
                invalidSelector.Calls == 1, "untrusted selector output must cite an offered source and literal task text");
        }
        var deletingSelector = new SelectionModel(async () =>
        {
            check(await store.ChangeMessage(owner.Id, room.Id, procedureSource.Sequence, null) is not null,
                "user can delete the original procedure while selection is in progress");
            return selected;
        });
        check((await store.PrepareGoalMemory(semanticGoal, files, deletingSelector)).LessonHints.Length == 0 &&
            deletingSelector.Calls == 1, "revoked source is checked again after the model returns");
        await Complete(semanticGoal);
        var deleted = await TaskFor("请撰写这次的发布说明");
        check((await store.PrepareGoalMemory(deleted, files)).LessonHints.Length == 0,
            "deleting a procedure's source prevents recall");
        await Complete(deleted);

        await Say("启用经验");
        var training = await TaskFor("修复登录超时问题");
        await Complete(training, acceptedPatch);
        var paraphrase = await TaskFor("请修好登录时超时的故障");
        var withPath = await store.PrepareGoalMemory(paraphrase, files);
        check(withPath.ExperienceHint is { } path && path.SourceSequence == training.Sequence &&
            path.Paths.SequenceEqual(acceptedPatch.Paths) && LittleLoopChatAdapter.BuildPrompt(withPath).Contains("code.txt"),
            "paraphrased goal receives the verified path in the production prompt");
        string sourcePath = files.Resolve("code.txt");
        string movedPath = Path.Combine(files.Root, "moved-code.txt");
        File.Move(sourcePath, movedPath);
        try
        {
            check((await store.PrepareGoalMemory(paraphrase, files)).ExperienceHint is null,
                "a moved file is dropped when preparing an otherwise matching memory");
        }
        finally { File.Move(movedPath, sourcePath); }
        await Complete(paraphrase);
        var different = await TaskFor("修复支付超时问题");
        check((await store.PrepareGoalMemory(different, files)).ExperienceHint is null,
            "similar phrasing on a different subsystem does not import the old path");
        await Complete(different);
    }

    private sealed class SelectionModel(Func<Task<string>> select) : ISemanticModelClient
    {
        public string Provider => "fixture";
        public string Model => "controlled-selection";
        public int Calls { get; private set; }
        public string Input { get; private set; } = "";
        public async Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Input = input;
            return new(await select(), Model);
        }
    }
}
