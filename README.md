# MailStencil

[![CI](https://github.com/polletto/MailStencil/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/polletto/MailStencil/actions/workflows/ci.yml)
[![NuGet prerelease](https://img.shields.io/nuget/vpre/MailStencil.Core.svg)](https://www.nuget.org/packages/MailStencil.Core)
[![License: MIT](https://img.shields.io/github/license/polletto/MailStencil.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)

**Storage-agnostic, strongly typed email templates for .NET.**

MailStencil is an email-template library for .NET 10. It separates
template storage, static validation, rendering, and runtime orchestration so applications can choose a
read-only FileSystem or Azure Blob source without coupling model contracts to storage.

`0.1.0-preview.1` is available on NuGet.org. This is a preview release, and APIs may still evolve before
1.0. MailStencil renders template content; it does **not** send email.

The runtime pipeline is:

```text
Retrieve -> Validate -> Cache -> Render
```

The cache reuses successfully retrieved source snapshots; validation and rendering still run for each
request.

## Packages

| Package | Purpose | NuGet |
| --- | --- | --- |
| `MailStencil.Core` | Public contracts, DI, culture fallback, and positive in-memory source caching | [NuGet.org](https://www.nuget.org/packages/MailStencil.Core) |
| `MailStencil.Scriban` | Bounded Scriban rendering and static AST validation | [NuGet.org](https://www.nuget.org/packages/MailStencil.Scriban) |
| `MailStencil.FileSystem` | Restrictive read-only UTF-8 filesystem provider | [NuGet.org](https://www.nuget.org/packages/MailStencil.FileSystem) |
| `MailStencil.AzureBlob` | Read-only Azure Blob provider using an application-owned SDK client | [NuGet.org](https://www.nuget.org/packages/MailStencil.AzureBlob) |

## Installation

Install the packages from NuGet.org. For FileSystem:

```sh
dotnet add package MailStencil.Core --version 0.1.0-preview.1
dotnet add package MailStencil.Scriban --version 0.1.0-preview.1
dotnet add package MailStencil.FileSystem --version 0.1.0-preview.1
```

For Azure Blob Storage:

```sh
dotnet add package MailStencil.Core --version 0.1.0-preview.1
dotnet add package MailStencil.Scriban --version 0.1.0-preview.1
dotnet add package MailStencil.AzureBlob --version 0.1.0-preview.1
```

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
var result = await templates.RenderAsync<OrderConfirmationModel>(
    "order-confirmation",
    new OrderConfirmationModel
    {
        CustomerName = "Ada & friends",
        OrderNumber = "MS-123",
        Total = 42.50m
    });

Console.WriteLine(result.Subject);

public sealed class OrderConfirmationModel
{
    public required string CustomerName { get; init; }
    public required string OrderNumber { get; init; }
    public decimal Total { get; init; }
}
```

```scriban
Hello {{ customer_name }}
Order {{ order_number }} totals {{ total }}
<p>Hello {{ customer_name | html.escape }}</p>
```

The declared generic model type is authoritative. MailStencil projects only its public readable
properties into detached Scriban objects, so runtime-subtype members and CLR methods are unavailable and
template assignments cannot mutate the original model.

## Validation and rendering

Resolve `ITemplateValidator` to validate unsaved content against the same declared model schema used by
the renderer:

```csharp
var result = await validator.ValidateAsync<OrderConfirmationModel>(content, cancellationToken);
if (!result.IsValid)
{
    foreach (var diagnostic in result.Diagnostics)
        Console.WriteLine($"{diagnostic.Component} {diagnostic.Code}: {diagnostic.Message}");
}
```

Invalid syntax or members produce structured diagnostics. `ITemplateRenderer` throws
`TemplateValidationException` for static validation failures and `TemplateRenderingException` for model
projection or execution failures. Cancellation remains `OperationCanceledException`.

## Localization

`IEmailTemplateService` follows `CultureInfo.Parent` from the requested or configured culture to the
default variant. For example:

```text
it-IT -> it -> default
```

Lookup culture and render formatting culture are related but distinct. Formatting keeps the original
requested culture even when template content falls back, so numbers use the requested culture. `null`
culture means invariant formatting and the default storage variant.

## Caching

Successful exact source snapshots use a five-minute absolute in-process cache by default. Set
`CacheDuration` to `TimeSpan.Zero` to disable it. Null reads, rendered output, models, diagnostics, and
exceptions are not cached. Concurrent cold requests may perform duplicate reads.

## FileSystem provider

Each exact variant is a directory containing `subject.txt` and at least one of `body.html` or `body.txt`:

```text
Templates/
  order-confirmation/
    default/
      subject.txt
      body.html
      body.txt
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

```text
mailstencil/order-confirmation/default/template.json
mailstencil/order-confirmation/it/template.json
```

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

.NET isolated Azure Functions can use the same registrations through standard Microsoft dependency
injection; MailStencil does not require an ASP.NET Core request pipeline.

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
vulnerability-reporting guidance. MailStencil is licensed under the [MIT License](LICENSE).
