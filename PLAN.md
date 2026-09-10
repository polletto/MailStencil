# CloudEmailTemplates — Development Plan

## Visione

Creare una libreria open source .NET 10 per la gestione completa del ciclo di vita dei template email.

Il progetto deve separare chiaramente due aree:

```text
AUTHORING
Create / Edit / Validate / Preview
                ↓
             Storage
                ↓
RUNTIME
Load / Cache / Render
                ↓
        Email provider esterno
```

La libreria NON deve occuparsi dell'invio delle email.

Non deve diventare un client SMTP, SendGrid, SES o Azure Communication Services.

La responsabilità del progetto termina con la produzione di:

- Subject renderizzato
- HTML Body renderizzato
- Text Body renderizzato

---

# Obiettivi principali

La piattaforma deve poter:

1. recuperare template email da differenti storage;
2. renderizzare template tramite Scriban;
3. gestire placeholder e modelli strongly typed;
4. validare i placeholder prima del rendering;
5. gestire Subject, HTML Body e Text Body;
6. supportare caching;
7. supportare localization;
8. integrarsi tramite Dependency Injection;
9. permettere in futuro la creazione e modifica dei template;
10. permettere preview e validazione prima del salvataggio;
11. essere facilmente estendibile con nuovi storage provider;
12. essere indipendente dal provider utilizzato per inviare le email.

Target:

```text
.NET 10
```

---

# Principi architetturali

Seguire:

- SOLID
- Dependency inversion
- API pubbliche minimali
- Async-first
- CancellationToken sulle operazioni I/O
- Nullable Reference Types
- TreatWarningsAsErrors
- XML documentation sulle API pubbliche
- thread safety
- dependency injection
- configuration tramite Options
- logging tramite Microsoft.Extensions.Logging

Non introdurre nel Core dipendenze da:

- Scriban
- Azure SDK
- AWS SDK
- Google SDK
- filesystem implementation
- sistemi di invio email

Ogni provider deve essere separato.

---

# Principio Reader / Writer

La progettazione deve distinguere fin dall'inizio:

```csharp
ITemplateReader
```

da:

```csharp
ITemplateWriter
```

Il runtime necessita solamente della lettura.

Un'applicazione deve poter registrare un provider read-only senza ottenere automaticamente capacità di modifica.

Indicativamente:

```csharp
public interface ITemplateReader
{
    Task<EmailTemplateSource?> GetAsync(
        TemplateRequest request,
        CancellationToken cancellationToken = default);
}
```

Il writer verrà implementato dopo la v1.0:

```csharp
public interface ITemplateWriter
{
    Task SaveAsync(
        EmailTemplateDefinition template,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        TemplateIdentifier template,
        CancellationToken cancellationToken = default);
}
```

IMPORTANTE:

la forma definitiva delle interfacce deve essere valutata durante Milestone 1.

Non assumere che gli esempi sopra siano già l'API finale.

---

# Solution structure

```text
CloudEmailTemplates.sln

src/

CloudEmailTemplates.Core
CloudEmailTemplates.Scriban
CloudEmailTemplates.FileSystem
CloudEmailTemplates.AzureBlob

tests/

CloudEmailTemplates.Core.Tests
CloudEmailTemplates.Scriban.Tests
CloudEmailTemplates.FileSystem.Tests
CloudEmailTemplates.AzureBlob.Tests

samples/

CloudEmailTemplates.Sample.Console
CloudEmailTemplates.Sample.AspNetCore
```

Future packages:

```text
CloudEmailTemplates.Authoring
CloudEmailTemplates.AmazonS3
CloudEmailTemplates.GoogleCloudStorage
CloudEmailTemplates.GoogleDrive
CloudEmailTemplates.Redis
CloudEmailTemplates.Cli
```

---

# Core domain

CloudEmailTemplates.Core deve contenere solo concetti indipendenti dall'infrastruttura.

Concetti da valutare:

```text
ITemplateReader
ITemplateRenderer
IEmailTemplateService

EmailTemplateDefinition
EmailTemplateSource
RenderedEmailTemplate

TemplateIdentifier
TemplateRequest
TemplateMetadata

TemplateValidationResult
TemplateValidationError

TemplateNotFoundException
TemplateRenderingException
TemplateValidationException

MailStencilOptions
```

Non creare automaticamente tutti questi tipi.

Favorire un'API piccola.

---

# Runtime API desiderata

Obiettivo ergonomico:

```csharp
var result =
    await templateService.RenderAsync<OrderConfirmationModel>(
        "order-confirmation",
        new OrderConfirmationModel
        {
            CustomerName = "Mario",
            OrderNumber = "12345",
            Total = 89.90m
        },
        cancellationToken);
```

Output:

```csharp
result.Subject
result.HtmlBody
result.TextBody
```

La futura implementazione di Authoring non deve richiedere breaking change significativi a questa API.

---

# Email template

Un template è composto da:

```text
Subject
HTML Body
Text Body
```

Almeno uno tra HTML Body e Text Body deve essere presente.

Esempio storage:

```text
templates/
    order-confirmation/
        subject.txt
        body.html
        body.txt
```

subject.txt:

```text
Ordine {{ order_number }} ricevuto
```

body.html:

```html
<h1>Ciao {{ customer_name }}</h1>

<p>
Il tuo ordine {{ order_number }} è stato ricevuto.
</p>

<p>Totale: {{ total }}</p>
```

body.txt:

```text
Ciao {{ customer_name }}

Il tuo ordine {{ order_number }} è stato ricevuto.

Totale: {{ total }}
```

---

# Storage abstraction

Il sistema deve supportare storage intercambiabili.

v1.0:

```text
FileSystem
Azure Blob Storage
```

Future:

```text
Amazon S3
Google Cloud Storage
Google Drive
OneDrive
Database
```

L'astrazione non deve contenere concetti Azure-specific.

Quando disponibili, i provider devono poter esporre:

```text
ETag
Version
LastModified
```

senza rendere obbligatori tali metadata per tutti gli storage.

---

# Scriban renderer

CloudEmailTemplates.Scriban deve utilizzare Scriban.

Supportare:

- placeholder
- nested objects
- conditions
- loops
- safe functions/filters

Esempio:

```scriban
Ciao {{ customer.name }}

{{ if customer.is_vip }}
Grazie per essere un cliente VIP!
{{ end }}

{{ for item in order.items }}
{{ item.name }} x {{ item.quantity }}
{{ end }}
```

Il motore deve essere sicuro per template modificabili da utenti non sviluppatori.

Non esporre arbitrariamente membri .NET.

---

# Strongly typed templates

Supportare:

```csharp
RenderAsync<TModel>()
```

Esempio:

```csharp
public sealed class OrderConfirmationModel
{
    public required string CustomerName { get; init; }

    public required string OrderNumber { get; init; }

    public required decimal Total { get; init; }
}
```

---

# Naming policy

Supportare almeno:

```text
SnakeCase
CamelCase
Original
```

Esempio:

```text
CustomerName
```

può diventare:

```text
customer_name
```

oppure:

```text
customerName
```

La policy deve essere configurabile.

Default da decidere durante Milestone 1/2.

---

# Placeholder validation

Questa è una feature centrale.

API indicativa:

```csharp
var validation =
    await templateService.ValidateAsync<OrderConfirmationModel>(
        "order-confirmation",
        cancellationToken);
```

Deve rilevare almeno:

- syntax error
- root placeholder inesistente
- proprietà annidata inesistente
- espressioni non valide
- variabili locali correttamente definite

Esempio errore:

```scriban
{{ customer_namme }}
```

Risultato:

```text
Unknown placeholder: customer_namme

Did you mean:
customer_name
```

Quando possibile usare AST Scriban.

NON usare regex come meccanismo principale di validazione.

---

# Scope delle variabili Scriban

Gestire correttamente variabili locali.

Esempio:

```scriban
{{ for item in order.items }}
    {{ item.name }}
{{ end }}
```

`item` è locale.

Non deve essere validato come proprietà root del model.

Considerare:

- loop
- assignments
- functions
- nested scopes
- aliases
- builtin Scriban

---

# Template schema

La v1.0 deve essere progettata affinché sia possibile aggiungere successivamente:

```csharp
TemplateSchema.Create<TModel>();
```

Esempio futuro:

```json
{
  "variables": [
    {
      "name": "customer_name",
      "type": "string",
      "required": true
    },
    {
      "name": "order_number",
      "type": "string",
      "required": true
    }
  ]
}
```

Questo schema servirà all'Authoring layer.

NON è obbligatorio implementare l'intera feature nella v1.0.

È però necessario non impedirla con l'API scelta.

---

# Caching

v1.0:

```text
Microsoft.Extensions.Caching.Memory
```

Supportare configurazione simile a:

```csharp
services.AddCloudEmailTemplates(options =>
{
    options.CacheDuration = TimeSpan.FromMinutes(5);
});
```

Il caching deve essere indipendente dal provider.

L'architettura deve permettere in futuro:

```text
Redis
IDistributedCache
ETag revalidation
version-aware cache
```

senza breaking change importanti.

---

# Localization

Supportare:

```text
it-IT
↓
it
↓
default
```

Esempio:

```text
templates/
    welcome/
        default/
            subject.txt
            body.html

        it/
            subject.txt
            body.html

        en/
            subject.txt
            body.html
```

API:

```csharp
await templateService.RenderAsync(
    "welcome",
    model,
    culture: new CultureInfo("it-IT"));
```

Fallback deterministico e testato.

---

# FileSystem provider

Package:

```text
CloudEmailTemplates.FileSystem
```

Registrazione indicativa:

```csharp
services
    .AddCloudEmailTemplates()
    .AddFileSystemTemplateStore(options =>
    {
        options.BasePath = "./templates";
    });
```

Requisiti:

- async I/O
- path normalization
- traversal protection
- CancellationToken
- LastModified
- localization
- error handling chiaro

---

# Azure Blob provider

Package:

```text
CloudEmailTemplates.AzureBlob
```

Utilizzare:

```text
Azure.Storage.Blobs
```

Registrazione indicativa:

```csharp
services
    .AddCloudEmailTemplates()
    .AddAzureBlobTemplateStore(options =>
    {
        options.ContainerName = "email-templates";
    });
```

Supportare:

- BlobServiceClient tramite DI
- ETag
- LastModified
- streaming
- CancellationToken
- missing blobs
- localization

Il Core non deve dipendere da Azure.

---

# Dependency Injection

Esperienza desiderata:

```csharp
builder.Services
    .AddCloudEmailTemplates()
    .AddScribanRenderer()
    .AddAzureBlobTemplateStore(options =>
    {
        options.ContainerName = "email-templates";
    });
```

Valutare API più ergonomiche se mantengono chiarezza.

Evitare:

- static globals
- service locator
- hidden runtime dependencies

---

# Logging

Usare:

```text
Microsoft.Extensions.Logging
```

Loggare:

- template load
- cache hit/miss
- fallback culture
- validation failure
- rendering failure

NON loggare automaticamente:

- model completi
- dati personali
- HTML completo
- contenuto email completo

---

# Authoring architecture

L'Authoring non fa parte del runtime obbligatorio.

Future package:

```text
CloudEmailTemplates.Authoring
```

Responsabilità:

```text
Create
Edit
Validate
Preview
Save
Delete
List
Schema
```

Workflow futuro:

```text
Edit
 ↓
Validate
 ↓
Preview
 ↓
Save
```

---

# Template creation

API futura indicativa:

```csharp
var template = new EmailTemplateDefinition
{
    Name = "order-confirmation",
    Culture = "it-IT",
    Subject = "Ordine {{ order_number }} ricevuto",
    HtmlBody = """
        <h1>Ciao {{ customer_name }}</h1>
        """,
    TextBody = """
        Ciao {{ customer_name }}
        """
};

await templateManager.SaveAsync(template);
```

---

# Authoring validation

Prima del salvataggio dovrà essere possibile validare il template.

Esempio:

```text
Template cannot be saved.

Unknown placeholder:
customer_namme

Did you mean:
customer_name
```

Il sistema deve poter opzionalmente impedire il salvataggio di template invalidi.

---

# Preview

Future API:

```csharp
var preview =
    await templateManager.PreviewAsync<OrderConfirmationModel>(
        template,
        sampleModel);
```

Preview deve utilizzare lo stesso renderer del runtime.

Non creare due motori di rendering differenti.

---

# Versioning

Feature futura.

Il design deve permettere:

```text
order-confirmation

v1
v2
v3 ← active
```

Future operations:

```text
Create version
Get version
List versions
Activate version
Rollback
```

Non affidare l'API pubblica esclusivamente al versioning nativo di Azure Blob o S3.

I provider possono sfruttare funzionalità native internamente, ma il dominio deve rimanere provider-independent.

---

# CLI futura

Possibile package/tool:

```text
CloudEmailTemplates.Cli
```

Comandi ipotetici:

```bash
cet template validate ./templates
```

```bash
cet template push ./templates --provider azure
```

```bash
cet template pull --provider azure
```

Possibile workflow CI:

```text
Git
 ↓
Pull Request
 ↓
Template validation
 ↓
Merge
 ↓
Push to storage
```

NON implementare CLI nella v1.0.

---

# Testing

Usare xUnit.

## Core

Testare:

- missing templates
- cancellation
- caching
- culture resolution
- fallback
- concurrency

## Scriban

Testare:

- placeholder semplici
- nested properties
- snake_case
- camelCase
- loops
- conditions
- syntax errors
- unknown variables
- nested unknown property
- loop scopes
- assignments
- null values
- unsafe expressions

## FileSystem

Testare:

- loading
- missing file
- localization
- LastModified
- traversal attempts
- cancellation

## Azure

Testare:

- template loading
- ETag
- LastModified
- missing blobs
- localization
- cancellation

Usare Azurite per integration test.

Evitare dipendenza da un account Azure reale.

---

# Samples

## Console

Dimostrare:

```text
Strongly typed model
FileSystem
Validation
Render
Subject
HTML
Text
```

## ASP.NET Core

Dimostrare:

```text
Dependency Injection
Options
Logging
Caching
Azure Blob
Strongly typed rendering
Localization
Validation
```

---

# NuGet

Package v1.0:

```text
CloudEmailTemplates.Core
CloudEmailTemplates.Scriban
CloudEmailTemplates.FileSystem
CloudEmailTemplates.AzureBlob
```

Preparare:

- PackageDescription
- PackageTags
- RepositoryUrl
- README
- LICENSE
- symbol packages
- SourceLink
- deterministic build
- package validation

---

# CI

GitHub Actions.

Pull request:

```text
dotnet restore
dotnet build
dotnet test
dotnet pack
```

Release configuration.

Target .NET 10.

Publish NuGet separato.

Non pubblicare automaticamente durante sviluppo.

---

# Roadmap

## v1.0 — Runtime foundation

```text
Core
Scriban
Strongly typed rendering
Placeholder validation
FileSystem
Azure Blob
MemoryCache
Localization
Dependency Injection
Logging
Tests
Samples
NuGet
CI
```

## v1.1 — Authoring

```text
ITemplateWriter
Create
Update
Delete
List
Template schema
Preview
Validation before save
```

## v1.2 — Versioning

```text
Template versions
Active version
Rollback
Version listing
```

## v1.3 — Cloud providers

```text
Amazon S3
Google Cloud Storage
```

Google Drive potrebbe essere incluso qui o nella release successiva in base alla complessità API.

## v1.4 — Developer tooling

```text
CLI
validate
push
pull
CI integrations
```

## v1.5+

```text
Google Drive
OneDrive
Redis / distributed caching
Database provider
```

## Future major version / separate project

```text
Web template manager
Template editor
Variable picker
Preview UI
WYSIWYG
Role-based editing
Approval workflow
```

---

# Milestone 1 — Architecture

Implementare SOLO:

```text
solution
projects
Core contracts
Core models
DI foundation
options foundation
```

NON implementare ancora:

```text
Scriban
FileSystem
Azure
Caching
Authoring
```

Obiettivo principale:

definire un'API pubblica che possa sopravvivere alla crescita della libreria.

Durante questa milestone considerare esplicitamente:

```text
Reader vs Writer
metadata
localization
versions future
schema future
distributed cache future
additional providers
```

Prima di finalizzare:

1. mostrare public API;
2. spiegare decisioni;
3. evidenziare possibili future breaking changes;
4. verificare estensibilità verso S3 e Google Drive;
5. verificare compatibilità futura con Authoring;
6. eliminare astrazioni non necessarie.

---

# Milestone 2 — Scriban

Implementare:

```text
renderer
strong typing
naming policies
AST validation
safe execution
validation diagnostics
```

Test completi.

---

# Milestone 3 — FileSystem

Implementare FileSystem provider.

Creare Console sample.

---

# Milestone 4 — Localization + caching

Implementare:

```text
culture resolution
fallback
MemoryCache
```

Testare concurrency e invalidazione temporale.

---

# Milestone 5 — Azure Blob

Implementare Azure provider.

Usare Azurite nei test.

---

# Milestone 6 — ASP.NET Core experience

Finalizzare:

```text
DI
options
logging
sample ASP.NET
configuration
```

---

# Milestone 7 — Production hardening

Review:

```text
public API
security
Scriban sandbox
path traversal
concurrency
thread safety
allocations
stream disposal
CancellationToken
exception model
nullable contracts
logging privacy
```

---

# Milestone 8 — Authoring compatibility review

NON implementare ancora Authoring.

Verificare però che la v1 possa successivamente supportare:

```text
ITemplateWriter
TemplateManager
Schema
Preview
Versions
Rollback
CLI
```

senza modifiche distruttive al Core.

Creare eventualmente ADR/documentazione delle decisioni.

---

# Milestone 9 — NuGet readiness

Preparare:

```text
README
LICENSE
CHANGELOG
samples
package metadata
SourceLink
symbols
CI
package validation
```

Generare package locali.

NON pubblicare ancora.

---

# Definition of Done v1.0

La release è pronta quando:

- solution compila senza warning;
- tutti i test passano;
- API pubblica documentata;
- strongly typed rendering funzionante;
- Subject/HTML/Text funzionanti;
- placeholder validation affidabile;
- Scriban sandbox verificato;
- FileSystem funzionante;
- Azure Blob funzionante;
- MemoryCache funzionante;
- localization funzionante;
- cancellation supportata;
- logging sicuro;
- samples funzionanti;
- package NuGet generabili;
- CI verde;
- Core indipendente da cloud e Scriban;
- design verificato rispetto al futuro Authoring layer.

---

# Regola di sviluppo

NON implementare tutte le milestone insieme.

Procedere una milestone per volta.

Alla fine di ogni milestone:

1. build;
2. test;
3. review public API;
4. riepilogo decisioni;
5. rischi;
6. technical debt;
7. possibili breaking changes.

Qualità e stabilità dell'API hanno priorità rispetto al numero di feature.