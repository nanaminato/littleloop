using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

// Protected acceptance tests for the real LittleLoop CLI feature. No source-text assertions.
string policy = Path.GetFullPath(args[0]);
string dotnet = args[1];
var assembly = Assembly.Load("LittleLoop.Cli");
var valid = assembly.GetType("DevelopmentCommand")!.GetMethod("Valid", BindingFlags.Public | BindingFlags.Static)!;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
bool Accept(params string[] input) => (bool)valid.Invoke(null, [input])!;
Check(Accept("dev-map", policy), "dev-map is registered with exactly one policy argument");
Check(!Accept() && !Accept("dev-map") && !Accept("dev-map", policy, "extra"), "invalid and empty argument lists are rejected safely");
Check(Accept("dev-run", "state", "policy", "model", "id") && Accept("dev-answer", "state", "id", "answer"), "existing development commands stay valid");
async Task<(int Code, string Out, string Error)> Run(params string[] input)
{
    var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(assembly.Location); foreach (var item in input) start.ArgumentList.Add(item);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(); return (process.ExitCode, await output, await error);
}
var result = await Run("dev-map", policy);
Check(result.Code == 0, "dev-map executes without state or model: " + result.Error);
using var document = JsonDocument.Parse(result.Out);
var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToArray();
Check(entries.Any(e => e.GetProperty("path").GetString()!.EndsWith(".csproj")), "map exposes real project manifests");
Check(entries.All(e => !e.GetProperty("path").GetString()!.StartsWith('.')), "map respects hidden-file exclusions");
var help = await Run();
Check(help.Code == 2 && help.Error.Contains("dev-map"), "usage documents the new command");
var invalid = await Run("dev-map", policy + ".missing");
Check(invalid.Code != 0, "missing policy fails without successful output");
Check(Accept("dev-map", policy, "src", "2"), "map pagination arguments accepted");
var paged = await Run("dev-map", policy, "src/LittleLoop.Core", "2");
using var page = JsonDocument.Parse(paged.Out);
Check(paged.Code == 0 && page.RootElement.GetProperty("offset").GetInt32() == 2 &&
    page.RootElement.GetProperty("entries").EnumerateArray().All(e => e.GetProperty("path").GetString()!.StartsWith("src/LittleLoop.Core")),
    "map prefix and offset reach actual project inspection");
Check((await Run("dev-map", policy, "src", "-1")).Code != 0, "negative map page is rejected");
Console.WriteLine("Repository acceptance tests passed.");
