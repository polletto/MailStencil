---
title: Why MailStencil?
description: Decide whether MailStencil, Razor or Blazor, or raw Scriban fits your email-template workflow.
---

# Why MailStencil?

MailStencil is useful when email templates are content that should live outside application code while
the application retains a known model contract. It composes retrieval, structural validation, culture
fallback, positive source caching, and Scriban rendering behind small interfaces.

MailStencil does not provide compile-time template type safety. Its validator checks Scriban syntax and
member access against the declared `TModel` contract. The runtime performs that validation before
rendering, and applications can also validate unsaved `EmailTemplateContent` through
`ITemplateValidator`.

## Use MailStencil when

- Templates should live outside application code and may need to change independently from application
  builds and deployments.
- Templates come from the read-only FileSystem or Azure Blob providers, or from another
  `ITemplateReader` implementation.
- Checking template structure and member access against a known `TModel` is useful.
- Culture fallback and positive source caching should be handled consistently by the runtime service.
- The application should remain independent from its SMTP or email-delivery provider.

## When Razor or Blazor may fit better

Razor or Blazor-based rendering may be a better fit when templates are application code, compile-time
checking is preferred, and IDE navigation and refactoring support matter more than independent template
deployment. This approach also fits when template changes can follow the application's normal build and
deployment lifecycle.

Rendering Razor or Blazor components as email content still requires application-specific decisions about
storage, caching, localization, execution, and integration with an email sender.

## When raw Scriban may be enough

Use Scriban directly when only template parsing and rendering are needed. It is a smaller choice when the
application does not need MailStencil's storage abstraction, declared-model validation, culture fallback,
or source cache, or when the application already implements those concerns.

MailStencil uses Scriban as its rendering engine. Its value is the surrounding contract and runtime
pipeline rather than a replacement template language.

## Comparison

| Capability | MailStencil | Razor / Blazor | Raw Scriban |
| --- | --- | --- | --- |
| Templates outside application deployment | Yes | Usually compiled with the application | Yes, if loaded externally |
| Compile-time template checking against the model | No | Yes, for compiled views or components | No |
| Structural validation against a declared `TModel` | Yes | Not equivalent — compiled views/components are checked by the compiler | No built-in application-model contract validation |
| FileSystem / Azure Blob abstraction | Included through `ITemplateReader` providers | Application-specific | Application-specific |
| Culture fallback | Included in `IEmailTemplateService` | Application-specific | Application-specific |
| Source-template caching | Positive in-memory source cache | Different compiled/rendering model or application-specific | Application-specific |
| Email sending | No | No | No |

## Where MailStencil stops

MailStencil returns a rendered subject and optional HTML and text bodies. SMTP, MailKit, SendGrid, Azure
Communication Services, retry policies, queueing, delivery tracking, bounce handling, and email-header
safety belong to the host application and its delivery integration.

Continue with [Installation](getting-started/installation.md), or read the
[architecture and limits](../../docs/architecture.md) for exact behavior and security boundaries.
