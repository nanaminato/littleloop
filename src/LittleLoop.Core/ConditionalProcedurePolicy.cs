using System.Text;
using System.Text.RegularExpressions;

namespace LittleLoop.Core;

/// <summary>Grounds a reusable practice in the topic and action the user actually named.</summary>
public static class ConditionalProcedurePolicy
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex ChineseRule = new(
        @"^(?:请记住[：:,， ]*|以后|今后|往后|下次起|每次|通常|一般|我们习惯)*\s*(?:在|当|遇到|对于)?(?<topic>[^，,。！？?；;：:\r\n]{2,90}?)(?:的时候|时|这类任务)[，,：: ]+(?<action>[^。！？?；;\r\n]{2,220})[。.!！]?$", Options);
    private static readonly Regex EnglishRule = new(
        @"^(?:(?:from now on|in future|as a rule|remember)[,: ]+)?(?:when(?:ever)?|for|every time)\s+(?<topic>[^,:;.?!\r\n]{2,90})[,:]\s*(?<action>[^;?!\r\n]{2,220}?)[.!]?$", Options);
    private static readonly Regex Teaching = new(
        @"^(?:请记住|以后|今后|往后|下次起|每次|通常|一般|我们习惯|这次|本次|这一次|from now on\b|in future\b|as a rule\b|remember\b|whenever\b|every time\b)", Options);
    private static readonly Regex Instruction = new(
        @"^(?:请|要|先|务必|应|需要|建议|记得|别|不要|避免|检查|核对|保留|使用|确认|记录|按|列出|附上|please\b|first\b|always\b|check\b|keep\b|use\b|verify\b|include\b|review\b|compare\b|record\b|confirm\b|list\b|avoid\b)", Options);
    private static readonly Regex TopicPrefix = new(
        @"^(?:(?:我们|你|请|需要|制作|处理|撰写|编写|整理|检查|准备|修改|生成|审阅|审核|写|做|我让你|有关|关于|这类|这份|项目的)\s*|(?:you|we|the|a|an|our|this|working on|work on|preparing|prepare|drafting|draft|writing|write|editing|edit|reviewing|review|creating|create|updating|update|handling|processing)\s+)+", Options);
    private static readonly Regex Words = new(@"[a-z0-9]+", Options);
    private static readonly Regex Cjk = new(@"[\p{IsCJKUnifiedIdeographs}]+", RegexOptions.CultureInvariant);
    private static readonly Regex EnglishOverride = new(
        @"\b(?:only|instead|except|skip|avoid|without|ignore|never|do\s+not|don['’]t|no\s+need|must\s+not)\b", Options);
    private static readonly string[] ChineseOverrides =
        ["这次不用", "这次不必", "这次不", "本次不", "不必", "不需要", "不再", "无需", "不用", "不要", "跳过", "忽略", "禁止", "不得", "只需", "只做", "只改", "只要", "仅", "改为", "除外"];
    // Aliases cover common bilingual names. Unlisted topics retain their original content words.
    private static readonly (Regex Aliases, string Topic)[] TopicAliases =
    [
        (new Regex(@"\brelease notes?\b|版本发布说明|版本说明|发行说明", Options), "发布说明"),
        (new Regex(@"\bchangelogs?\b", Options), "更新日志"),
        (new Regex(@"\bpull requests?\b|合入请求", Options), "合并请求")
    ];

    internal static MemoryObservation? Observe(string statement, MemoryScope scope)
    {
        var rule = ChineseRule.Match(statement);
        if (!rule.Success) rule = EnglishRule.Match(statement);
        if (!rule.Success) return null;
        var topic = NormalizeTopic(rule.Groups["topic"].Value);
        var action = rule.Groups["action"].Value.Trim().TrimEnd('。', '.', '！', '!');
        if ((scope == MemoryScope.Unspecified && !Teaching.IsMatch(statement) && !Instruction.IsMatch(action)) || topic.Length is < 2 or > 90 ||
            topic is "任务" or "工作" or "任何" or "所有" or "anything" or "everything" or "tasks" or "work" ||
            HasOverride(topic) || action.Length < 2) return null;
        return new(MemorySubject.TaskProcedure, scope == MemoryScope.Current ? scope : MemoryScope.Durable, action, topic);
    }

    public static string NormalizeTopic(string text)
    {
        string topic = TopicPrefix.Replace(text.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant(), "");
        return FoldAliases(topic).Trim();
    }

    public static bool Applies(MemoryObservation memory, ContinuousGoal goal)
    {
        // For open vocabulary an explicit exception can invalidate the whole practice.
        // Leave such decisions to the current task rather than guessing at an arbitrary antonym.
        if (memory.Subject != MemorySubject.TaskProcedure || !AllowsRecall(goal)) return false;
        string topic = NormalizeTopic(memory.Context);
        if (topic.Length < 2) return false;
        return goal.Requirements.Prepend(goal.Objective).Any(task => MentionsTopic(topic, task));
    }

    public static bool AllowsRecall(ContinuousGoal goal) =>
        !HasOverride(goal.Objective) && !goal.Requirements.Any(HasOverride);

    private static bool MentionsTopic(string topic, string task)
    {
        string current = FoldAliases(task.Normalize(NormalizationForm.FormKC).ToLowerInvariant());
        var phrases = Cjk.Matches(topic);
        // Keep Chinese noun phrases intact, so "发布说明" cannot be recalled solely by "发布".
        if (phrases.Any(phrase => !current.Contains(phrase.Value, StringComparison.Ordinal))) return false;
        var words = Terms(topic);
        if (words.Count == 0) return phrases.Count > 0;
        return words.IsSubsetOf(Terms(current));
    }

    private static HashSet<string> Terms(string text) => Words.Matches(text)
        .Select(word => word.Value)
        .Where(word => word is not ("the" or "an" or "a" or "of" or "for" or "our" or "to" or "and" or "in"))
        .Select(word => word.Length > 4 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
            ? word[..^1] : word)
        .ToHashSet(StringComparer.Ordinal);

    private static bool HasOverride(string text) =>
        ChineseOverrides.Any(word => text.Contains(word, StringComparison.Ordinal)) || EnglishOverride.IsMatch(text);

    private static string FoldAliases(string text)
    {
        foreach (var (aliases, topic) in TopicAliases)
            text = aliases.Replace(text, topic);
        return text;
    }
}
