# MailStencil

MailStencil is a storage-agnostic, strongly typed email-template library for .NET 10. It separates
template storage, static validation, rendering, and runtime orchestration so applications can choose a
read-only FileSystem or Azure Blob source without coupling model contracts to storage.

The `0.1.0-preview.1` packages are a release candidate and have not been published to NuGet.org.
MailStencil renders template content; it does not send email.

## Packages

| Package | Purpose |
| --- | --- |
| `MailStencil.Core` | Public contracts, DI, culture fallback, and positive in-memory source caching |
| `MailStencil.Scriban` | Bounded Scriban rendering and static AST validation |
| `MailStencil.FileSystem` | Restrictive read-only UTF-8 filesystem provider |
| `MailStencil.AzureBlob` | Read-only Azure Blob provider using an application-owned SDK client |

For a FileSystem application, install:

```sh
dotnet add package MailStencil.Core --version 0.1.0-preview.1
dotnet add package MailStencil.Scriban --version 0.1.0-preview.1
dotnet add package MailStencil.FileSystem --version 0.1.0-preview.1
```

Use `MailStencil.AzureBlob` instead of `MailStencil.FileSystem` for Azure Blob Storage. These commands
require a local feed containing the release-candidate packages until they are published.

## Quick start

Templates use Scriban's snake_case names. This example reads
`templates/order-confirmation/default/subject.txt` and `body.html` or `body.txt`:

```csharp
using MailStencil;
using Microsoft.Extensions.DependencyInjection;

using var provider = new ServiceCollection()
    .AddMailStencil(options =>
    {
        options.DefaultCulture = "en-US";
        options.CacheDuration = TimeSpan.FromMinutes(5);
    })
    .AddScribanRenderer()
    .AddFileSystemTemplateReader(options => options.BasePath = "templates")
    .BuildServiceProvider();

var templates = provider.GetRequiredService<IEmailTemplateService>();
var result = await templates.RenderAsync(
    "order-confirmation",
    new OrderModel("MS-123", "Ada & friends"));

Console.WriteLine(result.Subject);

public sealed record OrderModel(string OrderNumber, string CustomerName);
```

```scriban
Order {{ order_number }} for {{ customer_name }}
```

The declared generic model type is authoritative. MailStencil projects only its public readable
properties into detached Scriban objects, so runtime-subtype members and CLR methods are unavailable and
template assignments cannot mutate the original model.

## Validation and rendering

Resolve `ITemplateValidator` to validate unsaved content against the same declared model schema used by
the renderer:

```csharp
var result = await validator.ValidateAsync<OrderModel>(content, cancellationToken);
if (!result.IsValid)
{
    foreach (var diagnostic in result.Diagnostics)
        Console.WriteLine($"{diagnostic.Component} {diagnostic.Code}: {diagnostic.Message}");
}
```

Invalid syntax or members produce structured diagnostics. `ITemplateRenderer` throws
`TemplateValidationException` for static validation failures and `TemplateRenderingException` for model
projection or execution failures. Cancellation remains `OperationCanceledException`.

## Localization and caching

`IEmailTemplateService` follows `CultureInfo.Parent` from the requested or configured culture to the
default variant. Formatting keeps the original requested culture even when content falls back. `null`
culture means invariant formatting and the default storage variant.

Successful exact source snapshots use a five-minute absolute in-process cache by default. Set
`CacheDuration` to `TimeSpan.Zero` to disable it. Null reads, rendered output, models, diagnostics, and
exceptions are not cached. Concurrent cold requests may perform duplicate reads.

## FileSystem provider

Each exact variant is a directory containing `subject.txt` and at least one of `body.html` or `body.txt`:

```text
templates/order-confirmation/default/subject.txt
templates/order-confirmation/default/body.html
templates/order-confirmation/it/body.txt
```

The provider enforces bounded UTF-8 reads, restrictive identifiers, exact-case lookup, containment, and
link/reparse rejection. The base tree must be application-controlled. Portable path metadata checks are
not an OS-handle-level sandbox and cannot guarantee a transactional snapshot against hostile writers,
hard links, mount changes, or every filesystem race.

## Azure Blob provider

The application creates and owns `BlobServiceClient`, including its credentials, endpoint, transport, and
retry policy:

```csharp
services.AddSingleton(blobServiceClient);
services.AddMailStencil()
    .AddScribanRenderer()
    .AddAzureBlobTemplateReader(options =>
    {
        options.ContainerName = "email-templates";
        options.Prefix = "mailstencil";
    });
```

Each exact variant is one blob at `<prefix>/<name>/<culture-or-default>/template.json`:

```json
{
  "formatVersion": 1,
  "subject": "Order {{ order_number }} confirmed",
  "htmlBody": "<p>Hello {{ customer_name | html.escape }}</p>",
  "textBody": "Hello {{ customer_name }}"
}
```

The JSON parser rejects unknown or duplicate fields and unsupported format versions. Only Azure's exact
`BlobNotFound` response becomes a missing template; other Azure SDK failures propagate.

## Security and logging

MailStencil bounds template source, AST depth, loops, projection depth/nodes, output, filesystem parts,
and Azure blobs. Scriban include, eval, dynamic invocation, CLR member access, and model mutation are not
enabled. Application model getters and enumerables are trusted application code and may still execute
side effects or block. For truly hostile workloads, use process or OS isolation.

MailStencil does **not** automatically sanitize or HTML-escape model values. Use `html.escape` for
untrusted values inserted into `HtmlBody`. Rendered subjects are returned verbatim, including CR/LF; the
email sender must enforce its own header-injection rules.

Library warnings are fixed and sanitized. Debug logs may contain logical template name, culture, and
version. Template/model/rendered content, absolute paths, blob URIs, credentials, diagnostics, and
exception objects are excluded from MailStencil-generated logs.

## Documentation and samples

- [Public API](docs/public-api.md)
- [Architecture, limits, and threat model](docs/architecture.md)
- [ASP.NET Core usage](docs/aspnet-core.md)
- [Azurite integration tests](docs/azurite-testing.md)
- [Console sample](samples/MailStencil.Sample.Console)
- [ASP.NET Core sample](samples/MailStencil.Sample.AspNetCore)

Build with the SDK selected by `global.json`:

```sh
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --no-restore
```

## Current limitations

MailStencil targets .NET 10 and currently provides read-only runtime providers. Writer/Authoring APIs,
logical version management, distributed caching, S3, Google storage providers, CLI tooling, automatic
HTML escaping, trimming guarantees, and Native AOT guarantees are outside this preview.

See [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes and [SECURITY.md](SECURITY.md) for
vulnerability-reporting guidance. A project license and public repository URL have not yet been selected;
both must be resolved before publication.
