using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LlmSandboxApi.Mcp;

/// <summary>Rejects a tool call whose argument names don't match the tool's input schema, naming what each offending
/// level accepts. Without it the SDK drops an unknown name silently (a misnamed search filter lists everything) and
/// reports a missing required one only as an opaque "An error occurred invoking". Copy-paste pattern across the MCP
/// hosts; LupiraGeoApi holds the reference copy and its tests.</summary>
public static class StrictToolArguments
{
    private const int MaxDepth = 32;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, ct) => context.MatchedPrimitive is McpServerTool tool
            && Check(tool.ProtocolTool.Name, tool.ProtocolTool.InputSchema, context.Params?.Arguments) is { } error
                ? ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error }] })
                : next(context, ct);

    /// <summary>The rejection message, or null when every name matches. Top-level names are ordinal (the SDK's
    /// argument dictionary); nested ones case-insensitive (System.Text.Json web defaults), so nothing the binder
    /// would accept is rejected. Anything the walker doesn't understand passes.</summary>
    internal static string? Check(string toolName, JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        var walker = new Walker(inputSchema);
        if (walker.Resolve(inputSchema) is { } root && StrictProperties(root) is { } props)
        {
            var given = (arguments ?? new Dictionary<string, JsonElement>()).Select(a => (a.Key, a.Value));
            walker.Object(root, props, given, path: string.Empty, schemaPath: string.Empty, StringComparer.Ordinal, depth: 0);
        }

        return walker.Problems.Count == 0
            ? null
            : $"Invalid arguments for '{toolName}': {string.Join("; ", walker.Problems)}. {string.Join(" ", walker.Accepts)}";
    }

    /// <summary>The declared properties of an object schema that admits no others; null for free-form objects
    /// (dictionaries, extension data, JsonNode metadata).</summary>
    private static JsonElement? StrictProperties(JsonElement schema) =>
        schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
        && (!schema.TryGetProperty("additionalProperties", out var extra) || extra.ValueKind == JsonValueKind.False)
            ? props
            : null;

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private sealed class Walker(JsonElement root)
    {
        private readonly HashSet<string> _reported = [];

        public List<string> Problems { get; } = [];

        public List<string> Accepts { get; } = [];

        public void Object(JsonElement schema, JsonElement props, IEnumerable<(string Name, JsonElement Value)> given,
            string path, string schemaPath, StringComparer comparer, int depth)
        {
            var declared = new Dictionary<string, (string Name, JsonElement Schema)>(comparer);
            foreach (var p in props.EnumerateObject()) declared.TryAdd(p.Name, (p.Name, p.Value));
            List<string> required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
                ? [.. req.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!)]
                : [];

            var seen = new HashSet<string>(comparer);
            var bad = false;
            foreach (var (name, value) in given)
            {
                seen.Add(name);
                if (declared.TryGetValue(name, out var d))
                {
                    Value(d.Schema, value, Join(path, name), Join(schemaPath, d.Name), depth + 1);
                }
                else
                {
                    Problems.Add($"unknown {Join(path, name)}");
                    bad = true;
                }
            }

            foreach (var r in required.Where(r => !seen.Contains(r)))
            {
                Problems.Add($"missing required {Join(path, r)}");
                bad = true;
            }

            if (!bad || !_reported.Add(schemaPath)) return;
            var names = declared.Values.Select(d => required.Contains(d.Name) ? $"{d.Name} (required)" : d.Name).ToList();
            var list = names.Count == 0 ? "none" : string.Join(", ", names);
            Accepts.Add(schemaPath.Length == 0 ? $"Accepts: {list}." : $"At {schemaPath}: accepts {list}.");
        }

        public JsonElement? Resolve(JsonElement schema)
        {
            for (var hops = 0; hops < MaxDepth; hops++)
            {
                if (schema.ValueKind != JsonValueKind.Object) return null;
                if (schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("oneOf", out _) || schema.TryGetProperty("allOf", out _))
                    return null;
                if (!schema.TryGetProperty("$ref", out var r)) return schema;
                if (r.GetString() is not { } pointer || Pointer(pointer) is not { } target) return null;
                schema = target;
            }

            return null;
        }

        private void Value(JsonElement schema, JsonElement value, string path, string schemaPath, int depth)
        {
            if (depth > MaxDepth || Resolve(schema) is not { } s) return;
            if (value.ValueKind == JsonValueKind.Object && StrictProperties(s) is { } props)
            {
                Object(s, props, value.EnumerateObject().Select(p => (p.Name, p.Value)), path, schemaPath,
                    StringComparer.OrdinalIgnoreCase, depth);
            }
            else if (value.ValueKind == JsonValueKind.Array && s.TryGetProperty("items", out var items))
            {
                var i = 0;
                foreach (var item in value.EnumerateArray()) Value(items, item, $"{path}[{i++}]", $"{schemaPath}[]", depth + 1);
            }
        }

        private JsonElement? Pointer(string pointer)
        {
            if (pointer != "#" && !pointer.StartsWith("#/", StringComparison.Ordinal)) return null;
            var node = root;
            string[] segments = pointer.Length <= 2 ? [] : pointer[2..].Split('/');
            foreach (var raw in segments)
            {
                var segment = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
                if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out var child)) node = child;
                else if (node.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var i) && i < node.GetArrayLength()) node = node[i];
                else return null;
            }

            return node;
        }
    }
}
