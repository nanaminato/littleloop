using System.Text.RegularExpressions;

namespace LittleLoop.Core;

/// <summary>Ranks already scoped path hints. Exact matches work for every language. For a paraphrase,
/// require compatible actions, identifiers and subject matter before measuring lexical overlap.</summary>
internal static class ExperienceSimilarity
{
    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Negation = Pattern(@"不|别|勿|禁止|避免|排除|仅限|除了|\b(?:no|not|never|without|except|avoid|exclude|skip)\b|n't\b");
    private static readonly Regex Literal = Pattern(@"[a-z0-9_/-]+(?:\.[a-z0-9_/-]+)+|[a-z_][a-z0-9_.-]*=[a-z0-9_.-]+|[0-9]+|[""“‘'\u0060][^""”’'\u0060\n]{1,120}[""”’'\u0060]");
    private static readonly Regex Words = Pattern(@"[a-z0-9_]+|[\p{IsCJKUnifiedIdeographs}]+");
    private static readonly Regex Filler = Pattern(
        @"请帮我|麻烦你|帮我|请你|请|一下|一个|这个|的问题|的故障|问题|故障|错误|出现|发生|的时候|时的|时|的|进行|一下子|把|将|将其|并且|\b(?:please|could|would|you|the|a|an|issue|problem|bug|error|when|while|with|during|in|on|of|for|to|is|that|this)\b");

    // Action equivalence is narrower than term overlap. In particular, review/change and enable/disable
    // can refer to exactly the same files and still be different tasks.
    private static readonly (string Key, Regex Aliases)[] Actions =
    [
        ("fix", Pattern(@"修复|修正|修好|排除故障|解决|\b(?:fix|repair|correct|resolve)\b")),
        ("change", Pattern(@"修改|更新|改为|改成|更改|调整|替换|\b(?:update|change|replace|modify)\b")),
        ("add", Pattern(@"新增|新建|增加|添加|创建|\b(?:add|create|introduce)\b")),
        ("remove", Pattern(@"移除|去掉|去除|删除|清除|清理|\b(?:remove|delete|strip|trim|clean)\b")),
        ("copy", Pattern(@"复制|拷贝|另存|\b(?:copy|duplicate)\b")),
        ("move", Pattern(@"迁移|移动|搬运|搬过去|移到|\b(?:move|migrate|transfer)\b")),
        ("check", Pattern(@"检查|核查|核对|排查|验证|审阅|\b(?:check|inspect|review|verify|investigate)\b")),
        ("enable", Pattern(@"开启|打开|启用|\b(?:enable|activate)\b")),
        ("disable", Pattern(@"关闭|禁用|停用|\b(?:disable|deactivate)\b"))
    ];

    private static readonly (string Key, Regex Aliases)[] Concepts =
    [
        ("login", Pattern(@"登录|登陆|\b(?:log(?:in|ging in| in)|sign[ -]?in|authentication)\b")),
        ("logout", Pattern(@"登出|注销登录|退出登录|\b(?:logout|log out|sign out)\b")),
        ("timeout", Pattern(@"超时|\b(?:timeout|time[- ]?out|timed out|times out|timing out)\b")),
        ("notification", Pattern(@"通知|提醒|\b(?:notifications?|alerts?)\b")),
        ("draft", Pattern(@"样稿|文稿|草稿|稿件|\b(?:drafts?|manuscripts?)\b")),
        ("example", Pattern(@"示例|样例|范例|\b(?:examples?|samples?)\b")),
        ("text", Pattern(@"文本|文字|文案|\b(?:text|wording|copywriting)\b")),
        ("page", Pattern(@"页面|网页|\b(?:pages?|webpages?)\b")),
        ("banner", Pattern(@"横幅|\bbanners?\b")),
        ("status", Pattern(@"状态|标志|\b(?:status|state|flag)\b")),
        ("test", Pattern(@"测试|用例|\btests?\b"))
    ];

    private sealed record Features(HashSet<string> Actions, HashSet<string> Concepts, HashSet<string> Terms);

    public static int Score(ContinuousGoal prior, ContinuousGoal current)
    {
        if (!prior.Requirements.SequenceEqual(current.Requirements, StringComparer.Ordinal)) return 0;
        string first = prior.Objective.Trim(), second = current.Objective.Trim();
        if (first.Length == 0 || second.Length == 0) return 0;
        if (first == second) return 100;
        if (first.Length > 4000 || second.Length > 4000 ||
            Negation.IsMatch(first) || Negation.IsMatch(second) ||
            ConversationMemoryPolicy.FormattingRequest(first) != ConversationMemoryPolicy.FormattingRequest(second)) return 0;
        // Literals, including path spelling/case and order, cannot be supplied by a fuzzy match.
        if (!Literal.Matches(first).Select(m => m.Value).SequenceEqual(Literal.Matches(second).Select(m => m.Value),
                StringComparer.Ordinal)) return 0;

        var a = Analyze(first);
        var b = Analyze(second);
        if (a.Actions.Count == 0 || !a.Actions.SetEquals(b.Actions) || !a.Concepts.SetEquals(b.Concepts)) return 0;
        if (a.Concepts.Count == 0 && a.Terms.Count < 2) return 0;

        int shared = a.Terms.Count(b.Terms.Contains);
        int largest = Math.Max(a.Terms.Count, b.Terms.Count);
        // Unknown terms may be distinct components or constraints. Allow lexical variation only where
        // there is independent evidence (two recognized subject concepts and substantial term overlap).
        if (!a.Terms.SetEquals(b.Terms) && (a.Concepts.Count < 2 || shared * 5 < largest * 4)) return 0;
        return 70 + Math.Min(20, a.Concepts.Count * 5) + (largest == 0 ? 5 : 9 * shared / largest);
    }

    private static Features Analyze(string text)
    {
        string remaining = text.ToLowerInvariant();
        var actions = Extract(Actions, ref remaining);
        var concepts = Extract(Concepts, ref remaining);
        remaining = Filler.Replace(remaining, " ");
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match word in Words.Matches(remaining))
        {
            string term = word.Value;
            if (char.IsAscii(term[0]) || term.Length == 1) terms.Add(term);
            else
                for (int i = 0; i < term.Length - 1; i++) terms.Add(term.Substring(i, 2));
        }
        return new(actions, concepts, terms);
    }

    private static HashSet<string> Extract((string Key, Regex Aliases)[] vocabulary, ref string remaining)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, aliases) in vocabulary)
        {
            if (!aliases.IsMatch(remaining)) continue;
            keys.Add(key);
            remaining = aliases.Replace(remaining, " ");
        }
        return keys;
    }
}
