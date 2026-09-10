using System.Globalization;
using System.Reflection;
using MailStencil;
using MailStencil.AzureBlob;
using MailStencil.FileSystem;
using Microsoft.Extensions.DependencyInjection;

var baselinePath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../eng/PublicApiBaseline.txt"));
var update = args.Contains("--update", StringComparer.Ordinal);

var assemblies = new[]
{
    typeof(ITemplateReader).Assembly,
    typeof(MailStencilScribanServiceCollectionExtensions).Assembly,
    typeof(FileSystemTemplateOptions).Assembly,
    typeof(AzureBlobTemplateOptions).Assembly
};

var actual = assemblies
    .SelectMany(GetSurface)
    .OrderBy(value => value, StringComparer.Ordinal)
    .ToArray();

if (update)
{
    Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
    File.WriteAllLines(baselinePath, actual);
    Console.WriteLine($"Updated public API baseline with {actual.Length} entries.");
    return 0;
}

var expected = File.ReadAllLines(baselinePath)
    .Where(line => line.Length > 0 && !line.StartsWith('#'))
    .OrderBy(value => value, StringComparer.Ordinal)
    .ToArray();

if (actual.SequenceEqual(expected, StringComparer.Ordinal))
{
    Console.WriteLine($"Public API baseline passed with {actual.Length} entries.");
    return 0;
}

Console.Error.WriteLine("Public API differs from eng/PublicApiBaseline.txt.");
foreach (var removed in expected.Except(actual, StringComparer.Ordinal))
    Console.Error.WriteLine($"- {removed}");
foreach (var added in actual.Except(expected, StringComparer.Ordinal))
    Console.Error.WriteLine($"+ {added}");
return 1;

static IEnumerable<string> GetSurface(Assembly assembly)
{
    foreach (var type in assembly.GetExportedTypes().OrderBy(value => value.FullName, StringComparer.Ordinal))
    {
        var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
        var modifiers = type.IsSealed ? " sealed" : type.IsAbstract && !type.IsInterface ? " abstract" : string.Empty;
        yield return $"T|{assembly.GetName().Name}|{FormatType(type)}|{kind}{modifiers}|base={FormatType(type.BaseType)}";

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            yield return $"C|{FormatType(type)}|({FormatParameters(constructor.GetParameters())})";

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var accessors = $"get={property.GetMethod?.IsPublic == true};set={property.SetMethod?.IsPublic == true}";
            yield return $"P|{FormatType(type)}|{property.Name}|{FormatType(property.PropertyType)}|{accessors}";
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(value => !value.IsSpecialName))
        {
            var generic = method.IsGenericMethodDefinition
                ? $"`{method.GetGenericArguments().Length}<{string.Join(",", method.GetGenericArguments().Select(FormatGenericParameter))}>"
                : string.Empty;
            yield return $"M|{FormatType(type)}|{(method.IsStatic ? "static " : string.Empty)}{method.Name}{generic}|({FormatParameters(method.GetParameters())})->{FormatType(method.ReturnType)}";
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var value = field.IsLiteral ? Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) : null;
            yield return $"F|{FormatType(type)}|{field.Name}|{FormatType(field.FieldType)}|const={value}";
        }
    }
}

static string FormatParameters(IEnumerable<ParameterInfo> parameters) => string.Join(",", parameters.Select(parameter =>
    $"{FormatType(parameter.ParameterType)} {parameter.Name}{(parameter.HasDefaultValue ? $"={FormatDefault(parameter.DefaultValue)}" : string.Empty)}"));

static string FormatDefault(object? value) => value switch
{
    null => "null",
    string text => $"\"{text}\"",
    bool boolean => boolean ? "true" : "false",
    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
};

static string FormatGenericParameter(Type parameter)
{
    var attributes = parameter.GenericParameterAttributes;
    var constraints = new List<string>();
    if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0) constraints.Add("class");
    if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) constraints.Add("struct");
    constraints.AddRange(parameter.GetGenericParameterConstraints().Select(FormatType));
    if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0) constraints.Add("new()");
    return $"{parameter.Name}:{string.Join("&", constraints)}";
}

static string FormatType(Type? type)
{
    if (type is null) return "null";
    if (type.IsByRef) return $"{FormatType(type.GetElementType())}&";
    if (type.IsArray) return $"{FormatType(type.GetElementType())}[]";
    if (type.IsGenericParameter) return type.Name;
    if (!type.IsGenericType) return type.FullName ?? type.Name;

    var definition = type.GetGenericTypeDefinition();
    var name = definition.FullName ?? definition.Name;
    var tick = name.IndexOf('`');
    if (tick >= 0) name = name[..tick];
    return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FormatType))}>";
}
