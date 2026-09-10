# ASP.NET Core sample

A .NET 10 minimal API that previews fixed demonstration order data; it does not send email.

```sh
dotnet run --project samples/MailStencil.Sample.AspNetCore -c Release -- --urls http://127.0.0.1:5080
```

Open http://127.0.0.1:5080/preview/order-confirmation. The response is JSON with subject, htmlBody and
textBody. Add ?culture=it-IT for Italian, or ?culture=fr-CA to demonstrate default-template fallback
with French-Canadian numeric formatting. Omit culture to use configured DefaultCulture; an empty
culture explicitly selects invariant/default behavior.

FileSystem works without cloud credentials. Templates are copied into build/publish output.
BasePath resolves against the application's content root. See [ASP.NET usage](../../docs/aspnet-core.md)
for configuration, Azure alternative, logging, sanitized errors and limitations.
