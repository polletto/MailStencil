---
title: Installation
description: Install and compose the MailStencil runtime packages.
---

# Installation

MailStencil targets .NET 10. Install Core, the Scriban renderer, and one storage provider.

For filesystem templates:

```shell
dotnet add package MailStencil.Core --version 0.1.0-preview.1
dotnet add package MailStencil.Scriban --version 0.1.0-preview.1
dotnet add package MailStencil.FileSystem --version 0.1.0-preview.1
```

For Azure Blob Storage:

```shell
dotnet add package MailStencil.Core --version 0.1.0-preview.1
dotnet add package MailStencil.Scriban --version 0.1.0-preview.1
dotnet add package MailStencil.AzureBlob --version 0.1.0-preview.1
```

Continue with the [Quick Start](quick-start.md), or see the full
[ASP.NET Core integration guide](../../../docs/aspnet-core.md).
