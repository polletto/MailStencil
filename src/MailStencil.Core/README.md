# MailStencil.Core

Core contracts and runtime orchestration for storage-agnostic, strongly typed email templates on .NET 10.

```csharp
services.AddMailStencil(options =>
{
    options.DefaultCulture = "en-US";
    options.CacheDuration = TimeSpan.FromMinutes(5);
});
```

Applications compose one `ITemplateReader` with an `ITemplateRenderer`. The runtime performs culture
fallback through `CultureInfo.Parent`, caches successful source snapshots for an absolute duration, and
keeps reading, rendering, validation, and storage responsibilities separate. This package does not send
email and contains no storage provider or template engine.

MailStencil does not automatically sanitize rendered content or email headers. Applications must escape
untrusted HTML values and enforce the header policy required by their email transport.
