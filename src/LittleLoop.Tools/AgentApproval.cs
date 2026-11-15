using System.Text.Json;
using LittleLoop.Core;

namespace LittleLoop.Tools;

public sealed record AgentCommandGrant(string Command, string Cwd);
public sealed record AgentHostPolicy(string Executable, string Project, string ApprovalMode = "auto", string OutsideRule = "user",
    AgentCommandGrant[]? Commands = null, int MaxTurns = 4, int TimeoutSeconds = 600, int MaxRepairs = 1, bool AttachDocumentation = true, PersistentRole? Role = null,
    DevelopmentPolicy? Development = null, bool HostToolsOnly = false);
public sealed record AgentApprovalRequest(string Nonce, string Method, JsonElement RequestId, JsonElement Parameters, string Status);

public static class AgentApproval
{
    public static string Decide(AgentHostPolicy policy, string thread, string turn, string method, JsonElement p)
    {
        if (!p.TryGetProperty("threadId", out var t) || t.GetString() != thread ||
            !p.TryGetProperty("turnId", out var u) || u.GetString() != turn) return "decline";
        if (policy.ApprovalMode != "auto") return policy.ApprovalMode;
        if (method == "item/commandExecution/requestApproval" && p.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String &&
            (!p.TryGetProperty("kind", out var kind) || kind.GetString() == "command") &&
            p.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String &&
            (!p.TryGetProperty("networkApprovalContext", out var network) || network.ValueKind == JsonValueKind.Null) &&
            (!p.TryGetProperty("additionalPermissions", out var additional) || additional.ValueKind == JsonValueKind.Null) &&
            (policy.Commands ?? []).Any(g => g.Command == c.GetString() && ProjectFiles.Same(Path.GetFullPath(g.Cwd), Path.GetFullPath(cwd.GetString()!))))
            return "accept";
        return policy.OutsideRule;
    }
    public static object Response(string method, JsonElement parameters, bool accept) => method switch
    {
        "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" => new { decision = accept ? "accept" : "decline" },
        "item/permissions/requestApproval" => new { permissions = accept ? parameters.GetProperty("permissions").Clone() : JsonSerializer.SerializeToElement(new { }), scope = "turn" },
        _ => throw new ArgumentException("Unsupported approval protocol; do not synthesize grants.")
    };
    public static bool Supported(string method) => method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval";
}
