using System.Text.RegularExpressions;

namespace LittleLoop.Core;

public enum MemoryScope { Unspecified, Current, Durable }
public enum MemorySubject { CompletionNotice, DocumentWhitespace, TaskProcedure }

/// <summary>A proposed meaning. The host owns its source, lifetime, confirmation and visibility.</summary>
public sealed record MemoryObservation(MemorySubject Subject, MemoryScope Scope, string Value, string Context);

public static class ConversationMemoryPolicy
{
    private const string Trailing = @"(?:(?:每行末尾|行末尾|行末|行尾|尾随)(?:的)?(?:多余的?)?(?:空格|空白)|trailing (?:white ?space|spaces?))";
    private const string KeepVerb = @"(?:保留|保有|保持|留下|preserve|keep|retain)";
    private const string TrimVerb = @"(?:清理|清除|清空|去掉|去除|删除|移除|剔除|修剪|trim|remove|strip)";
    private const string NormalizeVerb = @"(?:规范化|标准化|normalize|normalise)";
    private const string Negative = @"(?:不需要|无须|不要|不应|不用|无需|不可|不能|不必|不得|不|别|do not |don't |don’t |never )";
    private const string KeepMode = @"(?:原样(?:保留|搬过去|复制|搬运|拷贝)?|保持原样|保留(?:原始|原有|原)?格式|不改变格式|as[- ]is|verbatim|unchanged|preserve (?:the |original )*(?:format|formatting))";
    private const string Meaningful = @"(?:有意义|有用|很重要|是有意义的|必须保留|有语义|matters?|(?:is |are )(?:meaningful|significant|important))";
    private const string DocumentNoun = @"(?:markdown(?: 文档| 文件|文档|文件| documents?| files?)?|草稿|样稿|文稿|稿件|稿子|文档|文本(?:文件)?|drafts?|manuscripts?|documents?|text files?)";

    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex Discussion = Pattern(
        @"^(?:新?任务\s*[:：]|补充(?:要求)?\s*[:：]|/(?:task|revise)\b|(?:task|example|quote)\s*:|假如|假设|比如|例如|如果|请问|请解释|有人说|他说|她说|用户说|你说|我说过|what if\b|suppose\b|if\b|he said\b|she said\b|you said\b|for example\b|should\b|could you explain\b)");
    private static readonly Regex Intro = Pattern(@"^(?:我希望|我想要|我想让你|请记住|请注意|更正一下|纠正一下|提醒一下|请|please\b|i'd like you to\b|i want you to\b|correction\b)[\s,，：:]*");
    private static readonly Regex ScopeMarker = Pattern(
        @"^(?:(?<future>从现在起|从今以后|今后|以后|之后都|往后|通常|每次|一直|默认|我(?:通常|一般|平时)喜欢|我(?:更)?偏好|from now on\b|going forward\b|in future\b|in the future\b|by default\b|always\b|i prefer\b)|(?<current>仅这次|就这次|这一次|这次|本次|这回|这个任务|当前任务|just this time\b|this time(?: only)?\b|for this (?:task|one)\b|for now\b))[\s,，：:]*");
    private static readonly Regex EndScope = Pattern(@"\s*,?\s*(?<scope>from now on|by default|this time only|just this time|this time)$");
    private static readonly Regex NoticeFrame = Pattern(
        @"^(?:(?:(?:任务)?(?:完成|做完|结束)(?:以后|之后|后|时|了)?(?:的)?(?:通知|汇报|回复|报告)?|完成通知|通知|给我的通知|汇报|回复|when (?:you're |you are )?(?:done|finished)|(?:in |for )?(?:the |your )?(?:completion (?:notice|notification|message)|final (?:report|update|message)))\s*[,，：:]?\s*)");
    private static readonly Regex Brief = Pattern(
        @"^(?:(?:你)?(?:就|请)?(?:只(?:要|需要|用|需)?|直接|仅需)?(?:告诉我|说|给我|汇报|报|发我)(?:最终|最后的)?结果(?:就(?:好|行|够)(?:了)?)?|(?:只要|只给|只说|只看)(?:最终)?结果(?:就(?:好|行|够)(?:了)?)?|" +
        @"(?:请|要|再|尽量)?(?:简洁|简短|精简|短)(?:一些|一点|些|点)?(?:就好|就行)?|(?:有点|太)(?:长|啰嗦)(?:了)?|" +
        @"(?:不要|不用|别)(?:附上|附带|重复|重述)(?:原来的|原始|原)?(?:任务)?目标|(?:please |just |only )*(?:tell|send|give) me (?:just |only )?(?:the )?(?:result|outcome)s?(?: only)?|" +
        @"(?:please )?keep (?:it|the (?:completion )?(?:message|notification|notice)) (?:brief|short|concise)|(?:brief|short|concise))$");
    private static readonly Regex WithGoal = Pattern(
        @"^(?:(?:请|也请|也|记得|要|应当|都)?(?:附上|带上|附带|包含|加上|含|重复|列出|写上)(?:原来的|原始|原)?(?:任务)?目标|(?:please )?(?:include|add|repeat) (?:the )?(?:original )?(?:task )?(?:goal|objective))$");
    private static readonly Regex DocumentPrefix = Pattern(
        @"^(?:处理|对于|遇到|对|在|when handling |when processing |for |in )?(?<qualifier>这类|此类|这类的|这种|这些|这一类|本类|这份|这个|这篇|当前|所有|每份|每个|every |all |these |such |this )?(?<noun>" + DocumentNoun + @")(?:的时候|时|中|里|内)?(?:的)?\s*[,，：:]?\s*");
    private static readonly Regex ActionPrefix = Pattern(@"^(?:请|应该|应当|需要|要|都要|都|务必|应|please )\s*");
    private static readonly Regex KeepForm = Pattern(@"^(?:" + KeepMode + "|" + Negative + @"\s*(?:" + TrimVerb + @"\s*(?:the )?" + Trailing + "|" + NormalizeVerb + @")|" +
        KeepVerb + @"\s*(?:the )?" + Trailing + "|" + Trailing + @"\s*(?:是|都要|要)?\s*(?:" + Meaningful + "|" + KeepVerb + @"))$");
    private static readonly Regex TrimForm = Pattern(@"^(?:" + NormalizeVerb + @"(?:格式|文本)?|" + TrimVerb + @"\s*(?:the )?" + Trailing + "|" + Trailing + @"\s*(?:都要|要|应该|应当|需要)\s*" + TrimVerb + @")$");
    private static readonly Regex IgnoreTrim = Pattern(Negative + @"\s*(?:" + TrimVerb + @"(?:掉)?\s*(?:the )?" + Trailing + "|" + NormalizeVerb + ")");
    private static readonly Regex IgnoreKeep = Pattern(Negative + @"\s*(?:" + KeepVerb + @"\s*(?:the )?" + Trailing + "|" + KeepMode + ")");
    private static readonly Regex KeepInGoal = Pattern(KeepMode + "|" + KeepVerb + @"\s*(?:the )?" + Trailing + "|" + Trailing + @"\s*" + Meaningful);
    private static readonly Regex TrimInGoal = Pattern(TrimVerb + @"\s*(?:the )?" + Trailing + "|" + NormalizeVerb);
    private static readonly Regex TextOperation = Pattern(
        @"原样|复制|拷贝|搬运|搬过去|保留|清理|去掉|去除|清除|整理|排版|规范化|迁移|转存|编辑|修改|更新|保存|誊写|\b(?:copy|move|clean|trim|strip|remove|normalize|normalise|edit|update|format|reformat|preserve|save|transfer)\b");
    private static readonly Regex Draft = Pattern(@"样稿|文稿|草稿|稿件|稿子|\b(?:drafts?|manuscripts?)\b");
    private static readonly Regex Markdown = Pattern(@"markdown|\.md(?:$|[^a-z0-9_])");
    private static readonly Regex Document = Pattern(@"文档|文本|说明书|说明文件|手册|\.txt(?:$|[^a-z0-9_])|\b(?:documents?|text|manuals?)\b");

    public static bool IsStandaloneStatement(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000) return false;
        text = text.Trim().TrimEnd('.', '。', '！', '!');
        return text.IndexOfAny(['\n', '\r', '?', '？', '"', '“', '”', '「', '」', '『', '』', '\u0060', '>']) < 0 &&
            !text.EndsWith('吗') && !text.EndsWith('么') && !text.EndsWith('呢') && !Discussion.IsMatch(text);
    }

    public static MemoryObservation? Observe(string text)
    {
        if (!IsStandaloneStatement(text)) return null;
        string phrase = text.Trim().TrimEnd('。', '.', '！', '!').TrimEnd();
        var scope = MemoryScope.Unspecified;
        var end = EndScope.Match(phrase);
        if (end.Success)
        {
            scope = end.Groups["scope"].Value.Contains("this time", StringComparison.OrdinalIgnoreCase)
                ? MemoryScope.Current : MemoryScope.Durable;
            phrase = phrase[..end.Index].TrimEnd();
        }
        while (phrase.Length != 0)
        {
            var intro = Intro.Match(phrase);
            if (intro.Success) { phrase = phrase[intro.Length..]; continue; }
            var marker = ScopeMarker.Match(phrase);
            if (!marker.Success) break;
            var next = marker.Groups["future"].Success ? MemoryScope.Durable : MemoryScope.Current;
            if (scope != MemoryScope.Unspecified && scope != next) return null;
            scope = next;
            phrase = phrase[marker.Length..];
        }

        var frame = NoticeFrame.Match(phrase);
        string content = phrase[frame.Length..].TrimStart();
        if (Brief.IsMatch(content) && (frame.Success || HasResultWord(content)))
            return new(MemorySubject.CompletionNotice, scope, "brief", "completion");
        if (WithGoal.IsMatch(content) && frame.Success)
            return new(MemorySubject.CompletionNotice, scope, "with-goal", "completion");

        var document = DocumentPrefix.Match(phrase);
        if (document.Success)
        {
            var directive = ActionPrefix.Replace(phrase[document.Length..], "", 1);
            string? value = KeepForm.IsMatch(directive) ? "preserve" : TrimForm.IsMatch(directive) ? "trim" : null;
            if (value is not null)
            {
                string qualifier = document.Groups["qualifier"].Value.Trim().ToLowerInvariant();
                if (scope == MemoryScope.Unspecified)
                    scope = qualifier switch
                    {
                        "这份" or "这个" or "这篇" or "当前" or "this" => MemoryScope.Current,
                        "这类" or "此类" or "这类的" or "这种" or "这些" or "这一类" or "本类" or
                            "所有" or "每份" or "每个" or "every" or "all" or "these" or "such" => MemoryScope.Durable,
                        _ => MemoryScope.Unspecified
                    };
                return new(MemorySubject.DocumentWhitespace, scope, value, DocumentContext(document.Groups["noun"].Value)!);
            }
        }
        return ConditionalProcedurePolicy.Observe(phrase, scope);
    }

    private static bool HasResultWord(string text) =>
        text.Contains("结果", StringComparison.Ordinal) || text.Contains("result", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("outcome", StringComparison.OrdinalIgnoreCase);

    private static string? DocumentContext(string text) => Markdown.IsMatch(text) ? "markdown" :
        Draft.IsMatch(text) ? "draft" : Document.IsMatch(text) ? "document" : null;

    [Flags] internal enum Formatting { None = 0, Preserve = 1, Trim = 2 }

    internal static Formatting FormattingRequest(string text)
    {
        // Consume negative clauses first. "Don't trim" must not also count as "trim".
        bool preserve = IgnoreTrim.IsMatch(text);
        bool trim = IgnoreKeep.IsMatch(text);
        string rest = IgnoreTrim.Replace(IgnoreKeep.Replace(text, " "), " ");
        var result = Formatting.None;
        if (preserve || KeepInGoal.IsMatch(rest)) result |= Formatting.Preserve;
        if (trim || TrimInGoal.IsMatch(rest)) result |= Formatting.Trim;
        return result;
    }

    public static bool Applies(MemoryObservation memory, ContinuousGoal goal)
    {
        if (memory.Subject == MemorySubject.TaskProcedure) return ConditionalProcedurePolicy.Applies(memory, goal);
        if (memory.Subject != MemorySubject.DocumentWhitespace || memory.Value is not ("preserve" or "trim")) return false;
        string text = string.Join("\n", goal.Requirements.Prepend(goal.Objective));
        string? context = DocumentContext(goal.Objective);
        if (context is null || (memory.Context != "document" && context != memory.Context) || !TextOperation.IsMatch(text)) return false;
        var opposite = memory.Value == "preserve" ? Formatting.Trim : Formatting.Preserve;
        return (FormattingRequest(text) & opposite) == 0;
    }

    public static int ExperienceScore(ContinuousGoal prior, ContinuousGoal current) =>
        ExperienceSimilarity.Score(prior, current);
}
