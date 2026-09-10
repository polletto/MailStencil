---
title: Quick Start
description: Render a strongly typed Scriban email template with MailStencil.
---

# Quick Start

Register the runtime, Scriban renderer, and filesystem reader:

```csharp
using MailStencil;
using Microsoft.Extensions.DependencyInjection;

using var provider = new ServiceCollection()
    .AddMailStencil()
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

public sealed class OrderConfirmationModel
{
    public required string CustomerName { get; init; }
    public required string OrderNumber { get; init; }
    public decimal Total { get; init; }
}
```

MailStencil maps PascalCase model properties to Scriban snake_case names. A template can use:

```scriban
Order {{ order_number }} totals {{ total }}
<p>Hello {{ customer_name | html.escape }}</p>
```

MailStencil does not automatically HTML-escape values. Use `html.escape` for untrusted values inserted
into an HTML body. See [validation and supported template syntax](../../../docs/architecture.md#ast-validation-and-supported-language)
and the [filesystem layout and security rules](../../../docs/architecture.md#filesystem-provider).
