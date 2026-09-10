# MailStencil public API — Milestone 9 release candidate

All domain types use namespace `MailStencil`. All model classes are sealed; `TemplateRequest`
is a sealed record with compiler-generated value equality, operators, `ToString`, and cloning.
The following lists every declared public member, including implicit parameterless constructors.
Nullable annotations and optional parameters are part of the contract.

```csharp
public interface ITemplateReader
{
    Task<EmailTemplateSource?> GetAsync(TemplateRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITemplateRenderer
{
    Task<RenderedEmailTemplate> RenderAsync<TModel>(EmailTemplateContent content,
        TModel model, CultureInfo culture, CancellationToken cancellationToken = default)
        where TModel : notnull;
}

public interface IEmailTemplateService
{
    Task<RenderedEmailTemplate> RenderAsync<TModel>(string name, TModel model,
        CancellationToken cancellationToken = default) where TModel : notnull;
    Task<RenderedEmailTemplate> RenderAsync<TModel>(TemplateRequest request, TModel model,
        CancellationToken cancellationToken = default) where TModel : notnull;
}

public sealed record TemplateRequest
{
    public TemplateRequest(string name, string? culture = null, string? version = null);
    public string Name { get; }
    public string? Culture { get; }
    public string? Version { get; }
}

public sealed class EmailTemplateContent
{
    public EmailTemplateContent(string subject, string? htmlBody = null, string? textBody = null);
    public string Subject { get; }
    public string? HtmlBody { get; }
    public string? TextBody { get; }
}

public sealed class EmailTemplateSource
{
    public EmailTemplateSource(TemplateRequest request, EmailTemplateContent content,
        TemplateMetadata? metadata = null);
    public TemplateRequest Request { get; }
    public EmailTemplateContent Content { get; }
    public TemplateMetadata? Metadata { get; }
}

public sealed class TemplateMetadata
{
    public TemplateMetadata();
    public string? ETag { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset? LastModified { get; init; }
}

public sealed class RenderedEmailTemplate
{
    public RenderedEmailTemplate(string subject, string? htmlBody = null, string? textBody = null);
    public string Subject { get; }
    public string? HtmlBody { get; }
    public string? TextBody { get; }
}

public sealed class MailStencilOptions
{
    public MailStencilOptions();
    public string DefaultCulture { get; set; } // default: "" (invariant/default variant)
    public TimeSpan CacheDuration { get; set; } // default: five minutes; zero disables caching
}
```

Namespace `Microsoft.Extensions.DependencyInjection`:

```csharp
public static class MailStencilServiceCollectionExtensions
{
    public static IServiceCollection AddMailStencil(this IServiceCollection services,
        Action<MailStencilOptions>? configure = null);
}
```

## Milestone 2 additions

The Milestone 1 signatures above are unchanged. The renderer's XML remarks now explicitly state
that declared root/nested/element types are authoritative and a null root model is invalid.
The implementation stays internal; consumers resolve the interfaces through DI.

Namespace `MailStencil` (Core, with no Scriban dependency):

```csharp
public interface ITemplateValidator
{
    Task<TemplateValidationResult> ValidateAsync<TModel>(EmailTemplateContent content,
        CancellationToken cancellationToken = default) where TModel : notnull;
}

public enum TemplateComponent { Subject = 0, HtmlBody = 1, TextBody = 2, General = 3 }
public enum TemplateDiagnosticSeverity { Warning, Error }

public sealed record TemplateSourceSpan(int Offset, int Length, int Line, int Column);

public sealed record TemplateDiagnostic(TemplateComponent Component,
    TemplateDiagnosticSeverity Severity, string Code, string Message,
    TemplateSourceSpan? Span = null, string? Member = null);

public sealed class TemplateValidationResult
{
    public TemplateValidationResult(IEnumerable<TemplateDiagnostic> diagnostics);
    public IReadOnlyList<TemplateDiagnostic> Diagnostics { get; }
    public bool IsValid { get; }
}

public sealed class TemplateValidationException : Exception
{
    public TemplateValidationException(TemplateValidationResult validationResult);
    public TemplateValidationResult ValidationResult { get; }
}

public sealed class TemplateRenderingException : Exception
{
    public TemplateRenderingException(TemplateDiagnostic diagnostic, Exception? innerException = null);
    public TemplateDiagnostic Diagnostic { get; }
}
```

The positional records expose init-only properties corresponding to their constructor parameters,
plus generated deconstruction, value equality, cloning, operators, and `ToString`. Source span offsets
are zero-based UTF-16; length is a count of UTF-16 code units; line/column are one-based.

Namespace `Microsoft.Extensions.DependencyInjection` (MailStencil.Scriban package):

```csharp
public static class MailStencilScribanServiceCollectionExtensions
{
    public static IServiceCollection AddScribanRenderer(this IServiceCollection services);
}
```

This extension registers singleton `ITemplateRenderer` and `ITemplateValidator` defaults using
TryAdd, preserving custom registrations. They share the same internal schema/AST implementation,
but need not be the same service instance. No reader or IEmailTemplateService is registered.

## Diagnostic codes and errors

| Code | Meaning |
| --- | --- |
| MSV001 | Scriban syntax/parser error |
| MSV002 | Unknown root/local variable (may include deterministic typo suggestion) |
| MSV003 | Unknown nested member, invalid member chain or unsupported index access |
| MSV004 | Unsupported language construct, function or write target |
| MSV005 | Invalid iterator, argument count or incompatible local assignment shape |
| MSV006 | Template length or AST depth limit |
| MSV007 | Unsupported declared model contract or member naming collision |
| MSR001 | Execution failure, including runtime value errors or limits |
| MSR002 | Model projection failure before component execution |

All current diagnostics have Error severity. Schema/projection failures apply to the whole input;
they use General with no source span. General is appended to preserve existing component numeric
values. Parser/member/runtime diagnostics retain their specific email component. Messages may evolve;
use codes and structured fields for program logic. Diagnostics can contain template identifiers or
parser text; treat them and exception inner causes as potentially sensitive when logging.

`ValidateAsync` returns expected syntax/member/policy failures without throwing. Cancellation throws
OperationCanceledException. `RenderAsync` validates first and throws TemplateValidationException for
invalid syntax, members, policy or model contracts. Its ValidationResult preserves all structured
diagnostics. A null API argument throws ArgumentNullException.
After validation, projection/execution errors use TemplateRenderingException; cancellation remains
OperationCanceledException carrying the supplied token. Static validation is not a promise about
runtime data, getter behavior, null dereferences, arithmetic errors or resource limits.

Inspected against the Release assemblies and source nullable annotations. Library assemblies
generate XML documentation with missing documentation treated as an error. Azure additions are described below.
There are no writer, distributed-cache, schema-generation, authoring,
version-management or CLI implementations.

## Milestone 3 additions

The existing Core and Scriban public APIs are unchanged. Namespace `MailStencil.FileSystem`:

```csharp
public sealed class FileSystemTemplateOptions
{
    public FileSystemTemplateOptions();
    public string BasePath { get; set; } // default: ""; must be configured
    public int MaxTemplateFileSize { get; set; } // bytes including BOM; default: 131072
}

public sealed class FileSystemTemplateException : IOException
{
    public FileSystemTemplateException(TemplateRequest request, string message,
        Exception? innerException = null);
    public TemplateRequest Request { get; }
}
```

Namespace `Microsoft.Extensions.DependencyInjection`:

```csharp
public static class MailStencilFileSystemServiceCollectionExtensions
{
    public static IServiceCollection AddFileSystemTemplateReader(this IServiceCollection services,
        Action<FileSystemTemplateOptions> configure);
}
```

The reader and deterministic test seam remain internal. FileSystem references Core only, with no
Scriban/cloud SDK references. The provider never parses or validates template language.

- BasePath must be nonblank with a valid platform filesystem path. Relative paths become absolute
  at singleton construction, relative to that working directory. The provider creates no directories.
- MaxTemplateFileSize accepts 1 through 16 MiB, defaults to 128 KiB, and counts physical UTF-8 bytes
  including an optional BOM. Three known parts imply an aggregate bound of three times the limit;
  there is no redundant aggregate-size setting. Larger configured sources may exceed renderer limits.
- Options validation occurs on options/reader resolution. The reader snapshots configuration once;
  changing options afterwards does not retarget it. There is no options monitoring or caching.
- Registration adds exactly one singleton ITemplateReader. An already registered reader, including
  repeat FileSystem registration, causes InvalidOperationException before adding configuration.
  Callers manually modifying IServiceCollection later retain normal Microsoft DI behavior and are
  responsible for maintaining the single-reader invariant. No provider-selection system is added.
- A missing exact directory returns null. Existing incomplete, oversized, invalid UTF-8, unsafe
  link/type entries, or repeatedly changing templates throw FileSystemTemplateException. Invalid
  logical identifiers throw ArgumentException; null requests throw ArgumentNullException; explicit
  versions throw NotSupportedException; cancellation throws OperationCanceledException. Access
  denial and unrelated I/O failures propagate as their original exceptions, not missing results.

See [filesystem architecture](architecture.md#filesystem-provider) for physical layout, exact case/
culture mapping, symlink/TOCTOU limitations, consistency strategy and metadata semantics.

## Milestone 4 additions

Namespace MailStencil (Core):

```csharp
public sealed class TemplateNotFoundException : Exception
{
    public TemplateNotFoundException(TemplateRequest request);
    public TemplateRequest Request { get; }
}
```

The exception retains the original request and rejects a null constructor argument.
MailStencilOptions.CacheDuration is the only new option; no existing method signatures changed.
The runtime implementation and cache key remain internal. Core references
Microsoft.Extensions.Caching.Memory 10.0.11; no provider or rendering-engine dependency is added.

AddMailStencil registers a singleton IEmailTemplateService using TryAdd. Reader and renderer
registrations may precede or follow it; dependencies resolve when the service is requested.
Custom services are preserved. The default service snapshots validated options once and owns a
dedicated cache disposed with the service/container. Dependencies must support concurrent singleton use.

The string overload normalizes DefaultCulture like TemplateRequest; empty means default/invariant.
The request overload starts at the supplied culture, including null even when DefaultCulture is set.
Lookup follows CultureInfo.Parent to null, retaining name/version. Only null reads trigger fallback.
The renderer always receives the original requested/configured formatting culture; null is invariant.

Successful EmailTemplateSource snapshots are cached by an internal ordinal, case-sensitive
(name, normalized culture, version) key for the actual exact lookup. Nulls and fallback resolutions
are never cached, so newly added specific variants are discoverable on the next render. Models,
rendered output, validation results and exceptions are not cached. A successful source remains
cached if subsequent rendering fails. Direct ITemplateReader calls bypass this cache.

CacheDuration defaults to five minutes, uses absolute expiration without refresh on hits, accepts
zero to disable caching, and rejects negative values through options validation. Content changes
to an already cached exact variant become visible after expiration. No watchers or revalidation
are present. Concurrent cold calls may perform duplicate independent reads; cancellation of one
call does not cancel another. Cancellation is checked even on cache hits and passed to both dependencies.

Only exhaustion of clean null reads throws TemplateNotFoundException. Provider capability, storage,
validation and rendering exceptions propagate unchanged; cancellation remains OperationCanceledException.
See [runtime architecture](architecture.md#runtime-orchestration-localization-and-cache).

## Milestone 5 additions

Namespace MailStencil.AzureBlob:

```csharp
public sealed class AzureBlobTemplateOptions
{
    public AzureBlobTemplateOptions();
    public string ContainerName { get; set; } // required; default ""
    public string Prefix { get; set; } // default ""
    public int MaxTemplateBlobSize { get; set; } // default 4194304; range 1–16777216 bytes
}

public sealed class AzureBlobTemplateException : IOException
{
    public AzureBlobTemplateException(TemplateRequest request, string message,
        Exception? innerException = null);
    public TemplateRequest Request { get; }
}
```

Namespace Microsoft.Extensions.DependencyInjection:

```csharp
public static class MailStencilAzureBlobServiceCollectionExtensions
{
    public static IServiceCollection AddAzureBlobTemplateReader(this IServiceCollection services,
        Action<AzureBlobTemplateOptions> configure);
}
```

These are the only new public types. Existing APIs are unchanged. The reader, name mapper and
JSON parser remain internal. The package references Core and Azure.Storage.Blobs 12.29.2 only.

The extension registers one singleton ITemplateReader and rejects an existing reader, including
repeat registration. The application registers BlobServiceClient before resolution, in either order.
Options validate on resolution and are snapshotted once. No credentials, cache or writer settings
are added. The client remains application-owned; the reader disposes each download stream/response.

The reader performs exact culture lookups, rejects explicit logical versions before I/O, and returns
null only for HTTP 404 with Azure error code BlobNotFound. Other Azure failures propagate unchanged.
Malformed, unsupported-format, oversized or inconsistent-length content throws
AzureBlobTemplateException. Null arguments throw ArgumentNullException; unsafe identifiers throw
ArgumentException; cancellation remains OperationCanceledException.

See [Azure architecture](architecture.md#azure-blob-provider) for the storage format and naming rules.

## Milestone 6 integration

No library public types, methods or options were added or changed. Internal implementations now use
ILogger<T>; AddMailStencil and each reader registration add standard logging infrastructure without
output providers. Public logger/event abstractions were not introduced.

ASP.NET applications can bind existing options and opt into ValidateOnStart using normal Microsoft
Options APIs. Eager host validation is not forced on other consumers. The sample's Program and
OrderConfirmationModel are application/test entry points, not NuGet library API.
See [ASP.NET usage and logging policy](aspnet-core.md).

## Milestone 7 public API audit

The compiled Release surface remains 19 Core exported types, one Scriban type, three FileSystem
types and three Azure Blob types. Milestone 7 added no public type, member, overload, option or
dependency. FileSystemTemplateException now rejects a runtime-null `message`, enforcing its existing
non-null annotation and matching AzureBlobTemplateException; its signature is unchanged.

The audit intentionally retains the small capability interfaces. ITemplateReader is the read-only
storage seam; future Authoring should add separate write/list/history/activation capabilities rather
than add required members to it. Validation and preview already compose through ITemplateValidator
and ITemplateRenderer. Provider implementations and runtime/cache implementations remain internal.

The following are 1.0 compatibility commitments and require care before release:

- TemplateRequest is a sealed record, so value equality, generated cloning and its Name/Culture/Version
  components are observable. TemplateDiagnostic and TemplateSourceSpan are positional sealed records,
  including generated equality, deconstruction and init accessors.
- TemplateComponent numeric values are Subject=0, HtmlBody=1, TextBody=2 and General=3;
  TemplateDiagnosticSeverity values are Warning=0 and Error=1.
- Optional constructor parameters, nullable body/metadata/member/span/version values, `notnull` generic
  constraints, sealed classes and exception base classes are part of the source/binary contract.
- Public option objects are mutable configuration, but singleton implementations snapshot validated
  values. Adding optional properties remains possible; changing defaults or normalization is behavioral.
- Adding abstract interface members, changing positional record components, reordering enum values,
  changing constructor parameter order/defaults or changing exception inheritance would be breaking.

No trimming or Native AOT compatibility is promised. The Scriban implementation reflects over the
declared model contract, so required metadata must be preserved by the host.

## Milestone 8 Authoring compatibility conclusion

No public API changed. The current 1.0 runtime surface is safe to freeze: `ITemplateReader` remains
read-only; future write/delete/catalog/history/activation capabilities can be separate optional interfaces.
Identity-free `EmailTemplateContent` already supports unsaved validation and preview through
`ITemplateValidator` and `ITemplateRenderer`. Authoring identity, metadata and concurrency conditions can
compose around content without changing it.

`TemplateRequest.Version` and `TemplateMetadata.Version` remain sufficient provider-independent logical
version seams; null request version continues to mean active/current. `TemplateMetadata.ETag` remains an
opaque aggregate snapshot token suitable for a future provider-specific conditional write, without exposing
Azure types. Additional activation/write tokens can belong to future Authoring results.

Future schema generation must map the existing internal Scriban `ModelSchema` so declared-type discovery,
member restrictions, collection rules and naming policy cannot diverge from validation/rendering. Neither
that internal type nor speculative Authoring contracts are made public here. See the dedicated
[Authoring compatibility review](architecture.md#milestone-8--authoring-compatibility-review).

## Milestone 9 package/API baseline

Packaging and release-candidate work adds no public type or member. The four runtime assemblies retain the
Milestone 7/8 surface above: 19 Core, one Scriban, three FileSystem, and three Azure Blob exported types.
`eng/MailStencil.PublicApiGuard` compares 115 compiled public reflection entries with
`eng/PublicApiBaseline.txt` in CI. Any intentional public API change must update this document, XML
documentation, compatibility reasoning, tests, and the baseline in the same review.
