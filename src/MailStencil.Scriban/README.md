# MailStencil.Scriban

Scriban 7.4.0 rendering and AST validation for declared .NET model contracts.

```csharp
services.AddMailStencil().AddScribanRenderer();

var content = new EmailTemplateContent("Hello {{ customer_name }}", textBody: "Welcome!");
var validation = await validator.ValidateAsync<CustomerModel>(content, cancellationToken);
if (validation.IsValid)
{
    var rendered = await renderer.RenderAsync(content, model, CultureInfo.InvariantCulture, cancellationToken);
}
```

Resolve `ITemplateValidator` and `ITemplateRenderer` through DI. Default naming is Scriban snake_case.
Only declared public readable properties and typed collections are projected into detached script
objects/arrays; methods and runtime subtype additions are unavailable. Validation and rendering
share one internal model schema. No storage access, fallback, caching or email sending is implemented.

The supported language is intentionally restricted: no include, eval, dynamic invocation, CLR method
access, or model mutation. MailStencil does not automatically HTML-escape values; use `html.escape` for
untrusted values inserted into an HTML body.

See the [MailStencil repository](https://github.com/polletto/MailStencil) for complete setup,
storage-provider registration, security limits, and samples.
