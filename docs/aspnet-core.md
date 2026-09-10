# ASP.NET Core usage

The working .NET 10 minimal API sample uses the existing IEmailTemplateService. It has one fixed
preview endpoint and fixed example customer/order data; it is not an email sender or Authoring UI.

## FileSystem registration and configuration

```csharp
builder.Services.AddMailStencil().AddScribanRenderer()
    .AddFileSystemTemplateReader(_ => { });
builder.Services.AddOptions<MailStencilOptions>()
    .Bind(builder.Configuration.GetSection("MailStencil")).ValidateOnStart();
builder.Services.AddOptions<FileSystemTemplateOptions>()
    .Bind(builder.Configuration.GetSection("MailStencilFileSystem"))
    .PostConfigure(o =>
    {
        if (!string.IsNullOrWhiteSpace(o.BasePath))
            o.BasePath = Path.GetFullPath(o.BasePath, builder.Environment.ContentRootPath);
    }).ValidateOnStart();
```

```json
{
  "MailStencil": {
    "DefaultCulture": "en-US",
    "CacheDuration": "00:05:00"
  },
  "MailStencilFileSystem": { "BasePath": "Templates" }
}
```

The sample copies its Templates directory to output and publish directories. Relative paths are
application-relative. Core and provider options use their existing validators. ValidateOnStart is
an application opt-in: bad configuration fails host startup without reading files or contacting Azure.
This does not check credentials, connectivity, container existence, or template availability.
Singletons snapshot options; runtime configuration reload does not retarget existing readers/caches.
ValidateScopes/ValidateOnBuild can additionally catch invalid dependency lifetimes. In particular,
custom dependencies injected into the singleton service must be suitable for concurrent singleton use.

## Azure alternative

Replace the FileSystem reader registration and FileSystem options binding; register only one reader:

```csharp
// Application-created client, using the application's credential and SDK retry settings.
builder.Services.AddSingleton(blobServiceClient);
builder.Services.AddMailStencil().AddScribanRenderer()
    .AddAzureBlobTemplateReader(_ => { });
builder.Services.AddOptions<MailStencilOptions>()
    .Bind(builder.Configuration.GetSection("MailStencil")).ValidateOnStart();
builder.Services.AddOptions<AzureBlobTemplateOptions>()
    .Bind(builder.Configuration.GetSection("MailStencilAzureBlob")).ValidateOnStart();
```

```json
{
  "MailStencilAzureBlob": {
    "ContainerName": "email-templates",
    "Prefix": "mailstencil",
    "MaxTemplateBlobSize": 4194304
  }
}
```

For example, an application that references Azure.Identity can construct that client using
new BlobServiceClient(serviceUri, new DefaultAzureCredential()). Managed identity and credential
selection remain application responsibilities. Neither MailStencil.AzureBlob nor the default sample
requires Azure.Identity or real cloud credentials. Do not put connection strings or credentials in
committed configuration. See [the Azure storage format](architecture.md#azure-blob-provider) for the
single JSON blob required at mailstencil/order-confirmation/default/template.json.

Reader registrations may appear before or after Core/Scriban/client registrations, but all dependencies
must exist before resolution. Both providers reject a previously registered reader. There is no
provider-routing abstraction and no startup network request.

## Preview, rendering and errors

The endpoint resolves IEmailTemplateService directly and calls RenderAsync with OrderConfirmationModel.
Its CustomerName, OrderNumber and Total properties map to customer_name, order_number and total.
Only the optional culture query is caller-controlled. The sample returns JSON rather than directly
serving the generated HTML as a webpage.

- No culture parameter: configured en-US attempts en-US, en, default.
- culture=it-IT: attempts it-IT, then finds the supplied it template.
- culture=fr-CA: reaches default; decimal formatting remains fr-CA.
- Empty culture: explicit invariant/default request.
- Core caches successful sources for five minutes; CacheDuration zero disables caching.

MailStencil does not automatically sanitize or HTML-escape model values. Untrusted values inserted
into HtmlBody should use `html.escape`; the sample does so explicitly. Rendered Subject text is also
returned verbatim, including CR/LF. An email transport integration must enforce its own header-value
and header-injection rules before sending.
IEmailTemplateService orchestrates runtime reads/rendering. For unsaved content, call
ITemplateValidator.ValidateAsync<TModel>(content), then ITemplateRenderer.RenderAsync(content, model,
culture) for preview; static validation cannot guarantee success for every runtime model value.

Expected sample problems have fixed, sanitized titles/types:
missing template 404; invalid query culture 400; validation/rendering/provider-format failures 500;
operational Azure/I/O/access failures 503. FileSystem and Azure format failures have separate types.
Responses include no exception message, diagnostic contents, inner exception or stack trace.
Cancellation is passed through. The endpoint deliberately has no general exception framework.
Unhandled framework failures and logging remain host responsibilities. This fixed-data demo has no
authentication/authorization; add application access controls before exposing real previews.

## Logging policy

Core references Microsoft.Extensions.Logging 10.0.11. Registration adds logging infrastructure using
normal Microsoft DI without adding output providers or changing host filters. No providers is valid.
Providers use ILogger<T> internally; all library public signatures are unchanged.

Core emits Debug events for positive-cache hit/miss, exact variant absence/source load, culture fallback,
and final not-found. Disabled caching does not emit cache hit/miss. Identifiers Name/Culture/Version
are structured Debug values, and may themselves be sensitive application data. Leave MailStencil at
Warning in normal production configuration; enable Debug only under the host's logging policy.

Core emits Warning for static validation and runtime rendering failures. FileSystem and Azure emit
one Warning for their own content-format/safety failures. These warnings contain fixed messages only,
with no identifiers, paths, URIs, content, diagnostics or exception objects. Cancellation is not a
failure log. No successful operation is automatically logged at Information, and Azure SDK HTTP/retry
events are not duplicated. Event IDs/names are internal implementation details, not a public contract.

MailStencil-generated state/messages never include model properties, template or rendered contents,
customer data, connection strings, credentials, SAS URLs, downloaded JSON, or exception inner data.
Exceptions still propagate unchanged: hosts and Azure/ASP.NET logging can independently log them;
this policy and its sentinel tests apply specifically to MailStencil's generated logging.
Logging providers must behave correctly; failures thrown by a host's logger remain host failures.

To inspect fallback/cache locally:
```json
{ "Logging": { "LogLevel": { "MailStencil": "Debug" } } }
```

## Tests and platform limits

The new ASP.NET test project uses WebApplicationFactory/TestServer, configuration binding and a capture
logger. Azure host tests use the actual SDK with a deterministic HTTP transport double, without a real
account. They verify startup requires no Azure request. The Console sample remains unchanged.

The existing Azurite suite/workflow remains opt-in and was statically reviewed: its restore/build still
covers the solution and its filter selects only emulator tests. Milestone 7 also executed all 11 cases
against the pinned official emulator image on the Windows host.

Windows remains the verification host. Linux/macOS still require execution before release. FileSystem
uses ordinal exact-name enumeration even on case-insensitive hosts. Symlink tests explicitly skip
when link creation is unavailable. On macOS, /var (often used by the temporary-directory path) may
be a symlink to /private/var: test-owned temporary roots now use the physical `/private/var` spelling,
while production continues to reject symlink ancestors. Timestamp precision, File.Replace/
open-file sharing and network-filesystem metadata behavior also require platform verification.
No platform behavior was weakened to make tests pass.
