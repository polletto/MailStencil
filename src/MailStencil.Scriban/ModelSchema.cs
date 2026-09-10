using System.Collections;
using System.Globalization;
using System.Reflection;
using Scriban.Runtime;

namespace MailStencil.Scriban;

internal enum ShapeKind { Object, Array, String, Number, Boolean, Null, Unknown }

// The sole reflection/member-policy implementation: reusable by a future schema generator.
internal sealed class ModelShape(ShapeKind kind, Type? type = null)
{
    internal ShapeKind Kind { get; } = kind;
    internal Type? Type { get; } = type;
    internal Dictionary<string, ModelMember> Members { get; } = new(StringComparer.Ordinal);
    internal ModelShape? Element { get; set; }
    internal static readonly ModelShape String = new(ShapeKind.String);
    internal static readonly ModelShape Number = new(ShapeKind.Number);
    internal static readonly ModelShape Boolean = new(ShapeKind.Boolean);
    internal static readonly ModelShape Null = new(ShapeKind.Null);
    internal static readonly ModelShape Unknown = new(ShapeKind.Unknown);
}

internal sealed record ModelMember(PropertyInfo? Property, ModelShape Shape);

internal sealed class ModelSchema(CancellationToken cancellationToken)
{
    private readonly Dictionary<Type, ModelShape> shapes = [];
    private int memberCount;

    internal ModelShape Build(Type type, int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (shapes.TryGetValue(type, out var existing)) return existing;
        if (depth > Limits.ModelDepth || shapes.Count >= Limits.ModelNodes)
            throw new NotSupportedException("The declared model schema exceeds the complexity limit.");
        var kind = type == typeof(string) || type == typeof(char) || type.IsEnum ||
                   type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
                   type == typeof(DateOnly) || type == typeof(TimeOnly) ? ShapeKind.String :
                   type == typeof(bool) ? ShapeKind.Boolean :
                   type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? ShapeKind.Number : ShapeKind.Object;
        if (kind != ShapeKind.Object) return shapes[type] = new ModelShape(kind, type);

        if (type == typeof(object) || typeof(Delegate).IsAssignableFrom(type) ||
            typeof(Type).IsAssignableFrom(type) || typeof(MemberInfo).IsAssignableFrom(type) ||
            typeof(IQueryable).IsAssignableFrom(type) || typeof(IDictionary).IsAssignableFrom(type) ||
            type.GetInterfaces().Append(type).Any(t => t.IsGenericType &&
                (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))))
            throw new NotSupportedException($"Model type '{type.Name}' is not a supported data contract.");

        var enumerables = type.GetInterfaces().Append(type).Where(t => t.IsGenericType &&
            t.GetGenericTypeDefinition() == typeof(IEnumerable<>)).Select(t => t.GetGenericArguments()[0]).Distinct().ToArray();
        if (enumerables.Length == 1)
        {
            var array = new ModelShape(ShapeKind.Array, type);
            shapes.Add(type, array);
            array.Element = Build(enumerables[0], depth + 1);
            return array;
        }
        if (typeof(IEnumerable).IsAssignableFrom(type) || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true)
            throw new NotSupportedException($"Model type '{type.Name}' is not a supported data contract.");

        var result = new ModelShape(ShapeKind.Object, type);
        shapes.Add(type, result);
        var types = type.IsInterface ? type.GetInterfaces().Append(type) : [type];
        foreach (var property in types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                     .Distinct().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0) continue;
            if (++memberCount > Limits.ModelNodes)
                throw new NotSupportedException("The declared model schema exceeds the complexity limit.");
            var name = MemberName(property);
            if (!result.Members.TryAdd(name, new ModelMember(property, Build(property.PropertyType, depth + 1))))
                throw new NotSupportedException($"Model members collide at '{name}'.");
        }
        return result;
    }

    // A single policy seam; future configurable policies must be applied here, not in the validator.
    private static string MemberName(PropertyInfo property) => StandardMemberRenamer.Default(property);

    internal object? Project(ModelShape shape, object? value, ref int nodes, int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++nodes > Limits.ModelNodes || depth > Limits.ModelDepth)
            throw new InvalidOperationException("Model projection exceeds the node or depth limit (possibly a reference cycle).");
        if (value is null) return null;
        if (shape.Kind == ShapeKind.String)
        {
            var text = value switch
            {
                string s => s,
                DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
                TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
                Guid g => g.ToString("D"),
                char c => c.ToString(),
                Enum e => Enum.Format(shape.Type!, e, "G"),
                _ => throw new InvalidOperationException("Unsupported scalar value.")
            };
            if (text.Length > Limits.OutputLength) throw new InvalidOperationException("Model string exceeds the size limit.");
            return text;
        }
        if (shape.Kind is ShapeKind.Number or ShapeKind.Boolean) return value;
        if (shape.Kind == ShapeKind.Array)
        {
            var array = new ScriptArray();
            foreach (var item in (IEnumerable)value)
                array.Add(Project(shape.Element!, item, ref nodes, depth + 1));
            array.IsReadOnly = true;
            return array;
        }
        var result = new ScriptObject();
        foreach (var (name, member) in shape.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.SetValue(name, Project(member.Shape, member.Property!.GetValue(value), ref nodes, depth + 1), true);
        }
        result.IsReadOnly = true;
        return result;
    }
}

internal static class Limits
{
    internal const int Loop = 1000;
    internal const int ModelDepth = 32;
    internal const int ModelNodes = 10000;
    internal const int OutputLength = 1024 * 1024;
    internal const int TemplateLength = 128 * 1024;
    internal const int AstDepth = 64;
}
