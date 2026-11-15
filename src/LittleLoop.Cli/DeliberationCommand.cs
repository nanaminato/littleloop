using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using LittleLoop.Core;

internal static class DeliberationCommand
{
    public static void Run(string[] args, JsonSerializerOptions json)
    {
        if (args[0] == "deliberate")
        {
            var input = JsonSerializer.Deserialize<DeliberationInput>(File.ReadAllText(args[1]), json)
                ?? throw new JsonException("Expected input.");
            var host = CreateHost(args.Skip(2));
            Console.WriteLine(JsonSerializer.Serialize(host.Run(input), json));
        }
        else
        {
            var trace = JsonSerializer.Deserialize<DeliberationTrace>(File.ReadAllText(args[1]), json)
                ?? throw new JsonException("Expected trace.");
            var decision = DeliberationPolicy.Decide(trace.Input, trace.Responses);
            if (JsonSerializer.Serialize(decision, json) != JsonSerializer.Serialize(trace.Decision, json))
                throw new InvalidDataException("Deliberation replay mismatch.");
            Console.WriteLine(JsonSerializer.Serialize(trace with { Decision = decision }, json));
        }
    }

    internal static PluginHost CreateHost(IEnumerable<string> paths)
    {
        var host = new PluginHost();
        foreach (var path in paths)
        {
            var assembly = new PluginLoadContext(Path.GetFullPath(path)).LoadFromAssemblyPath(Path.GetFullPath(path));
            var types = assembly.GetExportedTypes().Where(t => !t.IsAbstract &&
                typeof(IDecisionPlugin).IsAssignableFrom(t)).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            if (types.Length == 0) throw new ArgumentException($"No decision plugins in {path}.");
            foreach (var type in types)
                host.Register((IDecisionPlugin)(Activator.CreateInstance(type)
                    ?? throw new ArgumentException($"Cannot construct {type.FullName}.")));
        }
        return host;
    }

    private sealed class PluginLoadContext(string path) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IDecisionPlugin).Assembly.GetName().Name)
                return typeof(IDecisionPlugin).Assembly;
            var resolved = resolver.ResolveAssemblyToPath(name);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }
    }
}
