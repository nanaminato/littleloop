using System.Reflection;
using System.Text.Json;
using LittleLoop.Tools;

// Independent verifier for a real CLI improvement, with no goal-specific runner configuration.
var cli = Assembly.Load("LittleLoop.Cli").GetType("DevelopmentCommand")!;
var valid = cli.GetMethod("Valid", BindingFlags.Static | BindingFlags.Public)!;
var run = cli.GetMethod("Run", BindingFlags.Static | BindingFlags.Public)!;
void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); Console.WriteLine("PASS " + reason); }
bool Accept(params string[] input) => (bool)valid.Invoke(null, [input])!;
Check(Accept("dev-map", "policy.json", "src", "12"), "dev-map accepts optional prefix and page offset");
Check(Accept("dev-map", "policy.json") && !Accept("dev-map", "policy.json", "src"), "original form preserved and incomplete pagination rejected");
var source = Path.GetFullPath(".");
// Policy is a verifier fixture for a read-only command; no process will be dispatched.
string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
string policyFile = Path.Combine(source, ".littleloop-temp", "map-policy.json");
Directory.CreateDirectory(Path.GetDirectoryName(policyFile)!);
var policy = new DevelopmentPolicy(source, [], [new("unused", executable, [], true, ["tests/Project.Checks/Program.cs"])], [".cs", ".csproj"], MaxFileBytes: 262144);
File.WriteAllText(policyFile, JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
var output = new StringWriter(); var previous = Console.Out;
try { Console.SetOut(output); await (Task)run.Invoke(null, [new[] { "dev-map", policyFile, "src/LittleLoop.Core", "2" }, options])!; }
finally { Console.SetOut(previous); }
using var document = JsonDocument.Parse(output.ToString());
Check(document.RootElement.GetProperty("offset").GetInt32() == 2, "requested offset is forwarded to structure inspection");
Check(document.RootElement.GetProperty("entries").EnumerateArray().All(e => e.GetProperty("path").GetString()!.StartsWith("src/LittleLoop.Core")), "requested prefix filters actual project source");
bool rejected = false;
try { await (Task)run.Invoke(null, [new[] { "dev-map", policyFile, "src", "-1" }, options])!; }
catch (Exception) { rejected = true; }
Check(rejected, "negative offset rejected without execution");
Console.WriteLine("Discovery project checks passed.");
