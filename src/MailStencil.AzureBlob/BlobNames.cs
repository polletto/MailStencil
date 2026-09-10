namespace MailStencil.AzureBlob;

internal static class BlobNames
{
    internal static bool ValidSegment(string value) => value.Length is >= 1 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static bool ValidContainer(string? value) => value is { Length: >= 3 and <= 63 } &&
        value[0] != '-' && value[^1] != '-' && !value.Contains("--", StringComparison.Ordinal) &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    internal static string NormalizePrefix(string value) => string.Join('/', value.Split('/', StringSplitOptions.RemoveEmptyEntries));

    internal static bool ValidPrefix(string? value) => value is not null && NormalizePrefix(value).Length <= 512 &&
        value.Split('/', StringSplitOptions.RemoveEmptyEntries).All(ValidSegment);

    internal static string Map(string prefix, TemplateRequest request)
    {
        if (!ValidSegment(request.Name)) throw new ArgumentException("Template name must be a safe 1–128 character ASCII segment.", nameof(request));
        if (request.Culture is not null && (!ValidSegment(request.Culture) || request.Culture.Equals("default", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Template culture must be a safe segment; 'default' is reserved for null culture.", nameof(request));
        return (prefix.Length == 0 ? "" : prefix + "/") + request.Name + "/" + (request.Culture ?? "default") + "/template.json";
    }
}
