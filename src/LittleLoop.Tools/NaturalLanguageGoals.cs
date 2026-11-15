using System.Globalization;
using System.Text.RegularExpressions;
using LittleLoop.Core;

namespace LittleLoop.Tools;

/// <summary>Conservative bilingual document-domain grammar. Unconsumed clauses require clarification.</summary>
public sealed class DocumentGoalInterpreter : IGoalInterpreter
{
    public const string Version = "document-language/1";
    private const string Token = @"§\d+§";
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public GoalUnderstanding Interpret(GoalDialogue? previous, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8192) throw new ArgumentException("Provide 1–8192 characters of goal text.");
        if (previous?.Turns.Length >= 64) throw new ArgumentException("Dialogue turn limit reached; start a new goal.");
        var last = previous?.Turns.LastOrDefault()?.Understanding;
        string? answering = last?.Questions.FirstOrDefault()?.Field;
        // Unrecognized instructions are never silently dropped: the user must replace the whole request.
        var fields = last is null || answering == "request" ? new Dictionary<string, string[]>() :
            last.Fields.ToDictionary(k => k.Key, v => v.Value.ToArray());
        var evidence = last?.Evidence.ToList() ?? [];
        var questions = new List<GoalQuestion>();
        int turn = previous?.Turns.Length ?? 0;
        var literals = new List<string>();
        string work = Rx("\"([^\"]*)\"|'([^']*)'|“([^”]*)”|「([^」]*)」|`([^`]*)`").Replace(text, m =>
        {
            var value = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
            literals.Add(value);
            return "§" + (literals.Count - 1) + "§";
        });
        // Unquoted paths must have an extension; quote paths with spaces or unusual punctuation.
        work = Rx(@"[A-Za-z0-9_./\\-]+\.(?:txt|md|log|csv|json|yaml|yml)(?![A-Za-z0-9_.])").Replace(work, m =>
        { literals.Add(m.Value); return "§" + (literals.Count - 1) + "§"; });
        string Literal(string token) => literals[int.Parse(token.Trim('§'), CultureInfo.InvariantCulture)];
        string Expand(string value) => Rx(Token).Replace(value, m => Literal(m.Value));
        void Set(string field, string value, string quote, bool append = false)
        {
            fields[field] = append ? fields.GetValueOrDefault(field, []).Append(value).Distinct(StringComparer.Ordinal).ToArray() : [value];
            var expanded = Expand(quote);
            evidence.Add(new(field, value, turn, text.Contains(expanded, StringComparison.Ordinal) ? expanded : text));
        }
        void Consume(string pattern, Action<Match>? apply = null) => work = Rx(pattern).Replace(work, m => { apply?.Invoke(m); return " "; });

        Consume(@"(?:复制|copy)\s*(?<source>" + Token + @")\s*(?:到|to)\s*(?<output>" + Token + ")", m =>
        {
            AssignUnique("source", Literal(m.Groups["source"].Value), m.Value);
            AssignUnique("output", Literal(m.Groups["output"].Value), m.Value);
            Set("mode", "preserve", m.Value);
        });
        Consume(@"(?:内容(?:是|为)?\s*[:：]?|文本(?:是|为)\s*[:：]?|(?:fallback\s+)?content\s*(?:is|:)?\s*)\s*(?<value>" + Token + ")",
            m => Set("content", Literal(m.Groups["value"].Value), m.Value));
        Consume(@"(?:保存(?:为|到)|另存(?:为|到)|输出(?:到|为)|目标(?:文件)?(?:是|为)?\s*[:：]?|(?:save|write)\s+(?:it\s+)?(?:as|to)|output(?:\s+(?:is|to))?\s*[:：]?)\s*(?<value>" + Token + ")",
            m => AssignUnique("output", Literal(m.Groups["value"].Value), m.Value));
        Consume(@"(?:备用(?:来源|文件)?(?:是|为)?|失败(?:后|时)?(?:改用|使用)|fallback(?:\s+(?:source|to))?)\s*[:：]?\s*(?<value>" + Token + ")",
            m => Set("fallback", Literal(m.Groups["value"].Value), m.Value, true));
        Consume(@"(?:源文件(?:是|为)?|输入(?:文件)?(?:是|为)?|来源(?:是|为)?|source(?:\s+(?:file|is))?|from)\s*[:：]?\s*(?<value>" + Token + ")",
            m => AssignUnique("source", Literal(m.Groups["value"].Value), m.Value));

        var modes = evidence.Where(e => e.Turn == turn && e.Field == "mode").Select(e => e.Value).ToHashSet();
        Consume(@"不要规范化|不规范化|不改变格式|保留(?:原始)?格式|保持原样|原样(?:复制)?|do\s+not\s+normalize|preserve\s+(?:the\s+)?(?:original\s+)?(?:format|layout)|unchanged",
            m => { modes.Add("preserve"); evidence.Add(new("mode", "preserve", turn, m.Value)); });
        bool lines = false, trim = false;
        Consume(@"统一换行(?:符)?|(?:normalize|unify)\s+line\s+endings", m => { lines = true; evidence.Add(new("transform", "line-endings", turn, m.Value)); });
        Consume(@"(?:去掉|去除|删除|清理)行尾(?:空白|空格)|trim\s+trailing\s+(?:whitespace|spaces)", m => { trim = true; evidence.Add(new("transform", "trim-lines", turn, m.Value)); });
        Consume(@"规范化(?:文本|格式)?|normalize(?:\s+(?:the\s+)?text)?", m => { modes.Add("normalize"); evidence.Add(new("mode", "normalize", turn, m.Value)); });
        Consume(@"复制|copy", m => { if (modes.Count == 0 && !lines && !trim) { modes.Add("preserve"); evidence.Add(new("mode", "preserve", turn, m.Value)); } });
        if (lines && trim) modes.Add("normalize");
        else if ((lines || trim) && !modes.Contains("normalize"))
            questions.Add(new("mode", "partial-transform", "现有规范化工具会同时统一换行为 LF 并去掉行尾空白。你要规范化，还是保留格式？若需要单独变换，请重新描述任务，当前工具尚不支持。"));
        if (modes.Count > 1)
        { fields.Remove("mode"); questions.Add(new("mode", "conflict", "规范化会改变换行与行尾空白，和保留原始格式冲突。你希望规范化，还是保留格式？")); }
        else if (modes.Count == 1) fields["mode"] = [modes.Single()];

        Consume(@"(?:不要|禁止|不能|不允许|不)覆盖(?:已有|现有)?(?:文件)?|(?:do\s+not|never)\s+overwrite(?:\s+(?:existing\s+)?files?)?",
            m => Set("overwrite", "false", m.Value));
        Consume(@"(?:不允许|不能|禁止|不要)另存(?:为其他文件)?|(?:do\s+not|never)\s+rename", m => AssignUnique("alternate", "false", m.Value));
        Consume(@"(?:如果(?:目标)?文件(?:已)?存在(?:时)?|if\s+(?:the\s+)?(?:target\s+)?file\s+exists)\s*[,，]?\s*(?:允许|可以|allow|can)\s*(?:另存(?:为其他文件)?|rename)",
            m => AssignUnique("alternate", "true", m.Value));
        Consume(@"(?:允许|可以)另存(?:为其他文件)?|(?:另存为其他文件)|(?:allow|can)\s+rename", m => AssignUnique("alternate", "true", m.Value));
        Consume(@"(?:最多(?:使用)?|预算(?:是|为)?\s*[:：]?|at\s+most|budget\s*[:：]?)\s*(?<n>\d+|[一二三四五六七八九十两]+)\s*(?:次(?:工具(?:调用)?)?|tool\s+calls?)",
            m => AssignUnique("budget", Number(m.Groups["n"].Value).ToString(CultureInfo.InvariantCulture), m.Value));
        Consume(@"(?:最多)?(?:重试|重新规划|重规划)\s*(?<n>\d+|[零一二三四五六七八九十两]+)\s*次|(?:retry|replan)\s*(?<n>\d+)\s*times?",
            m => AssignUnique("replans", Number(m.Groups["n"].Value).ToString(CultureInfo.InvariantCulture), m.Value));
        Consume(@"不(?:要)?重试|不要重新规划|no\s+retries", m => Set("replans", "0", m.Value));
        Consume(@"(?:读回|验证)(?:结果|输出|文件)?|verify(?:\s+(?:the\s+)?(?:result|output|file))?", m => Set("verify", "true", m.Value));

        // A bare answer is grounded in the persisted question, not guessed from file position.
        var remainingTokens = Rx(Token).Matches(work).Cast<Match>().ToArray();
        if (remainingTokens.Length == 1 && answering is "source" or "output")
        {
            Set(answering, Literal(remainingTokens[0].Value), remainingTokens[0].Value);
            work = work.Replace(remainingTokens[0].Value, " ", StringComparison.Ordinal);
        }
        else if (remainingTokens.Length == 1 && !fields.ContainsKey("source"))
        {
            Set("source", Literal(remainingTokens[0].Value), remainingTokens[0].Value);
            work = work.Replace(remainingTokens[0].Value, " ", StringComparison.Ordinal);
        }

        // Only grammatical glue is discarded. Unknown verbs, negatives, conditions and limits survive.
        Consume(@"请(?:帮我)?|帮我|麻烦|我想(?:要)?|把|将|然后|并且|并|再|后|的|一下|整理|处理|文本文件|文件|\b(?:please|the|a|an|and|then|it|file|text|to)\b");
        string residual = Rx(@"[\s，。；、,;.!?！？:：()]+").Replace(work, " ").Trim();
        if (residual.Length > 0)
            questions.Insert(0, new("request", "unrecognized", "还有未理解或不支持的要求：“" + Expand(residual) + "”。请重新描述完整目标。目前能规范化或原样复制 UTF-8 文本到新文件；不会忽略这部分要求直接执行。"));
        if (last is not null && answering != "request")
            foreach (var pending in last.Questions.Where(q => q.Reason is "conflict" or "partial-transform"))
                if (!evidence.Any(e => e.Turn == turn && e.Field == pending.Field) && !questions.Any(q => q.Field == pending.Field))
                    questions.Add(pending);
        foreach (var key in new[] { "source", "output" })
            if (fields.TryGetValue(key, out var paths) && paths.Any(p => string.IsNullOrWhiteSpace(p) || p.Any(char.IsControl) || Path.IsPathRooted(p) || p.Contains(':') || p.Replace('\\', '/').Split('/').Any(c => c.StartsWith('.') || c.Length == 0)))
                questions.Add(new(key, "invalid-path", "请提供工作区内的相对文件路径，不要使用绝对路径或 ..；有空格的路径请加引号。"));
        if (!fields.ContainsKey("source") && !fields.ContainsKey("content"))
            questions.Add(new("source", "missing", "要处理哪个文件？请给工作区内的相对路径，也可以回答：内容是“要保存的文本”。"));
        if (!fields.ContainsKey("mode") && !questions.Any(q => q.Field == "mode"))
            questions.Add(new("mode", "missing", "你希望规范化（统一 LF 换行并去掉行尾空白），还是保留格式原样复制？"));
        if (!fields.ContainsKey("output")) questions.Add(new("output", "missing", "结果保存到哪个新文件？请给工作区内的相对路径。"));
        CheckNumber("budget", 1, 1000, "工具调用预算必须在 1 到 1000 之间，请用‘最多使用 6 次工具’这样的描述补充。");
        CheckNumber("replans", 0, 32, "重规划次数必须在 0 到 32 之间，请用‘重试 2 次’这样的描述补充。");
        if (fields.TryGetValue("source", out var source) && fields.TryGetValue("output", out var output) &&
            source.Any(s => string.Equals(s.Replace('\\', '/'), output[0].Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            questions.Add(new("output", "same-path", "输入和输出是同一个文件，工具不覆盖源文件。请指定另一个输出文件。"));
        return new(Version, fields, questions.DistinctBy(q => q.Field).ToArray(), evidence.ToArray());

        void AssignUnique(string field, string value, string quote)
        {
            if (evidence.Any(e => e.Turn == turn && e.Field == field && e.Value != value))
                questions.Add(new(field, "conflict", $"同一条消息中的 {field} 有多个不同要求，请明确一个值。"));
            Set(field, value, quote);
        }
        void CheckNumber(string field, int min, int max, string question)
        {
            if (fields.TryGetValue(field, out var values) && (!int.TryParse(values[0], out int n) || n < min || n > max))
                questions.Add(new(field, "out-of-range", question));
        }
    }
    private static int Number(string value)
    {
        if (int.TryParse(value, out int number)) return number;
        if (value.All(char.IsAsciiDigit)) return -1;
        string digits = "零一二三四五六七八九";
        value = value.Replace('两', '二');
        if (value.Length == 1 && value != "十") return digits.IndexOf(value[0]);
        if (value == "十") return 10;
        if (value.StartsWith('十') && value.Length == 2) return 10 + digits.IndexOf(value[1]);
        if (value.Length is 2 or 3 && value[1] == '十') return digits.IndexOf(value[0]) * 10 + (value.Length == 3 ? digits.IndexOf(value[2]) : 0);
        return -1;
    }
}

public static class NaturalLanguageGoals
{
    public static GoalDialogue Say(LearningSession session, string id, string text)
    {
        var state = session.Read();
        var previous = state.Dialogues.SingleOrDefault(d => d.Id == id);
        if (previous?.GoalId is not null) throw new ArgumentException("This conversation already submitted a goal; use a new ID.");
        var understanding = new DocumentGoalInterpreter().Interpret(previous, text);
        return Submit(session, id, text, state, previous, understanding);
    }

    public static async Task<GoalDialogue> SayAsync(LearningSession session, string id, string text,
        IAsyncGoalInterpreter interpreter, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8192) throw new ArgumentException("Provide 1–8192 characters of goal text.");
        var state = session.Read();
        var previous = state.Dialogues.SingleOrDefault(d => d.Id == id);
        if (previous?.GoalId is not null || previous?.Turns.Length >= 64) throw new ArgumentException("Conversation is submitted or full.");
        var understanding = await interpreter.InterpretAsync(previous, text, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Submit(session, id, text, state, previous, understanding);
    }

    private static GoalDialogue Submit(LearningSession session, string id, string text, LearningState state,
        GoalDialogue? previous, GoalUnderstanding understanding)
    {
        var dialogue = new GoalDialogue(id, [.. previous?.Turns ?? [], new(text, previous?.Turns[^1].Understanding.Questions.FirstOrDefault()?.Field, understanding)], null);
        if (understanding.Questions.Length > 0) return session.SaveDialogue(dialogue, state.Revision);
        var f = understanding.Fields;
        string Value(string key, string fallback) => f.TryGetValue(key, out var values) ? values[0] : fallback;
        var request = new DocumentGoal("language:" + id, string.Join("\n", dialogue.Turns.Select(t => t.Text)), f["output"][0],
            [.. f.GetValueOrDefault("source", []), .. f.GetValueOrDefault("fallback", [])],
            f.GetValueOrDefault("content")?.FirstOrDefault(), f["mode"][0] == "normalize",
            int.Parse(Value("budget", "12"), CultureInfo.InvariantCulture), int.Parse(Value("replans", "3"), CultureInfo.InvariantCulture), Value("alternate", "false") == "true");
        var (goal, facts, resources) = DocumentGoals.Compile(request);
        return session.SaveDialogue(dialogue with { GoalId = goal.Id }, state.Revision, goal, facts, resources);
    }
}
