using System.Text;
using System.Text.Json;

namespace MailStencil.AzureBlob;

internal static class BlobDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static EmailTemplateContent Parse(ReadOnlyMemory<byte> bytes, TemplateRequest request)
    {
        try
        {
            if (bytes.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
            // JsonDocument may defer UTF-8 decoding; validate all bytes without allocating a decoded copy.
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("Template document must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? subject = null, html = null, text = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid("Duplicate template document property.");
                switch (property.Name)
                {
                    case "formatVersion":
                        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var version) || version != 1)
                            throw Invalid("Only integer formatVersion 1 is supported.");
                        break;
                    case "subject": subject = StringValue(property.Value, false); break;
                    case "htmlBody": html = StringValue(property.Value, true); break;
                    case "textBody": text = StringValue(property.Value, true); break;
                    default: throw Invalid("Unknown template document property.");
                }
            }
            if (!seen.Contains("formatVersion") || subject is null || (html is null && text is null))
                throw Invalid("Template requires formatVersion, a string subject and at least one non-null body.");
            return new EmailTemplateContent(subject, html, text);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new AzureBlobTemplateException(request, "Template blob is not a valid UTF-8 JSON document.", exception);
        }

        AzureBlobTemplateException Invalid(string message) => new(request, message);
        string? StringValue(JsonElement value, bool nullable)
        {
            if (nullable && value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String) throw Invalid("Template component must be a JSON string (bodies may be null).");
            return value.GetString();
        }
    }
}
