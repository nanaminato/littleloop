using System.Text.RegularExpressions;

namespace LittleLoop.Core;

public sealed record GoalMessageIntent(string Action, string Text);

public static class GoalMessageRouter
{
    public static GoalMessageIntent Parse(string message, bool hasGoal, bool privateDocumentRequests = false) =>
        Parse(message, hasGoal, privateChat: privateDocumentRequests);

    public static GoalMessageIntent Parse(string message, bool hasGoal, bool privateChat,
        bool taskReply = false, bool awaitingInput = false)
    {
        string text = message.Trim();
        if (text.StartsWith("@littleloop", StringComparison.OrdinalIgnoreCase)) text = text[11..].TrimStart(' ', '：', ':', '，', ',');
        string command = text.TrimEnd('。', '！', '!', '.', '？', '?').Trim();
        if (command is "暂停" or "暂停任务" or "先暂停" or "先停一下" or "暂停一下" or "/pause") return new("pause", "");
        if (command is "恢复" or "恢复任务" or "继续" or "继续吧" or "接着做吧" or "继续工作" or "/resume") return new("resume", "");
        if (command is "取消" or "取消任务" or "取消吧" or "/cancel") return new("cancel", "");
        if (command is "进度" or "查询进度" or "查询任务" or "任务状态" or "进展如何" or "进度如何" or "进度怎么样了" or
            "做到哪了" or "现在做到哪了" or "做完了吗" or "/status") return new("status", "");
        if (privateChat && command.ToLowerInvariant() is "你好" or "您好" or "嗨" or "在吗" or "hello" or "hi")
            return new("reply", "我在。有什么想问的，或想让我帮你做的？");
        if (privateChat && command.ToLowerInvariant() is "谢谢" or "多谢" or "辛苦了" or "thanks" or "thank you")
            return new("reply", "不客气！");
        if (privateChat && command is "好的" or "好" or "收到" or "明白了")
            return new("reply", "好。");
        if (command is "新任务" or "任务" or "开发任务" or "/task") return new("create", "");
        foreach (string prefix in new[] { "/task ", "开发任务：", "开发任务:" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new("create", text[prefix.Length..].Trim());
        foreach (string prefix in new[] { "补充：", "补充:", "补充要求：", "补充要求:", "/revise " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return new("revise", text[prefix.Length..].Trim());
        bool newTask = false;
        foreach (string prefix in new[] { "新任务：", "新任务:", "任务：", "任务:" })
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            text = text[prefix.Length..].Trim();
            if (!privateChat || text.Length == 0) return new("create", text);
            newTask = true;
            break;
        }
        if (privateChat)
        {
            if (text.Length == 0) return new("reply", "你希望我帮你怎么处理这条消息？");
            var polite = PoliteRequest.Match(text);
            string request = polite.Success ? text[polite.Length..].Trim() : text;
            if (!polite.Success && (Discussion.IsMatch(text) || text.TrimEnd('。', '.', '！', '!').EndsWith('吗') ||
                text.EndsWith('?') || text.EndsWith('？'))) return new("chat", text);
            if (PrivateDocumentRequest.IsMatch(request) || ProjectSubject.IsMatch(request) &&
                (ProjectRequest.IsMatch(request) || newTask && VerifyProject.IsMatch(request)))
                return new(taskReply && !newTask ? "revise" : "create", text);
            if (!newTask && hasGoal && (Requirement.IsMatch(text) ||
                (taskReply || awaitingInput) && !polite.Success && !IndependentRequest.IsMatch(text)))
                return new("revise", text);
            return new("chat", text);
        }

        // Groups retain their explicitly addressed, bounded task grammar.
        if (Discussion.IsMatch(text)) return new("help", "");
        if (hasGoal && text.Length > 0 && !text.EndsWith('?') && !text.EndsWith('？') &&
            text is not ("你好" or "谢谢" or "好的")) return new("revise", text);
        if (!hasGoal && !text.EndsWith('?') && !text.EndsWith('？') &&
            new[] { "请修复", "帮我修复", "修复", "请实现", "帮我实现", "实现", "请修改", "帮我修改", "修改" }.Any(text.StartsWith))
            return new("create", text);
        return new("help", "");
    }

    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex Discussion = Pattern(
        """^(?:[>"“「『`]|如果|假如|假设|比如|例如|请问|为什么|怎么|如何|我想知道|(?:有人|他说|她说|你说|用户说)|if\b|what\b|why\b|how\b|suppose\b|for example\b)""");

    private static readonly Regex PoliteRequest = Pattern(
        @"^(?:请问)?你?(?:能不能|可不可以|可以|能否|能)帮我|^(?:could|would|can) you (?:please )?");
    private static readonly Regex ProjectRequest = Pattern(
        @"^(?:(?:请|帮我|帮忙|麻烦你?|替我|我希望你|我想让你|please\b|help me\b)\s*)*(?:修复|修好|解决|修改|实现|开发|重构|编译|调试|测试|运行|更新|(?:给|为|在|把|将)[^。！？\n]{1,100}(?:改为|改成|替换|增加|加入|添加|实现|修复)|fix\b|patch\b|implement\b|refactor\b|build\b|test\b|run\b|update\b|edit\b|add\b)");
    private static readonly Regex ProjectSubject = Pattern(
        @"代码|仓库|这个项目|本项目|函数|编译|配置解析|单元测试|回归测试|现有测试|固定测试|测试用例|登录(?:时的?|的)?(?:问题|超时|故障)|\b(?:bug|codebase|repository|function|unit tests?|verify)\b|\.(?:cs|csproj|sln|ts|js|py|java|kt|cpp|txt|json|yaml|yml)\b");
    private static readonly Regex VerifyProject = Pattern(
        @"(?:运行|通过|执行)\s*(?:宿主|项目|现有|固定)?\s*(?:verify|测试)|\brun (?:the )?(?:host )?(?:verify|tests?)\b");
    private static readonly Regex IndependentRequest = Pattern(
        @"^(?:请问|请查|帮我|请帮我|麻烦|我想知道|教我|给我|查一下|查询|整理|做一|写一|能不能|你能|可以|please\b|can you\b|could you\b|help me\b)");
    private static readonly Regex Requirement = Pattern(
        @"^(?:请帮我|请|帮我|please )?(?:保留|沿用|记得|还要|还需要|也要|不要改|只改|改成|改为|需要兼容|keep\b|preserve\b|also\b|instead\b)");

    private static readonly Regex PrivateDocumentRequest = Pattern(
        @"^(?:(?:请|麻烦|帮我|请帮我)?(?:把|将)(?:另一份|这份|那份|一份|这个|那个|那篇|这篇)?(?:样稿|草稿|文稿|文档|文本|文件).{0,80}?(?:复制到|拷贝到|转存到|搬到|移到|原样搬过去)|(?:请|请帮我|帮我)(?:复制|整理|撰写|编写|更新)(?:这次的|这份|本次的|项目的|另一份)?(?:发布说明|版本说明)|please (?:copy|move) (?:the |this |another )?(?:document|draft)\b|please (?:edit|draft|write) (?:the )?release notes\b)");
}
