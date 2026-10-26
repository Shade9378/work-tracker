using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using ResearchTracker.Application;
using ResearchTracker.Persistence;

namespace ResearchTracker.Cli;

public static class EntryPoint
{
    private sealed class UsageException(string message) : Exception(message);
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var remaining = args.ToList(); var root = Extract(remaining, "--data") ?? WorkspaceLease.DefaultRoot;
            var readOnly = remaining.Remove("--read-only");
            if (remaining.Count == 1 && remaining[0] == "--version") { await output.WriteLineAsync("Work Tracker 1.7.5"); return 0; }
            if (remaining.Count == 0 || remaining[0] is "help" or "--help" || remaining.Contains("--help"))
            {
                remaining.RemoveAll(a => a is "help" or "--help"); await output.WriteLineAsync(Help(remaining)); return 0;
            }
            var commands = new WorkspaceCommands(Path.GetFullPath(root), readOnly);
            if (remaining[0] == "mcp")
            {
                if (remaining.Count != 1) throw new UsageException("mcp accepts only --data and --read-only. It speaks Model Context Protocol over stdio.");
                await RunMcpAsync(commands, readOnly); return 0;
            }
            if (remaining.Count < 2) throw new UsageException("Use work-tracker <group> <command>. Run work-tracker --help for commands.");
            var name = remaining[0] + "_" + remaining[1]; remaining.RemoveRange(0, 2);
            var method = WorkspaceCommands.Catalog.SingleOrDefault(m => m.Name == name) ?? throw new UsageException("Unknown command: " + name.Replace('_', ' '));
            var parameters = ParseParameters(remaining, method);
            var result = method.Invoke(commands, parameters);
            await output.WriteLineAsync(JsonSerializer.Serialize(result, WorkspaceCommands.Json)); return 0;
        }
        catch (Exception ex)
        {
            if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
            var code = ex is UsageException ? 2 : ex is IOException && ex.Message.StartsWith("Workspace is busy") ? 3 : 1;
            await error.WriteLineAsync(JsonSerializer.Serialize(new { error = ex.Message, exitCode = code }, WorkspaceCommands.Json)); return code;
        }
    }
    private static string? Extract(List<string> args, string flag)
    {
        var matches = args.Select((v, i) => (v, i)).Where(x => x.v == flag || x.v.StartsWith(flag + "=", StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length > 1) throw new UsageException("Duplicate option: " + flag);
        var (value, index) = matches[0];
        if (value.Contains('=')) { args.RemoveAt(index); return value[(flag.Length + 1)..]; }
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--")) throw new UsageException("Missing value for " + flag);
        var result = args[index + 1]; args.RemoveRange(index, 2); return result;
    }
    private static string Normalize(string value) => value.Replace("-", "").Replace("_", "").ToLowerInvariant();
    private static object?[] ParseParameters(List<string> args, MethodInfo method)
    {
        var supplied = new Dictionary<string, JsonElement>(); var parameters = method.GetParameters();
        var argsFile = Extract(args, "--args-file"); var argsJson = Extract(args, "--args-json");
        if (argsFile != null && argsJson != null) throw new UsageException("Choose --args-file or --args-json, not both.");
        if (argsFile != null || argsJson != null)
        {
            using var document = JsonDocument.Parse(argsFile == null ? argsJson! : File.ReadAllText(argsFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new UsageException("Arguments JSON must be an object.");
            foreach (var property in document.RootElement.EnumerateObject()) if (!supplied.TryAdd(Normalize(property.Name), property.Value.Clone())) throw new UsageException("Duplicate argument: " + property.Name);
        }
        while (args.Count > 0)
        {
            var flag = args[0]; args.RemoveAt(0); if (!flag.StartsWith("--")) throw new UsageException("Expected a named --option, got " + flag);
            var parts = flag[2..].Split('=', 2); var key = Normalize(parts[0]);
            var parameter = parameters.SingleOrDefault(p => Normalize(p.Name!) == key) ?? throw new UsageException("Unknown option: --" + parts[0]);
            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            string value;
            if (parts.Length == 2) value = parts[1];
            else if (type == typeof(bool) && (args.Count == 0 || args[0].StartsWith("--"))) value = "true";
            else { if (args.Count == 0 || args[0].StartsWith("--")) throw new UsageException("Missing value for --" + parts[0]); value = args[0]; args.RemoveAt(0); }
            JsonElement element;
            try { element = type == typeof(string) || type == typeof(Guid) ? JsonSerializer.SerializeToElement(value) : JsonDocument.Parse(value).RootElement.Clone(); }
            catch (JsonException) { throw new UsageException("Invalid JSON value for --" + parts[0]); }
            if (!supplied.TryAdd(key, element)) throw new UsageException("Duplicate argument: " + parts[0]);
        }
        foreach (var key in supplied.Keys) if (!parameters.Any(p => Normalize(p.Name!) == key)) throw new UsageException("Unknown argument: " + key);
        return parameters.Select(p =>
        {
            if (!supplied.TryGetValue(Normalize(p.Name!), out var value)) return p.HasDefaultValue ? p.DefaultValue : throw new UsageException("Required option: --" + p.Name);
            if (value.ValueKind == JsonValueKind.Null && new NullabilityInfoContext().Create(p).ReadState == NullabilityState.NotNull) throw new UsageException("Null is invalid for --" + p.Name);
            if (value.ValueKind == JsonValueKind.Null && Nullable.GetUnderlyingType(p.ParameterType) == null && p.ParameterType != typeof(string) && !p.ParameterType.IsArray) throw new UsageException("Null is invalid for --" + p.Name);
            try { return value.Deserialize(p.ParameterType, WorkspaceCommands.Json); }
            catch (Exception ex) when (ex is JsonException or FormatException) { throw new UsageException("Invalid value for --" + p.Name + ": " + ex.Message); }
        }).ToArray();
    }
    public static string Help(IReadOnlyList<string> parts)
    {
        var command = string.Join('_', parts); var methods = WorkspaceCommands.Catalog.Where(m => command.Length == 0 || m.Name == command || parts.Count == 1 && m.Name.StartsWith(command + "_", StringComparison.Ordinal)).ToArray();
        if (methods.Length == 0) throw new UsageException("Unknown command: " + string.Join(' ', parts));
        var lines = new List<string> { "Work Tracker — offline research tracker", "Usage: work-tracker [--data PATH] [--read-only] <group> <command> [--option VALUE]", "       work-tracker [--data PATH] [--read-only] mcp", "Output: JSON to stdout; errors to stderr. Exit codes: 0 success, 1 operation error, 2 usage error, 3 workspace busy.", "Use --args-file PATH or --args-json JSON for complex arguments. Boolean flags can omit their true value.", "" };
        foreach (var method in methods)
        {
            var metadata = method.GetCustomAttribute<CommandAttribute>()!;
            lines.Add(method.Name.Replace('_', ' ') + (metadata.ReadOnly ? " [read]" : " [write]") + " — " + method.GetCustomAttribute<DescriptionAttribute>()?.Description);
            foreach (var p in method.GetParameters()) lines.Add($"  --{p.Name} ({(Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType).Name})" + (p.HasDefaultValue ? $" optional; default={p.DefaultValue ?? "null"}" : " required"));
        }
        return string.Join(Environment.NewLine, lines);
    }
    public static async Task RunMcpAsync(WorkspaceCommands commands, bool readOnly)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(WorkspaceCommands.Catalog
            .Where(m => !readOnly || m.GetCustomAttribute<CommandAttribute>()!.ReadOnly)
            .Select(method =>
            {
                var metadata = method.GetCustomAttribute<CommandAttribute>()!;
                return new ToolWithErrors(McpServerTool.Create(method, commands, new McpServerToolCreateOptions
                {
                    Name = "work_tracker_" + method.Name, Description = method.GetCustomAttribute<DescriptionAttribute>()!.Description,
                    ReadOnly = metadata.ReadOnly, Destructive = metadata.Destructive, OpenWorld = false,
                    UseStructuredContent = true, OutputSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = true }), SerializerOptions = WorkspaceCommands.Json
                }));
            }));
        using var host = builder.Build(); await host.RunAsync();
    }
    private sealed class ToolWithErrors(McpServerTool inner) : DelegatingMcpServerTool(inner)
    {
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            try { return await base.InvokeAsync(request, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException { InnerException: { } actual }) ex = actual;
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
            }
        }
    }
}
