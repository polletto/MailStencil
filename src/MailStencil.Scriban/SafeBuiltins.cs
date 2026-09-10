using Scriban.Functions;
using Scriban.Runtime;

namespace MailStencil.Scriban;

internal sealed record BuiltinSignature(int MinArguments, int MaxArguments, ModelShape Result);

internal static class SafeBuiltins
{
    // One allowlist supplies both the runtime and the validator. No eval, includes, reflection,
    // regex, object mutation, clocks or environment-dependent functions are imported.
    internal static readonly IReadOnlyDictionary<string, BuiltinSignature> Functions =
        new Dictionary<string, BuiltinSignature>(StringComparer.Ordinal)
        {
            ["string.upcase"] = new(1, 1, ModelShape.String),
            ["string.downcase"] = new(1, 1, ModelShape.String),
            ["string.capitalize"] = new(1, 1, ModelShape.String),
            ["string.strip"] = new(1, 1, ModelShape.String),
            ["string.size"] = new(1, 1, ModelShape.Number),
            ["string.contains"] = new(2, 2, ModelShape.Boolean),
            ["string.replace"] = new(3, 3, ModelShape.String),
            ["html.escape"] = new(1, 1, ModelShape.String),
            ["array.size"] = new(1, 1, ModelShape.Number),
            ["math.abs"] = new(1, 1, ModelShape.Number),
            ["math.round"] = new(1, 2, ModelShape.Number)
        };

    internal static bool IsReserved(string name) => name is "string" or "html" or "array" or "math" or "for";

    internal static ScriptObject Create()
    {
        var defaults = new BuiltinFunctions();
        var result = new ScriptObject();
        foreach (var group in Functions.Keys.GroupBy(k => k.Split('.')[0]))
        {
            var source = (ScriptObject)defaults[group.Key]!;
            var functions = new ScriptObject();
            foreach (var key in group)
            {
                var member = key.Split('.')[1];
                functions.SetValue(member, source[member], true);
            }
            functions.IsReadOnly = true;
            result.SetValue(group.Key, functions, true);
        }
        result.IsReadOnly = true;
        return result;
    }
}
