# MailStencil architecture — Milestone 8

## Scope and structure

Milestones 1–7 provide Core contracts, Scriban rendering/AST validation, exact FileSystem and Azure Blob
readers, runtime orchestration with localization fallback and positive in-memory source caching, and
production hardening. Milestone 8 reviews compatibility with future Authoring without implementing it.
No writer, catalog, schema-generation, version-management, distributed caching or CLI exists.

```text
MailStencil.sln
Directory.Build.props                 net10.0, nullable, warnings-as-errors, XML docs
src/
  MailStencil.Core/                   Contracts, models, DI/options, diagnostics, runtime/cache
  MailStencil.Scriban/                Renderer, validator, shared internal schema, DI
  MailStencil.FileSystem/             Exact UTF-8 reader, provider options/exception, DI
  MailStencil.AzureBlob/              Exact single-blob JSON reader, options/exception, DI
tests/
  MailStencil.Core.Tests/             Foundation and runtime orchestration tests
  MailStencil.Scriban.Tests/          Rendering, validation, safety and concurrency tests
  MailStencil.FileSystem.Tests/       Storage, security, consistency, DI and concurrency tests
  MailStencil.AzureBlob.Tests/        Unit tests and opt-in Azurite integration tests
samples/
  MailStencil.Sample.Console/        Working service orchestration example and local templates
  MailStencil.Sample.AspNetCore/      Working minimal API, configuration and localized templates
```

Core, Scriban and FileSystem are packable; final NuGet metadata, license, SourceLink, release automation and
package validation remain Milestone 9. Core has no Scriban/cloud SDK dependency. Scriban references
Core and pins Scriban 7.4.0, the stable 7.4.x package verified for this milestone. Sources consulted:
[NuGet 7.4.0](https://www.nuget.org/packages/Scriban/7.4.0),
[Scriban safe runtime](https://scriban.github.io/docs/runtime/safe-runtime/), and the source shipped
inside that exact NuGet package. No older Scriban dependency is used.

## Public responsibilities and API review

The Milestone 1 signatures are unchanged:

- ITemplateReader reads an exact snapshot without write capabilities.
- ITemplateRenderer renders identity-free EmailTemplateContent with an explicit culture and model.
  This remains appropriate for runtime rendering and eventual unsaved preview.
- IEmailTemplateService coordinates exact reads, culture fallback, source caching and rendering. Its signatures are unchanged; no ValidateAsync member was added.
- EmailTemplateContent and RenderedEmailTemplate still require a non-null subject and at least one
  non-null body. Empty strings are permitted. Neither needs engine-specific state or diagnostics.
- MailStencilOptions configures the service DefaultCulture and CacheDuration. Direct rendering still takes its culture explicitly.

New Core API: ITemplateValidator plus a validation result, diagnostic, source span, component and
severity enums, a static validation exception and a separate execution exception. These types must be public because consumers need
structured validation without depending on Scriban. String diagnostic codes avoid a large evolving
enum. Source spans do not leak Scriban types. The AST checker, schema, naming policy, projection,
execution limits and renderer implementation are internal. See [complete API](public-api.md).

AddScribanRenderer registers singleton renderer/validator defaults with TryAdd. Repeated calls do
not duplicate registrations and custom implementations remain untouched. The engine is stateless:
every operation creates its own schema and parsed templates, and every component creates a fresh
TemplateContext, local scope and output writer. No mutable context is shared and no parsing cache
has been added to the renderer. Source caching belongs to the service. Safe orchestration/provider logging is described below; model/email
contents are not logged automatically.

Expected template problems return diagnostics from ValidateAsync. RenderAsync validates before
reading model getters and throws TemplateValidationException for invalid syntax, members, policy or
declared model contracts. Its ValidationResult preserves the complete structured diagnostics;
callers wanting normal validation flow can use ITemplateValidator first. Null API arguments still
throw ArgumentNullException. Projection/execution failures are exceptional
and use TemplateRenderingException, with a diagnostic and inner cause. Cancellation is normalized
to OperationCanceledException with the supplied token. Static validation does not guarantee success
for every possible data value (for example, a null dereference, failing getter or excessive loop).

TemplateComponent values are Subject = 0, HtmlBody = 1, TextBody = 2, General = 3. General is appended
without renumbering existing values. Unsupported model contracts, naming collisions and whole-model
projection failures use General, with no source span. Parser/member/runtime errors that belong to
an email component retain Subject, HtmlBody or TextBody.

## Shared declared-type schema and projection

ModelSchema is the only reflection implementation. It builds a graph from typeof(TModel), public
instance readable non-indexed properties, declared nested property types, and IEnumerable<T>
element types. Public inherited/interface properties are included; methods, fields, statics and
runtime-subclass additions are excluded. Virtual getters still dispatch normally, but cannot
expand the declared member contract. Naming collisions are rejected as schema diagnostics.

The naming-policy seam calls Scriban's StandardMemberRenamer.Default: CustomerName becomes
customer_name. No duplicate snake_case algorithm exists. A future options overload can select
CamelCase or Original at this single seam without changing renderer/validator signatures. Such
configuration is intentionally not exposed in this milestone; future schema generation must reuse
this same graph and policy rather than introducing another reflection walker.

Supported values are strings, chars, bools, ordinary integral/floating/decimal numbers, enums,
Guid, DateTime, DateTimeOffset, DateOnly, TimeOnly, custom property-based DTOs/interfaces, and typed
collections (including arrays). Nullable<T> uses its underlying schema. Chars/enums/Guid/dates/times
are explicitly converted to strings (enums use names, Guid uses D, temporal values use invariant O).
Numbers remain numeric and render with the explicitly supplied formatting culture.

Object-typed values, delegates, reflection types, dictionaries, IQueryable, untyped/ambiguous
collections, and other System/Microsoft infrastructure types are rejected. Dictionaries and dynamic
JSON shapes need a separate future contract decision. A plain scalar or collection is not a valid
root model; wrap it in a property-based DTO. Root names string/html/array/math/for are reserved.

Projection eagerly creates detached ScriptObject/ScriptArray graphs, with immutable scalar leaves.
Model members and containers are marked read-only. No arbitrary CLR references are put in Scriban.
Each component gets a separate writable local scope above the model. Assignments to members,
indexers, root model variables or builtins are rejected by the AST policy. Allowed builtins do not
mutate model containers. Template code therefore cannot mutate the original CLR model.

Getters and enumerators are application code, not sandboxed code. They must be side-effect-free,
bounded and safe to call; the library cannot preempt a blocking getter or MoveNext. Projection
checks cancellation between reads/items, and bounds nodes and depth, including cycles. Caller
concurrent mutation of the supplied model is outside the renderer's thread-safety guarantee.

## AST validation and supported language

Each Subject/HtmlBody/TextBody is parsed independently with Scriban's parser. Parser messages retain
component and source location. Validation walks the AST without executing the template or calling
model getters. It resolves roots and nested members through the shared schema, propagates element
shapes into loop variables, and propagates shapes through aliases, arrays and expressions.

Supported constructs:

- Scalar/nested member output, literals, raw text, comments and normal Scriban whitespace controls.
- if / else if / else, conditional expressions, comparisons, boolean logic, basic arithmetic,
  null/empty coalescing, parentheses, and unary plus/minus/not.
- for over typed collections, array literals and numeric ranges; nested loops; break/continue;
  for.index/index0/length/rindex/rindex0/first/last/even/odd/changed metadata.
- Local assignment with =, including $locals and aliases. Assignments must preserve the shape of
  an existing local. Branches merge definitely assigned variables; a variable defined on only one
  path cannot be used afterwards. Loop-only variables do not become available after the loop.
  Reusing an existing local as a loop variable is intentionally rejected, including nested-loop
  name reuse, to avoid ambiguous shadowing. Local member access through ambiguous branch types
  is rejected conservatively.
- Typed collection numeric indexing, string numeric indexing, and constant-string object indexing
  for declared member names. Null-conditional member access is supported.
- Direct calls and pipelines (including chained pipelines) to this exact builtin allowlist:
  string.upcase, string.downcase, string.capitalize, string.strip, string.size, string.contains,
  string.replace, html.escape, array.size, math.abs, math.round.

The same builtin table populates the runtime and checks validation targets, arity and return shapes.
Unknown variables and nested chains are errors even in non-executed branches. Typo suggestions for
root members use bounded edit distance with ordinal tie-breaking, and are informational text only.
Validation intentionally is not a full Scriban type system: runtime argument values, arithmetic,
index bounds and resource usage may still fail. Conservative rejection is preferred when a member
surface cannot be established statically.

Unsupported constructs fail closed with a diagnostic, including custom functions, function aliases,
dynamic invocation/eval, includes/loaders, this/global-object access, object literals, dynamic object
keys, while/tablerow, capture/import/wrap, loop modifiers, compound assignments, member/index writes,
and builtins outside the allowlist. No regex engine, object eval, file/network access or reflection
function is exposed. These are deliberate restrictions of the untrusted email-template subset,
not limitations claimed for Scriban itself. User-defined template recursion is not enabled.

## Null, HTML and formatting behavior

- A null root model throws ArgumentNullException, consistent with the notnull generic constraint.
- Null properties/objects/collections rendered directly produce empty text, following Scriban.
- Accessing customer.name when customer is null fails at runtime under strict target access.
  Use an if guard or customer?.name. Validation checks the declared chain, not runtime nullness.
- A null collection iterates zero times. No synthetic empty collection/object is substituted.
- Null scalar values remain null, rather than zero or an empty string in the projected graph.
- MailStencil does not automatically sanitize or HTML-escape model values. Untrusted strings inserted
  into HtmlBody should use the explicit html.escape function, for example {{ value | html.escape }}.
  Raw HTML and Unicode otherwise remain intact. Warnings for unescaped output are a possible future
  hardening feature; none are emitted yet. Consumers remain responsible for content policy and
  subject line controls when integrating with an email sender.
- Every component gets a fresh context. Assignment in Subject does not define a variable in a body.
  Culture is cloned/read-only per component; no ambient culture or localization fallback is used.

## Safety limits

Fixed, internal limits keep this milestone's public API small:

| Limit | Value |
| --- | --- |
| Component template source | 128 Ki UTF-16 code units |
| Parser / AST depth | 64 |
| Scriban cumulative nested loop budget | 1,000 iterations |
| Model projection node budget | 10,000 |
| Model projection / Scriban object depth | 32 |
| Scriban function recursion limit | 32 (user functions are not enabled) |
| Component output writer | 1 Mi UTF-16 code units; exceeding it throws |
| Scriban LimitToString | 1 Mi characters |
| Regex timeout | 100 ms defense-in-depth; regex builtins are not exposed |

StrictVariables and strict member/target/function/index access are explicitly enabled. Null indexing
is disabled, MemberFilter denies CLR reflection as a secondary safeguard, TemplateLoader is null,
and CancellationToken is passed to Scriban. LimitToString retains Scriban's documented behavior:
individual string/object materialization may truncate with an ellipsis, while some operations throw.
The independent output writer prevents total component output from exceeding its cap. This is bounded
execution inside the process, not OS/process isolation or a guaranteed CPU/memory quota.

## Preserved storage and future extension decisions

ITemplateReader remains the only storage capability. It returns coherent materialized snapshots,
null only for missing exact variants/versions, and propagates operational errors/cancellation.
Names and logical versions are opaque/case-sensitive; request culture is normalized, with null
selecting the default variant. Null version selects active. Unsupported explicit versions must be
rejected, never silently ignored. Source preserves request identity; optional metadata may identify
the resolved active version. An ETag must describe all content parts, not just one storage object.

The runtime service owns culture fallback and exact positive source caching (see below). Future distributed caching may replace its internal implementation using a provider/tenant namespace and unambiguous keys. Conditional revalidation remains a possible separate capability.

| Future provider | Mapping to existing read contract |
| --- | --- |
| S3 | Bucket/prefix plus a manifest or bundle; native version IDs remain internal; coherent aggregate metadata. |
| Google Cloud Storage | Bucket/object/manifest; generations may enforce coherent reads; expose logical versions separately. |
| Google Drive | Resolve stable file IDs within configured folder/index; reject duplicate ambiguous names; revision/export handling remains internal. |

Future Authoring can compose identity/content/editing metadata and add independent write/delete/list/
history/activation interfaces, without burdening read-only providers. ITemplateValidator already
validates unsaved content, and preview can reuse ITemplateRenderer. This does not implement Authoring.
Schema generation can reuse ModelSchema. Writer concurrency, create-versus-replace, logical version
activation and rollback remain deferred decisions, not speculative interfaces.

## Preserved rendering decisions and risks

- Product naming is MailStencil rather than the original plan's CloudEmailTemplates. The prior rename
  also updated the candidate options name in PLAN.md. No additional plan changes were needed here.
- The supported language is an explicit safe subset, with conservative local-flow rules and a small
  builtin allowlist. This is narrower than arbitrary Scriban; it is documented and tested, not silently
  treated as full language support. Additional constructs need both runtime and static policy review.
- Naming defaults are finalized as Scriban snake_case. Configurable alternative naming remains an
  additive future option; fixed safety limits also remain internal until configuration requirements exist.
- Eager projection copies all declared properties. Expensive getters, huge strings/collections,
  cycles and polymorphic/dynamic contracts need caller attention. No trimming/Native AOT or source
  generator support is promised; reflection metadata must remain available.
- A successfully validated model surface is the same surface projected at runtime. Validation is not
  proof against null/data errors or resource limits. Diagnostics and inner exceptions may contain
  template text or application exception data and should not be logged indiscriminately.
- The host should evaluate process isolation for truly hostile workloads requiring hard CPU/memory
  guarantees. HTML sanitization and subject injection controls are separate email integration concerns.
- FileSystem's storage-specific behavior is defined below. No storage concerns or encoding changes
  were added to the renderer or its model schema.
- Keep future validation additions in the existing separate capability; do not add required methods
  to renderer/reader interfaces. Preserve constructor signatures when adding optional state. Request
  record equality and culture/version conventions remain public compatibility commitments.

## Filesystem provider

MailStencil.FileSystem references only Core, not Scriban or any cloud SDK. The existing ITemplateReader
semantics and all Core/Scriban public signatures are unchanged. Public additions are limited to
FileSystemTemplateOptions, FileSystemTemplateException and AddFileSystemTemplateReader. A small
provider-specific IOException subtype lets callers distinguish malformed/unsafe/unstable storage
from template-language validation; no new Core storage abstraction or exception hierarchy is needed.

### Layout and exact lookup

```text
<BasePath>/
  order-confirmation/
    default/
      subject.txt
      body.html
      body.txt
    it/
      subject.txt
      body.html
    it-IT/
      subject.txt
      body.txt
```

Null culture maps to default; a non-null normalized request culture maps to that exact directory.
The provider never reads CurrentCulture/CurrentUICulture and never falls back to a parent/default
culture. Names, culture directories and part filenames use ordinal case-sensitive matching even
on Windows: mismatched case is not silently mapped to another logical identity. Explicit culture
`default` is rejected because the directory name is reserved for null culture.

Version null selects current content. Any explicit version throws NotSupportedException before
filesystem lookup, even if the template does not exist. No version directories or management exist.

Missing exact directories (including a missing base/template directory) return null. An existing
variant must contain subject.txt and at least one body; absent parts, wrong file/directory types,
oversized files and invalid UTF-8 are provider exceptions, not missing templates. Empty subject/body
files are valid; additional unrelated files are ignored as content. A directory observed during a
read that subsequently disappears fails after bounded retries rather than being reported as a clean
missing lookup. Access errors are not hidden by File.Exists/Directory.Exists checks.

### Encoding and memory limits

All files use strict UTF-8. One leading UTF-8 BOM is removed, otherwise text and line endings are
preserved verbatim. Invalid sequences throw FileSystemTemplateException with a DecoderFallbackException
cause. UTF-16 BOMs do not trigger encoding detection. No ANSI/default-codepage decoding is used.

MaxTemplateFileSize is measured in bytes including BOM, default 128 KiB and configurable from 1 byte
to 16 MiB. The three-part layout bounds aggregate source bytes to three times that value (384 KiB
by default). Each read allocates only the inspected, bounded file length; premature EOF or growth
causes a retry. Metadata and size are inspected again on retries, so growing files cannot cause
unbounded allocation. Decoded strings and temporary byte arrays add bounded memory overhead.
The default is conservative relative to Scriban's 128 Ki UTF-16 source limit, especially for non-ASCII
text. Raising the storage limit does not change any renderer limit. There is no separate aggregate
configuration knob because only three known files are read.

### Path and link security

Logical identifiers are one segment of 1–128 ASCII letters/digits/hyphens/underscores, starting with
a letter/digit. Dot segments, separators of either platform, drive prefixes, UNC identifiers, colon/
alternate streams, percent encodings, whitespace and Windows reserved device names are rejected
rather than rewritten. Cultures are additionally normalized/validated by the existing TemplateRequest.
This restriction is provider-specific and does not narrow the Core contract for other providers.

BasePath and targets are resolved through Path.GetFullPath; Path.GetRelativePath verifies containment
with a directory boundary, not a vulnerable string prefix (`templates` versus `templates-evil`).
The configured base is trusted application configuration, not a user-supplied template identifier.
Relative base paths are resolved when the singleton is first constructed and then remain fixed.

All directory ancestors, including the base path and its ancestors, are checked for ReparsePoint
before traversal. Template directories and each content file are checked too. Symlinks, junctions,
other reparse points and reported device entries are rejected, even when their target stays inside
the base; broken links are rejected as well. Checks are repeated immediately before opening a file
and during final inspection. Permission/metadata failures do not become successful reads or null.
Deploy on a real directory tree; e.g. cloud placeholder reparse files or a symlinked base are unsupported.

These portable .NET path/attribute checks are **not an atomic OS-handle traversal sandbox**. A hostile
actor able to replace ancestors or files between checks can create TOCTOU races; hard links are not
identified by ReparsePoint, and Unix special files or mount changes require OS-specific protections.
Only regular files in an application-controlled tree are supported. Do not give untrusted principals
write access to the template tree or its ancestors; use process/OS isolation or immutable deployment
ownership when that threat exists. Known links and failures to establish the inspected path are
rejected; complete protection against hostile concurrent filesystem mutation is not claimed.

### Multipart snapshot and sharing

Each call has independent state and up to three immediate attempts:

1. Inspect the variant directory and all three part names, including absent optional bodies. Record
   file length, LastWriteTimeUtc and CreationTimeUtc, plus directory creation/write timestamps.
2. Read each present file asynchronously, honoring cancellation between chunks and using strict
   size bounds. FileShare.ReadWrite | FileShare.Delete allows common update/replacement strategies.
3. Recheck paths and the complete metadata set. A change, disappearance, short read or growth retries
   the complete snapshot. Exhaustion throws FileSystemTemplateException, never known mixed content.
4. Decode only the accepted snapshot and return immutable Core models. Streams have already closed.

Stable malformed content fails immediately. Optional body addition/removal is detected; directory
metadata may conservatively force retries for unrelated changes too. There are no watchers, caches,
global locks, cross-process locks or writer requirements. Unrelated requests are not serialized.
The internal per-instance test checkpoint runs at before-read/after-chunk/after-read boundaries and
is absent from public API/production registration; it makes race and cancellation tests deterministic.

This is metadata-stability detection, not a transactional filesystem snapshot. Same-size updates
that restore timestamps, coarse/cached filesystem metadata, replacements preserving all observed
metadata, or staged multipart writes paused across a read can evade detection. Writers should deploy
complete variants in a coordinated fashion or stop readers for updates where stronger consistency is
required. A manifest/commit protocol is future design work, not an implicit Authoring implementation.
Windows File.Replace was tested during an open shared read. File.Move overwrite of an open destination
was denied by the host OS despite sharing flags; deployment tools must use an OS-supported replacement
strategy. Flags do not override OS/remote-filesystem behavior or permissions.

LastModified is the maximum UTC last-write timestamp of the returned parts. Version remains null.
ETag remains null: a content hash could be added later, but no consumer in this milestone needs it,
and a timestamp of one part would misrepresent an aggregate token. No synthetic version is invented.

### DI and Console sample

```csharp
services.AddMailStencil()
    .AddScribanRenderer()
    .AddFileSystemTemplateReader(o => o.BasePath = "./templates");
```

Options validate on resolution. The singleton reader copies the full base path and size limit;
mutating options later does not retarget the reader. Registration rejects any existing ITemplateReader,
including repeated FileSystem registration, so the extension selects one active reader predictably.
Callers manually adding descriptors afterwards retain normal Microsoft DI behavior and must preserve
this invariant. No provider-selection or replacement framework is introduced.

The Console project copies an order-confirmation/default template to its output and calls IEmailTemplateService. Configured en-US falls back through en to default while preserving en-US formatting. HtmlBody explicitly uses html.escape for strings. Run with:

```sh
dotnet run --project samples/MailStencil.Sample.Console -c Release
```

### Storage risks carried forward

Cross-platform tests are written against portable APIs, but the verification host is Windows; run
the suite on Linux/macOS and any intended network filesystem before release. Symlink tests explicitly
skip only if the host lacks link-creation privileges. Security and snapshot limitations above remain
relevant regardless of passing tests. Relative paths depend on startup working directory; absolute
configuration is preferable for services. Blocking filesystem metadata/open calls cannot always be
preempted by CancellationToken even though content reads and checks honor it. Localization and caching orchestrate exact reads without adding fallback or state to this provider.

## Verification

Tests cover rendering all components, declared root/nested/collection types, snake_case, builtins,
Unicode/HTML, nullable members, guarded and unguarded nulls, local scope/aliases/branches, unknown
members, syntax/component/span diagnostics, deterministic suggestions, blocked CLR access/mutation,
concurrency, output/loop/source/model-depth limits and deterministic cancellation.

Milestone 2 final verification completed on .NET SDK 10.0.400:

- dotnet restore: passed.
- dotnet build -c Release: passed with zero warnings and zero errors.
- dotnet test -c Release: 129 passed (14 Core + 115 Scriban), zero failed or skipped.
- Compiled public API inspected: Core has the intended diagnostic/validation additions; Scriban
  exports only its DI extension class. XML documentation includes the validation exception and General component.

Milestone 3 verification completed on .NET SDK 10.0.400 / Windows:

- dotnet restore: passed.
- dotnet build -c Release: passed, zero warnings and zero errors.
- dotnet test -c Release: 202 passed (14 Core + 115 Scriban + 73 FileSystem), zero failed or skipped.
  All four symbolic-link tests ran on this host.
- Console sample: exited 0, loading and validating the filesystem template; Subject was
  `Order MS-123 confirmed`, HTML contained `Ada &amp; friends` and total `42.50`, and plain text
  contained `Ada & friends`. Run used the built Release output with --no-build --no-restore.
- Compiled FileSystem API inspected: exactly the options class, provider exception and DI extension
  are public; reader/test seam remain internal. Its generated XML has 8 entries. Its project
  references Core only, with no Scriban/cloud dependency.

Core, Scriban and FileSystem emit XML documentation; nullable reference types and warnings-as-errors
remain centrally enabled. AzureBlob testing is described in the Milestone 5 section below. The 73 new
tests cover exact reads, malformed UTF-8/parts, bounded sizes, path/link rejection, options/DI,
concurrent reads and deterministic changes/disappearance/replacement/cancellation. Milestone 4 verification is recorded below.

## Runtime orchestration, localization and cache

The internal EmailTemplateService depends only on ITemplateReader, ITemplateRenderer and validated
MailStencilOptions. It performs no Scriban validation itself: each call passes the original model,
declared generic type and loaded content directly to the renderer. Preview can still render
identity-free content directly. Neither storage nor renderer interfaces gained members.

AddMailStencil uses TryAddSingleton for IEmailTemplateService, preserving custom registrations.
All three registration extensions work in any order; no provider is built during registration.
Reader/renderer are required on service resolution (or eager container validation), and must have
lifetimes suitable for concurrent singleton use. The service snapshots options once, owns a private
MemoryCache, and disposes it when the container disposes the service. It neither consumes nor
configures an application's shared IMemoryCache. Independent containers cannot mix provider sources.

### Lookup and formatting

For it-IT the service attempts it-IT, it, then null/default; it attempts it then default; null
attempts default only. CultureInfo.Parent defines the chain, including script-bearing cultures.
Name and logical version remain unchanged. Only a null exact read advances the chain. Ambient
CurrentCulture and CurrentUICulture never select a variant or formatting culture.

The string overload uses normalized DefaultCulture; empty means null/invariant. The explicit request
overload respects null even when a different DefaultCulture is configured. This clarifies the old
contract remarks that suggested configured formatting for an explicit null request; no signature changed.
If fr-CA falls back to fr, formatting still uses fr-CA. Null always uses InvariantCulture.

### Cache semantics and freshness

Core uses Microsoft.Extensions.Caching.Memory 10.0.11. A private record key holds name, normalized
culture and version with ordinal, case-sensitive equality; it is not a serialized hash or ToString.
Because the cache belongs to one service/reader, no additional provider namespace is needed here.

Only successful EmailTemplateSource snapshots are stored under the exact identity read. A parent
hit is stored under the parent key, never under the missing child key. There is no negative or
resolution cache: the next request can immediately discover a newly added more-specific variant.
Direct ITemplateReader calls bypass service caching and remain exact.

CacheDuration defaults to five minutes. Absolute expiration starts at successful insertion; hits do
not extend it. Zero disables caching and negative values fail options validation. Changes/deletions
to an already cached variant become visible after expiration. No watcher, conditional read,
ETag revalidation, sliding expiration, size/priority options or distributed cache is implemented.
Models, rendered output, validation results and exceptions are never cached. A source successfully
loaded before a rendering failure remains a valid source-cache entry; rendering is retried normally.

MemoryCache operations are thread-safe and all call state is local. Concurrent cold requests may
load the same exact identity independently; there are no global locks or shared cancellation tasks.
If concurrent reads observe different snapshots, the last insertion wins until expiration.
Cancellation flows to reader/renderer, is checked around lookup/render and on cache hits, and
stops fallback when observed. Failed/cancelled reads are not inserted; one caller cannot poison
another caller's independent load.

### Errors and remaining risks before Azure

| Condition | Result |
| --- | --- |
| Every fallback lookup cleanly returns null | TemplateNotFoundException with original Request |
| Unsupported explicit version | NotSupportedException, no further fallback |
| Malformed/unsafe filesystem source | FileSystemTemplateException |
| Static template validation failure | TemplateValidationException |
| Projection/runtime failure | TemplateRenderingException |
| Cancellation | OperationCanceledException |
| Other storage/access errors | Original exception propagates |

No errors are flattened into missing-template results. Null API arguments remain ArgumentNullException.

The cache intentionally has no public size controls; high key cardinality or large sources can consume
memory until expiration. Use bounded application request identities, an appropriate duration or zero
to disable caching. This is per-process freshness, not cross-process invalidation. Future Azure reads
must still return coherent exact snapshots, distinguish absence from failures and honor cancellation.
Provider-specific version support must be explicit; no version management is introduced here.
Existing FileSystem race/platform limitations and Scriban execution limits remain unchanged.

MailStencil does not automatically sanitize or HTML-escape model values. Untrusted strings inserted
into HtmlBody should explicitly use html.escape. Warnings for unescaped output remain possible
future hardening work, not an implemented feature.

### Milestone 4 verification

The runtime tests add deterministic fake-reader/clock coverage for fallback, formatting, version
preservation, exact cache identity, expiry, zero duration, failure propagation and cancellation.
Integration tests exercise all six registration orders, direct-reader bypass and real provider/
renderer exceptions. Release verification totals 244 tests (46 Core, 125 Scriban, 73 FileSystem).
The Console sample uses the runtime service; Azure and ASP.NET Core remain untouched scaffolds.


Final Milestone 4 verification on .NET SDK 10.0.400 / Windows:
- dotnet restore: passed.
- dotnet build -c Release: passed, 0 warnings and 0 errors.
- dotnet test -c Release: 244 passed, 0 failed, 0 skipped. AzureBlob remains an empty test scaffold.
- Updated Console sample: exited 0; Subject "Order MS-123 confirmed", HTML "Ada &amp; friends",
  text "Ada & friends", total "42.50".
- Release Core exported types inspected: TemplateNotFoundException is the only new public type;
  CacheDuration is the only new options property. Their generated XML entries were verified.
  Runtime implementation/cache key remain internal. Nullable and warnings-as-errors remain enabled.

## Azure Blob provider

MailStencil.AzureBlob references Core and Azure.Storage.Blobs **12.29.2** (stable), with no Scriban,
FileSystem, Azure.Identity or ASP.NET dependency. The test project references Scriban solely to
exercise runtime composition. No other production package changes are needed.

### Client, registration and lifecycle

The application registers BlobServiceClient, constructed using its chosen credentials and SDK
retry/transport settings. MailStencil selects ContainerName via GetBlobContainerClient. Choosing
this over direct BlobContainerClient injection retains the planned container option and allows one
application SDK client to serve other uses. MailStencil has no credential or connection-string fields.

AddAzureBlobTemplateReader registers one singleton ITemplateReader and rejects any existing reader.
Registration order with client, AddMailStencil and AddScribanRenderer is unrestricted before resolution;
registration never builds a provider. Options validate and are copied on reader construction.
The reader shares the SDK client, with independent per-call state and no locks. No account, container
or blob is created or changed. SDK operational errors are not wrapped.

### Layout and identifiers

Each complete variant is one atomic blob:
```text
<prefix>/<name>/<culture-or-default>/template.json
mailstencil/order-confirmation/default/template.json
mailstencil/order-confirmation/it-IT/template.json
```

Names and prefix segments use 1–128 ASCII letters/digits/hyphen/underscore, starting with a letter or
digit. Unlike FileSystem, Windows device names have no special meaning in Blob Storage. Unsafe names
are rejected, not rewritten. Culture is normalized by TemplateRequest, then checked as a safe segment;
explicit culture "default" is reserved and rejected. Null maps to default. Blob names are case-sensitive.

Prefix is trusted configuration. Empty means none; leading/trailing/repeated forward slashes collapse
to one separator. Every nonempty segment follows the policy above; normalized length is at most 512
characters. Backslashes, dot segments and unsafe characters are rejected. ContainerName requires
3–63 lowercase ASCII letters/digits/hyphens, alphanumeric ends and no consecutive hyphens.
Special Azure containers such as $root are intentionally outside this configuration contract.

The reader never falls back or uses ambient cultures. Explicit TemplateRequest.Version throws
NotSupportedException before downloading. formatVersion, logical versions and Azure native VersionId
are separate concepts; no native version ID is selected or exposed as a logical version.

### Strict format v1

```json
{
  "formatVersion": 1,
  "subject": "Order {{ order_number }} confirmed",
  "htmlBody": "<p>Hello {{ customer_name | html.escape }}</p>",
  "textBody": "Hello {{ customer_name }}"
}
```

formatVersion must be the integer 1. Subject must be a string; each body is optional/null or a string,
and at least one body must be non-null. Empty strings are valid. Property names are case-sensitive;
unknown and duplicate names (including escaped equivalents) are rejected. Future format versions
fail explicitly. Comments, trailing commas, malformed JSON/UTF-8, invalid string escapes and unsupported
shapes fail. System.Text.Json parses UTF-8 directly after validating all bytes, accepting one leading
UTF-8 BOM. No UTF-16 or code-page detection occurs. Parser depth is bounded at eight.

The internal parser returns Core content directly; no public storage DTO is needed. It performs no
Scriban validation or HTML sanitization. application/json is recommended, but Content-Type is not
trusted as a format discriminator: application/octet-stream is equally acceptable.

### Download, bounds and metadata

DownloadStreamingAsync obtains content and metadata together, with no preliminary property request
and no multipart retry protocol. SDK retry behavior remains under application client configuration.
The successful single-object snapshot supplies the complete template ETag and LastModified;
TemplateMetadata.Version remains null even if the response includes an Azure native version.

MaxTemplateBlobSize defaults to 4 MiB and accepts 1 byte through 16 MiB, including BOM and JSON overhead.
The default comfortably holds three 128-Ki UTF-16 components even with JSON escaping; Scriban's
per-component limits remain independent and are not raised by storage configuration.
Reported length is checked before allocation, copying enforces the bound plus one sentinel byte,
and a mismatch with reported length is rejected. All buffers are bounded; JSON/string allocations
still add overhead to the configured physical-byte limit. Streams and raw responses are disposed on
success, parsing/size errors and cancellation. Cancellation reaches SDK calls and stream reads and
is checked around bounded synchronous parsing; parsing is not preemptible mid-operation.

Only HTTP 404 with BlobNotFound becomes null. ContainerNotFound, other 404 codes, authentication,
authorization, throttling, conflicts, server/network failures propagate. AzureBlobTemplateException
covers MailStencil content/format/size/length failures and retains the exact request.
Neither provider failures nor malformed content trigger culture fallback.

### Runtime, future compatibility and verification limits

Core's service supplies fallback and five-minute positive caching unchanged; direct Azure reader
calls bypass that cache. Unit runtime composition covers fallback, cache reuse, new-specific-variant
visibility, model-dependent rendering and explicit html.escape. No Azure logic was added to Core.

Future Authoring can write one complete content document atomically and use its aggregate ETag for
optimistic concurrency. That requires a separate writer capability. Adding document fields requires
an intentional format evolution decision because v1 rejects unknown fields. Logical activation/history/
rollback and any use of native versions remain future design work, not implicitly supported features.

Azurite tests are opt-in and CI-ready; see [instructions](azurite-testing.md). During Milestone 5,
Docker Desktop's Linux engine was unavailable on this host, so those tests did not run then.
Milestone 7 later ran all emulator cases successfully. Live Azure authentication, permissions,
network and SDK behavior remain deployment verification concerns.
No Milestone 6 work is included.

### Milestone 5 final verification

On Windows with .NET SDK 10.0.401:
- dotnet restore: passed.
- dotnet build -c Release: passed, 0 warnings and 0 errors.
- dotnet test -c Release: 310 passed, 0 failed (46 Core, 125 Scriban, 73 FileSystem, 66 Azure units).
- Seven Azurite methods skipped during the Milestone 5 normal run, representing 11 opt-in cases when
  theories expand. All 11 were run separately during Milestone 7.
- The deterministic Azure/Core/Scriban runtime composition test passed.
- Release exported API inspected: exactly AzureBlobTemplateOptions, AzureBlobTemplateException and
  MailStencilAzureBlobServiceCollectionExtensions. Generated XML contains all nine documented entries.
  Nullable reference types and warnings-as-errors remain enabled; existing public contracts are unchanged.

## ASP.NET Core experience and logging — Milestone 6

Library public API remains unchanged. Core now references Microsoft.Extensions.Logging 10.0.11,
and Core/FileSystem/Azure registration calls AddLogging. Internal typed loggers report Debug cache,
exact-read and fallback signals; fixed Warning messages distinguish validation, rendering and
provider-content failures. No providers or filters are installed by the library. No output provider
is required for correct operation. Logging does not add shared mutable render state.

No model/template/rendered values, paths, blob URIs, credentials, diagnostic messages or exception
objects are included in library logs. Only Debug orchestration events include structured logical
identity; applications must treat those identifiers according to their own sensitivity. Providers
log content failures only, avoiding duplicate Core and Azure HTTP/retry events. Cancellation and
exception propagation semantics are unchanged. See [the complete policy](aspnet-core.md#logging-policy).

The .NET 10 minimal API sample binds existing options and explicitly requests ValidateOnStart for
Core and FileSystem. This remains an application decision rather than forcing host lifecycle changes
into libraries. Validation checks configuration only. Sample-relative template paths resolve against
content root; resources are copied to build/publish output. The normal endpoint resolves only
IEmailTemplateService. Existing content, renderer, reader and service responsibilities remain intact.

GET /preview/order-confirmation returns Subject, HtmlBody and TextBody for fixed example order data.
An optional culture query demonstrates fallback while retaining requested numeric formatting.
English default and Italian parent variants demonstrate snake_case and explicit html.escape.
Expected exception categories become distinct sanitized problem responses without diagnostic/stack
disclosure. A documented alternative uses an application-owned BlobServiceClient and replaces the
FileSystem registration; there is no new provider selector or Azure.Identity library dependency.

The new tests/MailStencil.Sample.AspNetCore.Tests project covers the actual HTTP endpoint, binding/
startup validation, sanitized errors, no-provider logging, captured events/privacy, concurrent service
calls, Azure configuration/registration orders with an SDK HTTP double, and the one-reader invariant.
The Azure tests prove no network request is required to validate/start the host. Existing Console and
Azurite tests remain intact. The Azurite workflow was reviewed statically and remains opt-in.

At Milestone 6, cross-platform work remained: execute on Linux/macOS, use non-symlink template and
temporary ancestors (notably macOS /var), and verify timestamp/replacement/link privileges on each host.
The provider's intentional link rejection and metadata consistency limitations remain unchanged.
This milestone does not implement Milestone 7 hardening, Authoring, writers, version management,
distributed caching, new storage providers, CLI or NuGet release preparation.

### Milestone 6 final verification

Windows / .NET SDK 10.0.401:
- dotnet restore succeeded.
- dotnet build -c Release succeeded with 0 warnings and 0 errors.
- dotnet test -c Release: 332 passed, 0 failed; seven opt-in Azurite methods skipped.
  Breakdown: 46 Core, 125 Scriban, 73 FileSystem, 66 Azure and 22 new ASP.NET/developer-experience cases.
- Console sample ran successfully with subject Order MS-123 confirmed and explicit HTML escaping.
- ASP.NET sample started in Production on localhost:5080. Actual HTTP requests returned all three
  components: default subject Order MS-123 confirmed / total 42.50; it-IT fell back to it with subject
  Ordine MS-123 confermato / total 42,50. Both HTML bodies contained Ada &amp; friends.
  The verification process was stopped after requests completed.
- Compiled exported types remain 19 Core, 1 Scriban, 3 FileSystem and 3 Azure; no library public API
  growth. XML documentation, nullable and warnings-as-errors remain enabled.
- Azurite was not run during Milestone 6; Milestone 7 later ran all 11 cases. Linux/macOS remain unverified.

## Milestone 7 hardening findings

### Issues found and fixes made

The production review found one resource-limit defect in Scriban schema construction. The schema
limit counted distinct CLR types but did not count exposed properties, so a generated declared type
could contain more than 10,000 members while using only one property type. Schema construction now
counts every exposed declared member and fails with the existing General/MSV007 validation diagnostic
above the 10,000-member boundary. Tests cover limit-1, limit and limit+1. Projection's independent
10,000-node runtime budget is unchanged.

FileSystemTemplateException accepted a runtime-null message despite its non-null public annotation.
It now throws ArgumentNullException, matching AzureBlobTemplateException and the rest of the public
null contract. This changes no signature. Test-owned temporary roots now use the physical
`/private/var` spelling on macOS so the suite does not weaken or accidentally trip the production
provider's intentional rejection of symlink ancestors. The existing Azurite job now pins the exact
official emulator image digest exercised in this milestone.

No other production defect justified a public API change. The compiled API remains 19 Core, one
Scriban, three FileSystem and three Azure Blob exported types. Nullable annotations, XML documentation,
warnings-as-errors, enum numeric values and existing dependency directions remain unchanged.

### Threat model and security findings

Application configuration, DI registrations, model types and model getters are trusted. Template text
and remotely stored Azure JSON may be untrusted; model values are potentially untrusted and log-sensitive.
The renderer exposes a detached ScriptObject/ScriptArray/primitive graph derived only from `typeof(TModel)`,
uses a fail-closed AST allowlist, no template loader/eval/reflection/CLR method access, fresh contexts and
bounded source/AST/loop/projection/output work. This is bounded in-process execution, not a secure process
sandbox. A getter or enumerable is application code and can block, mutate external state, throw or perform
I/O before MailStencil can regain control; isolate truly hostile workloads at the process/OS boundary.

The FileSystem base tree is trusted application-controlled storage. Segment validation, containment and
reparse checks protect against ordinary path traversal and known link redirection, but portable .NET
metadata checks cannot provide atomic handle-level confinement against hostile writers, hard links,
mount changes or all TOCTOU races. Azure content is size-bounded and strictly parsed; TLS endpoint,
credentials, authorization, retry policy and service availability belong to the application-owned client.

MailStencil does not automatically sanitize or HTML-escape model values. Untrusted values inserted into
HtmlBody should use `html.escape`. Unescaped-output warnings remain a possible future hardening feature
because a partial detector could create false confidence. Subject is returned verbatim, including CR/LF;
the email sender must enforce any required header-injection policy.

### Runtime, cancellation, disposal and concurrency

All registered runtime services remain singleton-oriented and build no temporary IServiceProvider.
Options are validated and snapshotted; custom reader/renderer dependencies must also be safe singleton
dependencies. Every render and provider read uses per-call mutable state. Sequential and concurrent tests
cover same/different templates, cultures, models, components and local names; no Scriban state leakage was
found. MailStencil owns and disposes its private MemoryCache. It disposes filesystem streams and Azure
download streams/responses on success, errors and cancellation, and never disposes the application-owned
BlobServiceClient.

Cancellation tokens reach service lookup/cache checkpoints, readers, asynchronous filesystem reads,
Azure SDK/stream operations, model projection checks and Scriban execution. OperationCanceledException
is not wrapped or logged as failure. Bounded synchronous reflection, Template.Parse, JSON parsing,
filesystem metadata calls and arbitrary application getters/enumerators cannot be preempted mid-call.
No sync-over-async, async void, Task.Run wrapping or CancellationToken.None was found in production paths.

### Cache and culture findings

The source cache remains correct: absolute expiry, zero-duration disablement, positive exact-source entries
only, ordinal case-sensitive name/culture/version keys, no fallback-resolution entry, cancellation checks,
no rendered/model/error caching and disposal with the service. Concurrent cold requests may duplicate
reads by design. There is no public size limit, so high-cardinality identities and large configured source
limits can increase process memory until expiration; deployments should bound identity cardinality or
disable the cache where that is not possible. Distributed caching and single-flight coordination remain
future work.

Culture fallback uses CultureInfo and its Parent chain, including script/region parents. Formatting always
uses the original requested/configured culture; explicit null is invariant/default and remains distinct
from DefaultCulture and ambient culture. In invariant-globalization mode, non-invariant culture creation
is a host runtime limitation and fails deterministically rather than being manually reinterpreted.

### Provider and resource-limit findings

FileSystem continues to reject traversal, both separators, rooted/UNC/drive-relative paths, ADS/colon,
reserved devices, mismatched case, links/reparse points and unexpected file types. The before/read/after
algorithm detects tested replacement, optional-body addition/removal, short read, growth and disappearance
and retries exactly three times. Same-size replacements preserving every observed timestamp and identity,
coarse/network metadata, hard links and hostile races remain documented limitations; no transactional
snapshot is promised.

Azure returns null only for status 404 plus exact BlobNotFound. ContainerNotFound, other 404 responses,
401/403, 429, 5xx and network failures remain Azure RequestFailedException/SDK errors. MailStencil wraps
only malformed/unsupported/oversized/inconsistent content. Reported and actual lengths are bounded and
matched; BOM/UTF-8, depth-eight strict JSON, escaped duplicate fields, unknown fields, formatVersion,
ETag and LastModified behavior remain covered. Content-Type and native VersionId do not change semantics.

Storage byte limits are validated between 1 and 16 MiB and use bounded `limit + 1` reads without overflow.
Scriban retains 128-Ki UTF-16 component source, AST depth 64, loop 1,000, schema/projection 10,000,
projection depth 32, model-string/output 1-Mi limits. Boundaries are tested where independently observable.

### Logging, dependencies, performance and platform status

MailStencil-generated Warning logs remain fixed and sanitized, with no exception object. Debug state is
limited to logical Name/Culture/Version and fallback/cache events. Sentinel tests inspect formatted message,
structured state and exception fields and found no model values, template/rendered content, absolute paths,
blob URIs, SAS/credential data, diagnostics or inner exceptions. Host, Azure SDK and application logging are
separate and must apply their own privacy policy.

`dotnet package list --vulnerable --include-transitive` reported no findings. The deprecation audit reported
only the test-only xunit 2.9.3 stack as Legacy, recommending the major xunit.v3 migration; no production
package is deprecated. The outdated audit found Microsoft.Extensions 10.0.11 to 10.0.12 maintenance updates
and test-tool major/minor updates (Microsoft.NET.Test.Sdk 17.14.1 to 18.10.0 and xunit runner 3.1.4 to
4.0.0). Scriban 7.4.0 and Azure.Storage.Blobs 12.29.2 were current. The xunit v3/runner migration is deferred
because it is test-only and needs a deliberate runner, attribute/gating and CI compatibility pass; it does
not affect shipped binaries. No package was changed without a security or correctness reason, and there is
no unresolved known vulnerable production dependency.

Schema reflection and Scriban parsing are rebuilt for each validation/render; model projection is eager,
filesystem reads materialize parts, and Azure buffers one bounded JSON blob. These are the likely hotspots.
An internal bounded parse/schema cache may help after measurement, but cancellation, declared-type/member
policy identity and eviction must stay correct. No speculative cache or public performance option was added.
Trimming and Native AOT are not guaranteed because reflection metadata is required.

The normal suite has no arbitrary sleeps or order-dependent global state; culture changes are restored,
temporary resources are uniquely owned and Azure unit tests use deterministic transports. Link tests skip
only when the host cannot create links. Test roots now account for the macOS `/var` alias, but actual Linux
and macOS execution, timestamp precision, replacement/open sharing and network filesystem behavior remain
pre-release work. Windows does not prove those platforms. The normal suite is prepared for future
windows-latest/ubuntu-latest/macos-latest CI without weakening production security.

The opt-in Azurite gate cannot silently skip once `MAILSTENCIL_AZURITE=1`; setup/connection failures fail,
the connection string is fixed to local development storage, containers are unique and cleaned, and the
workflow always stops the emulator. Docker 29.7.2 was available for Milestone 7: all 11 Azurite cases ran
and passed against the pinned image. This is not live Azure verification.

### Milestone 7 final verification

Windows / .NET SDK 10.0.401:

- `dotnet restore` passed.
- `dotnet build -c Release` passed with 0 warnings and 0 errors.
- The normal Release suite passed 336 tests: 46 Core, 129 Scriban, 73 FileSystem, 66 Azure unit and
  22 ASP.NET/developer-experience cases. Seven opt-in Azurite methods were skipped in the normal run.
- A separate enabled Azurite run passed all 11 expanded emulator cases, for 347 successful executions
  across normal and integration runs.
- The Console sample exited successfully with all three components, invariant-preserving fallback and
  explicit HTML escaping. The ASP.NET sample started in Production; real HTTP requests to its default
  and `it-IT` preview endpoints returned the expected subjects, escaped HTML and culture-specific totals.
- The compiled Release public API was inspected after the fixes and retained exactly 19 Core, one Scriban,
  three FileSystem and three Azure Blob exported types. Generated XML, nullable annotations and
  warnings-as-errors remained clean.
- Dependency vulnerability audit had no findings; deprecation found only the deferred test-only xunit v2
  stack. The deliberate outdated results and upgrade decisions are recorded above. Linux/macOS execution,
  xunit v3 migration and live Azure remain pre-release concerns.

## Milestone 8 — Authoring compatibility review

### Runtime API freeze conclusion

The current runtime API does not block future Authoring. No public or production-code change is required
before 1.0. Runtime capabilities should remain small and stable, while Authoring composes beside them:

- `ITemplateReader` remains an exact, read-only snapshot capability. It should never acquire write,
  delete, listing, history or activation members.
- `ITemplateRenderer` remains storage- and identity-independent and already supports unsaved preview.
- `ITemplateValidator` remains the static validation capability for unsaved content and a declared model.
- `IEmailTemplateService` remains runtime orchestration for load, fallback, positive source cache and render.
  Authoring can resolve `ITemplateReader` directly when it needs source metadata such as an ETag.
- `TemplateRequest`, `EmailTemplateContent`, `EmailTemplateSource`, `TemplateMetadata` and
  `RenderedEmailTemplate` have sufficient separation of identity, source text, snapshot metadata and
  rendered output. Their sealed shapes encourage composition rather than inheritance.

Adding required members to any existing interface would burden read-only providers and custom consumers.
No future workflow reviewed here requires that. Future interfaces and command/result models can be added
independently without changing existing method signatures or constructor shapes.

### Candidate future capability boundaries

The following are design roles, not frozen type names or public API proposals:

| Capability | Responsibility | Why it stays separate |
| --- | --- | --- |
| Write/create/update | Persist complete content, optionally as a new logical version and with an expected concurrency token | Read-only credentials and providers remain valid; create/update semantics need explicit commands/results |
| Delete | Delete a variant or version with explicit conditional behavior | Delete permission and retention policy commonly differ from write permission |
| Catalog/query | Page through names, cultures and versions with provider-specific consistency/cost | Exact runtime reads must not require enumeration permission or an efficient list operation |
| History | Read version summaries and audit metadata | Some providers have no history; native storage history need not equal logical history |
| Activation | Atomically or conditionally select an active logical version | Activation has a different concurrency target from editing version content |
| Model schema | Describe the exact declared model surface used by validation/rendering | It belongs to the template-language/model policy, not storage |
| Authoring orchestration | Coordinate validate, preview, save and policy | It composes capabilities and application authorization without enlarging runtime contracts |

A provider may implement and register any useful subset. Normal DI availability is sufficient for initial
capability discovery: an application resolves the optional interface it needs. A `CanWrite`/`CanList` flags
object would duplicate registration truth and can drift from credentials or runtime availability, so no
formal capability enum is justified now. If a future UI needs richer explanations, an additive descriptor
can report semantics such as pagination, strong conditional writes or immutable history without changing
the operational interfaces.

This separation preserves read-only FileSystem mounts, read-only Blob credentials, custom HTTP and
embedded-resource readers, and third-party readers that implement only `ITemplateReader`. Writer and
reader instances may also use different credentials or deployment identities.

### Unsaved validation and preview

The edit flow already composes without storage:

```text
editor text
  -> EmailTemplateContent
  -> ITemplateValidator.ValidateAsync<TModel>
  -> ITemplateRenderer.RenderAsync<TModel>(content, sampleModel, culture)
  -> future writer capability
```

`EmailTemplateContent` deliberately has no identity and can represent a draft before any read or write.
Validation does not require a model instance or execute getters; preview takes the caller's sample model
and explicit formatting culture. Both use the same Scriban implementation and declared `typeof(TModel)`
contract. A separate preview abstraction would only wrap existing calls and is not needed unless a future
product needs orchestration policy beyond validation and rendering.

Validation-before-save belongs in Authoring orchestration or the application workflow. Storage remains
model-agnostic. The application can associate an editor/template kind with `TModel` through its typed
workflow or a separate schema-key registry. CLR `Type` information does not belong in
`EmailTemplateContent`; a future portable logical schema identifier, if needed, is authoring metadata.
The existing generic-only validator/renderer are sufficient for typed applications. A host that discovers
model contracts dynamically can add a separate Type-based Authoring adapter or invoke a closed generic
workflow; it does not require adding a method to either existing interface.

### Schema and naming-policy reuse

Scriban's internal `ModelSchema` is already the single source for declared `typeof(TModel)`, public readable
non-indexed property discovery, collection element types, supported scalar/object restrictions,
snake_case naming, collision detection, projection and validation member lookup. A future schema generator
must traverse this same internal graph and map it to an immutable public schema result. It must not expose
`ModelSchema`, `PropertyInfo`, Scriban runtime objects or a second reflection implementation.

The neutral schema capability/result can be added later in a package visible to both the Scriban adapter
and Authoring; alternatively, a small Scriban/Authoring bridge can expose the mapping. Package placement
must preserve a single implementation of model discovery. This is an additive packaging decision, not a
reason to change the current runtime API.

The current member-policy seam can become instance/options-driven later. An additive Scriban options
overload can preserve the existing no-argument registration and snake_case default while selecting a
future SnakeCase, CamelCase or Original policy. Renderer, validator and schema generation must consume the
same snapshotted policy. Changing the default after 1.0 would break stored templates behaviorally and must
not occur; a chosen non-default policy likewise becomes part of the application's template contract.

### Content and authoring metadata

`EmailTemplateContent` should remain only Subject/HtmlBody/TextBody. Description, tags, author, status,
created/updated timestamps, logical schema association and editing state can live in a future draft,
definition or command that composes:

```text
identity + EmailTemplateContent + authoring metadata + concurrency condition
```

`EmailTemplateSource` remains a read snapshot, not an edit model. `TemplateMetadata` remains optional
runtime snapshot metadata. Its string ETag is intentionally provider-neutral and opaque; adding Azure SDK
types would couple Core and still would not describe FileSystem guarantees. Authoring operation results may
need additional tokens for version content and activation state rather than overloading one runtime ETag.

### Optimistic concurrency by provider

Azure stores one current variant as one complete JSON blob, so the `TemplateMetadata.ETag` returned by a
read is sufficient as the opaque expected token for a future conditional `If-Match` replacement of that
blob. The writer translates the string to Azure SDK conditions internally and reports a provider-neutral
conflict. Creates can use `If-None-Match`; deletes can be conditional. MailStencil must not require callers
to reference Azure `ETag` or request-condition types.

Logical version activation may update a different active pointer or current blob, so its condition is a
separate operation token. A content ETag must not be assumed to protect both version content and activation
state. Native Blob VersionId can be used internally for implementation or recovery but is not the
MailStencil logical version.

FileSystem `LastModified` is observational metadata and cannot provide a strong compare-and-swap guarantee.
A future writer can offer documented best effort using an expected timestamp and content hash, but checks
remain vulnerable to races. Stronger designs include staging complete files/directories, OS-supported
atomic replacement, a content-addressed version directory plus conditionally replaced active manifest, or
a writer-owned lock/commit protocol. Network filesystems and platform replacement semantics differ. A
FileSystem writer may reject unsupported strong conditions; all providers need not claim identical
concurrency guarantees.

### Logical versions, activation and rollback

The related version, format and concurrency concepts remain distinct:

- `TemplateRequest.Version` is an opaque MailStencil logical version; null means active/current.
- `TemplateMetadata.Version` may report the resolved logical version, especially for an active read.
- Azure Blob VersionId is a provider-native implementation detail.
- JSON `formatVersion` identifies the storage document schema.
- ETag is an opaque change/concurrency token, not a version name.

These contracts are sufficient for provider-independent logical versions. Existing FileSystem and Azure
readers correctly reject explicit versions today; a future internal reader implementation can honor them
without changing `ITemplateReader`. Creating a version should not implicitly activate it unless the future
command says so. Activation selects the version returned by a null-version request. Rollback is normally an
activation of a prior immutable version, with conditional protection against racing activations; history
and activation remain optional capabilities rather than reader requirements.

Azure could retain the current active `template.json` while storing immutable logical-version blobs, or
later read an explicit active pointer. FileSystem could retain the current active three-file layout while
storing version directories and an optional manifest. Those physical choices must preserve the public null
equals active semantics and clearly define aggregate snapshot/ETag behavior.

### Storage-format evolution

Azure JSON format v1 remains strict and must not silently accept authoring fields. A future writer can emit
v1 for current content. Rich metadata should use provider metadata/sidecars with explicit consistency, or
an intentionally supported `formatVersion: 2`; the v1 interpretation remains unchanged. Unknown-field
rejection is useful protection against silently ignoring content written for a newer reader.

The FileSystem reader examines only `subject.txt`, `body.html` and `body.txt`; stable unrelated files are
currently ignored. A future writer can therefore write the existing layout without runtime changes. A
stable `manifest.json` could be additive at the filesystem level, but it has no meaning to the current
reader and concurrent changes may affect directory metadata/retry behavior. If a manifest later becomes
part of identity, versions, activation or aggregate consistency, the reader/writer protocol must be
versioned and updated deliberately rather than treating the ignored file as implicitly authoritative.

### Listing, CLI and package direction

Listing stays outside `ITemplateReader`. Blob/object listing may be paged and eventually consistent;
Google Drive-style discovery can be expensive or ambiguous; embedded/HTTP readers may have no enumeration
endpoint; credentials may allow exact reads but deny list. A future catalog capability should expose
pagination/cancellation and document ordering, consistency and permission behavior rather than promise a
cheap complete collection.

A future CLI can compose the same capabilities: validate and preview use current content/validator/renderer;
push and delete use writer capabilities; exact pull uses the reader; discovery uses the catalog; history
and activation use their optional interfaces. It needs configuration and model/schema selection, not
special methods on Core runtime services.

The preferred future package direction is:

```text
MailStencil.Core                         stable runtime contracts/models
MailStencil.Authoring                   authoring orchestration/contracts -> Core
MailStencil.Scriban authoring adapter    schema mapping that reuses internal ModelSchema
MailStencil.FileSystem authoring adapter writer/catalog capabilities -> Authoring + provider
MailStencil.AzureBlob authoring adapter  writer/catalog/history/activation -> Authoring + provider
MailStencil.Cli                          composes runtime + selected authoring/provider adapters
```

Adapters may become separate packages (for example, `MailStencil.AzureBlob.Authoring`) to keep runtime-only
provider packages free of an Authoring dependency and avoid granting write capability accidentally. Exact
names and whether a small neutral schema abstraction belongs in Core or an Authoring abstractions package
remain future decisions. Dependency direction must avoid Core depending on Scriban/Azure/FileSystem and
must avoid duplicating `ModelSchema` to preserve that package ideal. Provider adapters must likewise reuse
or internally refactor the existing path/blob-name and storage-format policies instead of creating writer
rules that disagree with their readers; this is an implementation/package-access issue, not public API.

### Cache invalidation

The current five-minute private positive source cache is not an architectural blocker, but same-process
Authoring must define read-after-write behavior. Today an authoring host can set `CacheDuration` to zero and
preview unsaved content directly. Later, an additive optional invalidation/generation capability can be
implemented by the internal runtime service without changing `IEmailTemplateService`; a writer or Authoring
orchestrator can signal the affected exact identities after a successful commit. Updating/deleting a
variant invalidates that version key; changing activation invalidates the null-version active key.

Simple remove-after-write has a race with an older concurrent load inserting after removal. If strong
same-process freshness is required, use a per-identity/global generation captured before load and checked
before insertion, or ETag revalidation. Cross-process writers require short expiry, notifications, shared
generation state or a future distributed cache protocol. None requires exposing the existing MemoryCache,
and no hypothetical invalidation API is added now.

### Deferred decisions and recommendation

Future work must still define create-versus-replace commands, conditional conflict results/exceptions,
delete retention, catalog pagination, authoring authorization/auditing, schema DTO shape, logical version
syntax, immutable-history guarantees, activation atomicity, per-provider capability semantics, metadata
storage, cache invalidation and cross-process consistency. UI/editor and CLI workflows must also decide how
applications select a declared model/schema without persisting arbitrary CLR type names.

These are additive Authoring design decisions. They do not expose a missing runtime primitive or justify a
pre-1.0 breaking change. The recommendation is to freeze the current runtime public API for 1.0 and develop
future Authoring contracts separately, validating each against read-only and weak-concurrency providers.

### Milestone 8 verification

Milestone 8 changed documentation only. On Windows with .NET SDK 10.0.401, `dotnet restore` succeeded,
`dotnet build -c Release` completed with 0 warnings and 0 errors, and `dotnet test -c Release` passed all
336 normal tests. The seven opt-in Azurite methods were skipped as designed and were not rerun because no
production/provider code changed. The exported runtime API remains the Milestone 7 baseline.

## Milestone 9 — packaging and release-candidate engineering

The four runtime assemblies are the only packable projects: `MailStencil.Core`, `MailStencil.Scriban`,
`MailStencil.FileSystem`, and `MailStencil.AzureBlob`. They share the centrally defined
`0.1.0-preview.1` default, deterministic/portable-symbol settings, package validation, and `.snupkg`
generation. Each package includes its own README, XML documentation, and only its `net10.0` runtime
assembly plus normal NuGet metadata. Project references become ordinary minimum-version package
dependencies; no test, sample, build output, or source tree is shipped.

Core remains the dependency root. Scriban depends on Core and Scriban 7.4.0; FileSystem depends only on
Core; AzureBlob depends on Core and Azure.Storage.Blobs 12.29.2. Consumers still select exactly one reader.
The release work changes no runtime behavior, public type, dependency lifetime, exception, security limit,
storage layout, or logging policy.

`global.json` selects SDK 10.0.401 with `latestFeature` roll-forward. Local developer builds stay
deterministic while compatible .NET 10 feature-band servicing remains possible. `ContinuousIntegrationBuild`
is enabled only when the CI environment property is true. Repository/project URLs and license metadata are
deliberately absent because this working repository has no commit, remote, or license file; inventing them
would make the artifacts misleading. A real remote, initial commit, deliberate license, and verified
security contact are publication blockers. Source Link is SDK-provided once sources are committed and a
real repository URL exists; the current uncommitted candidate cannot claim usable source navigation.

Normal CI runs restore, Release build, and the complete non-Azurite test suite on Windows, Ubuntu, and
macOS. A nonpublishing package job audits dependencies, packs once, inspects package identities, content,
and dependency groups, checks the compiled public API baseline, runs clean FileSystem and Azure DI consumers
against the local packages, and uploads only candidate artifacts. The separate pinned Azurite job remains
opt-in/path-scoped and never targets real credentials.

The local package consumer projects contain only PackageReferences. The verifier copies them to unique
temporary directories, restores MailStencil from the candidate directory plus NuGet.org for third-party
dependencies, runs a real FileSystem render, and resolves the Azure reader/service using an
application-owned `BlobServiceClient` without contacting a service. Package inspection rejects unexpected
payloads and local path/secret sentinels in text metadata.

Package IDs were unregistered in the NuGet.org flat-container lookup performed for this milestone, but
availability is not ownership reservation and must be checked again immediately before publication.
Publishing is intentionally absent. The future release design uses a protected environment and NuGet.org
trusted publishing/OIDC with a short-lived key; long-lived API keys must not be stored.

### Milestone 9 verification and readiness

Windows verification used .NET SDK 10.0.401. Restore and Release build succeeded with zero warnings and
errors. All 336 normal tests passed; the normal run skipped the seven opt-in Azurite methods as designed.
A separate enabled run against the pinned emulator digest passed all 11 expanded Azurite cases. The Console
sample completed, and the ASP.NET Core sample returned expected default and `it-IT` preview responses with
escaped HTML and culture-specific decimal formatting.

The exact solution-level `dotnet pack -c Release -p:Version=0.1.0-preview.1 -o artifacts/packages` command
produced four `.nupkg` and four `.snupkg` files without warnings. Inspection verified IDs, version, net10.0
DLL/XML/PDB placement, README inclusion, dependency groups, absence of test/sample/source/build payloads,
and absence of local paths or secret sentinels in textual package metadata. Both temporary package-only
consumers passed: FileSystem performed a real load/render, and Azure resolved the complete singleton DI
graph with an application-owned client without network access. The compiled 115-entry API baseline passed.

The vulnerability audit found no known direct or transitive vulnerability. No production dependency is
deprecated. The xunit 2.9.3 test stack remains marked Legacy, and the outdated audit still reports the
previously reviewed Microsoft.Extensions 10.0.12 servicing updates plus test-tool major updates; no package
was upgraded solely to be latest. CI now targets Windows, Ubuntu, and macOS, but hosted execution cannot be
claimed until a real remote exists and runs the workflow.

The candidate is **not ready to publish**. Blocking items are a deliberately chosen license, a real public
repository and initial commit, truthful package project/repository URLs, usable Source Link mappings,
verified private security reporting, and a successful hosted cross-platform CI run. Package IDs appeared
available on 2026-09-10 but are not reserved. Recommended follow-up is to resolve those ownership choices,
rerun all audits/pack/smokes from a clean commit, inspect Source Link against that commit, then configure a
protected trusted-publishing workflow. The test-only xunit v3 migration and nonsecurity 10.0.12 servicing
updates remain normal follow-up work rather than publication blockers.
