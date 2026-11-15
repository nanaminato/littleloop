using System.Text.Json;
using LittleLoop.Tools;

internal static class ProjectDevelopmentCommand
{
    public static bool Valid(string[] args) => args.Length > 0 && args[0] switch
    {
        "dev-project" => args.Length is 3 or 4 or 5,
        "dev-project-resume" or "dev-project-show" or "dev-project-retry" => args.Length == 2,
        "dev-permit" => args.Length == 4 && args[3] is "approve" or "deny",
        _ => false
    };
    public static async Task Run(string[] args)
    {
        if (args[0] == "dev-project")
        {
            string model = args.Length >= 4 ? Path.GetFullPath(args[3]) : Path.Combine(
                LocalModelSecrets.FindProjectDirectory(AppContext.BaseDirectory) ?? throw new ArgumentException("Specify a model config from an installed project."), "examples", "models", "deepseek.json");
            string directory = args.Length == 5 ? Path.GetFullPath(args[4]) : Path.Combine(Path.GetTempPath(), "LittleLoop", "projects", Guid.NewGuid().ToString("N"));
            var host = new ProjectDevelopmentHost(directory);
            host.Create(args[1], args[2], model);
            Console.WriteLine("会话目录：" + directory); Show(host); return;
        }
        var existing = new ProjectDevelopmentHost(args[1]);
        if (args[0] == "dev-project-retry")
        {
            var session = new LittleLoop.Core.LearningSession(existing.StatePath);
            session.RetryDevelopmentPlanning("project", session.Read().Revision);
            Console.WriteLine("规划重试已记录，权限、预算和历史未重置；使用 dev-project-resume 继续。"); return;
        }
        if (args[0] == "dev-project-show") { Show(existing); return; }
        if (args[0] == "dev-permit")
        {
            existing.Decide(args[2], args[3] == "approve");
            Console.WriteLine("权限决定已记录；未执行命令。使用 dev-project-resume 继续。"); return;
        }
        var job = existing.Read();
        if (job.Status != "approved") { Show(existing); return; }
        var config = JsonSerializer.Deserialize<SemanticModelConfig>(File.ReadAllText(job.ModelFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var client = new HttpSemanticModelClient(config, projectDirectory: LocalModelSecrets.FindProjectDirectory(Path.GetDirectoryName(job.ModelFile)!));
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); }; Console.CancelKeyPress += cancel;
        try
        {
            var result = await existing.ResumeAsync(client, config.MaxInputCharacters, config.MaxObservationCharacters, cancellation.Token);
            Console.WriteLine($"{result.Status}: {result.Detail}");
            if (result.PendingPermission is not null) Show(existing);
            else if (result.Status == LittleLoop.Core.GoalStatus.AwaitingClarification)
                Console.WriteLine($"回答：dev-answer \"{existing.StatePath}\" project \"回答内容\"，然后恢复项目会话。");
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    private static void Show(ProjectDevelopmentHost host)
    {
        var job = host.Read();
        Console.WriteLine(JsonSerializer.Serialize(job, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        string? id = job.Status == "awaiting-permission" ? job.RequestId : null;
        if (File.Exists(host.StatePath))
        {
            var run = new LittleLoop.Core.LearningSession(host.StatePath).Read().DevelopmentRuns.SingleOrDefault(r => r.Id == "project");
            if (run?.PendingPermission is { } request)
            {
                Console.WriteLine(JsonSerializer.Serialize(request)); id = request.Id;
                if (request.Kind == "command") Console.WriteLine(JsonSerializer.Serialize(job.Discovery.ExtraCommands.Single(c => c.Id == request.Resource)));
            }
        }
        if (id is not null)
        {
            Console.WriteLine("需要明确授权：项目源码将发送给所选模型，允许列出的文件修改和固定命令。项目命令可能访问网络或项目外文件，当前不是 OS 沙箱。依赖恢复另行申请。");
            Console.WriteLine($"dev-permit \"{host.DirectoryPath}\" {id} approve|deny");
        }
    }
}
