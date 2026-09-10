---
title: MailStencil
description: Storage-agnostic, strongly typed email templates for .NET.
---

<div class="mailstencil-hero-logo">
  <img class="mailstencil-logo-light" src="../assets/branding/mailstencil-logo-light.png" alt="MailStencil">
  <img class="mailstencil-logo-dark" src="../assets/branding/mailstencil-logo-dark.png" alt="MailStencil">
</div>

# MailStencil

**Storage-agnostic, strongly typed email templates for .NET.**

MailStencil is a .NET 10 library that retrieves templates from pluggable storage, validates them
against strongly typed model contracts, caches source templates, and renders them with Scriban.
It returns rendered content and does not send email.

```text
Retrieve -> Validate -> Cache -> Render
```

<div class="mailstencil-actions">
  <a class="btn btn-primary" href="articles/getting-started/installation.md">Get Started</a>
  <a class="btn btn-outline-primary" href="api/MailStencil.yml">API Reference</a>
  <a class="btn btn-outline-primary" href="https://www.nuget.org/packages/MailStencil.Core">NuGet</a>
  <a class="btn btn-outline-primary" href="https://github.com/polletto/MailStencil">GitHub</a>
</div>

## Packages

| Package | Purpose |
| --- | --- |
| [MailStencil.Core](https://www.nuget.org/packages/MailStencil.Core) | Contracts, dependency injection, localization fallback, and source caching |
| [MailStencil.Scriban](https://www.nuget.org/packages/MailStencil.Scriban) | Bounded Scriban rendering and static validation |
| [MailStencil.FileSystem](https://www.nuget.org/packages/MailStencil.FileSystem) | Restrictive read-only filesystem template reader |
| [MailStencil.AzureBlob](https://www.nuget.org/packages/MailStencil.AzureBlob) | Read-only Azure Blob template reader using an application-owned client |

MailStencil `0.1.0-preview.1` is available on NuGet.org. It is a preview release, and APIs may evolve
before 1.0.
