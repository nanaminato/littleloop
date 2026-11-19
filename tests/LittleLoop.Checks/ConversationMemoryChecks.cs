using LittleLoop.Core;

internal static class ConversationMemoryChecks
{
    public static void Run()
    {
        void Observation(string input, MemorySubject subject, MemoryScope scope, string value, string context) =>
            Check(ConversationMemoryPolicy.Observe(input) == new MemoryObservation(subject, scope, value, context), input);

        Observation("以后做完告诉我结果就行", MemorySubject.CompletionNotice, MemoryScope.Durable, "brief", "completion");
        Observation("这次完成通知请附上原任务目标。", MemorySubject.CompletionNotice, MemoryScope.Current, "with-goal", "completion");
        Observation("完成通知再简短点", MemorySubject.CompletionNotice, MemoryScope.Unspecified, "brief", "completion");
        Observation("完成通知太长了", MemorySubject.CompletionNotice, MemoryScope.Unspecified, "brief", "completion");
        Observation("This time, when you're done, just give me the result.", MemorySubject.CompletionNotice,
            MemoryScope.Current, "brief", "completion");
        Observation("For the final update, include the original goal from now on.", MemorySubject.CompletionNotice,
            MemoryScope.Durable, "with-goal", "completion");
        Observation("这类文档的尾随空格有意义。", MemorySubject.DocumentWhitespace, MemoryScope.Durable, "preserve", "document");
        Observation("以后 Markdown 文档不要去掉行尾空格", MemorySubject.DocumentWhitespace, MemoryScope.Durable, "preserve", "markdown");
        Observation("这份样稿请清理行尾空格", MemorySubject.DocumentWhitespace, MemoryScope.Current, "trim", "draft");
        Observation("From now on, for drafts, keep trailing spaces.", MemorySubject.DocumentWhitespace,
            MemoryScope.Durable, "preserve", "draft");
        Observation("This time, when reviewing release notes, check the version.", MemorySubject.TaskProcedure,
            MemoryScope.Current, "check the version", "发布说明");
        Observation("以后整理交接清单时，附上值班电话。", MemorySubject.TaskProcedure,
            MemoryScope.Durable, "附上值班电话", "交接清单");
        var procedure = ConversationMemoryPolicy.Observe("When reviewing release notes, check the version.")!;
        Check(ConversationMemoryPolicy.Applies(procedure, ContinuousGoal.Create("编写发行说明")),
            "a conditional procedure survives translation of its subject");
        Check(!ConversationMemoryPolicy.Applies(procedure, ContinuousGoal.Create("update the release notebook")),
            "a partial English word does not identify the subject of a procedure");
        foreach (var example in new[] { "如果以后做完告诉我结果就行呢？", "他说以后完成通知附带原目标", "“这类文档的尾随空格有意义”",
            "任务：修改文档，并让完成通知简洁", "以后做完告诉我结果就行，删除草稿", "以后这次完成通知简洁",
            "以后只给我结果，并把日志发给同事", "只针对财务部门的文档，尾随空格有意义",
            "昨天处理发布说明时，发现一处笔误", "When I wrote the release notes, the build failed." })
            Check(ConversationMemoryPolicy.Observe(example) is null, "memory abstains: " + example);

        var general = new MemoryObservation(MemorySubject.DocumentWhitespace, MemoryScope.Durable, "preserve", "document");
        bool Applies(string objective, params string[] requirements) => ConversationMemoryPolicy.Applies(general,
            ContinuousGoal.Create(objective) with { Requirements = requirements });
        Check(Applies("把另一份样稿原样搬过去"), "recalled document correction for a related draft task");
        Check(!Applies("这份样稿请清理行尾空格"), "current request vetoes previous preserve advice");
        Check(!Applies("原样复制这份文档", "去掉行尾空白"), "conflicting supplemental requirement vetoes memory");
        Check(Applies("复制样稿，不要清理行尾空格"), "negated trim is a preservation constraint");
        Check(!Applies("修复登录超时问题"), "unrelated task has no document advice");
        Check(!ConversationMemoryPolicy.Applies(general with { Context = "markdown" }, ContinuousGoal.Create("复制交付样稿")),
            "narrow markdown correction does not apply to an unspecified draft");
        Check(ConversationMemoryPolicy.Applies(general with { Context = "markdown" }, ContinuousGoal.Create("copy README.md")),
            "file extension gives a markdown task its context");

        var original = ContinuousGoal.Create("修复登录超时问题");
        foreach (string related in new[] { "请修好登录时超时的故障", "Fix the sign-in timeout" })
            Check(ConversationMemoryPolicy.ExperienceScore(original, ContinuousGoal.Create(related)) is > 0 and < 100,
                "scoped path evidence can support paraphrase: " + related);
        foreach (string unrelated in new[] { "调查登录超时问题", "不要修复登录超时问题", "修复付款超时问题", "修复账单导出问题" })
            Check(ConversationMemoryPolicy.ExperienceScore(original, ContinuousGoal.Create(unrelated)) == 0,
                "no path evidence for different action, component or negation: " + unrelated);
        Check(ConversationMemoryPolicy.ExperienceScore(original, original with { Requirements = ["只读"] }) == 0,
            "different requirements invalidate path hint");
        Check(ConversationMemoryPolicy.ExperienceScore(ContinuousGoal.Create("复制 a.txt 到 b.txt"),
            ContinuousGoal.Create("拷贝 b.txt 到 a.txt")) == 0, "changed literal order invalidates path hint");
        Check(ConversationMemoryPolicy.ExperienceScore(original, original) == 100, "existing exact match has priority");
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
