# MailStencil.FileSystem

An exact, read-only UTF-8 template reader. References MailStencil.Core only.

```csharp
services.AddMailStencil().AddFileSystemTemplateReader(options =>
{
    options.BasePath = "./templates";
    // options.MaxTemplateFileSize = 128 * 1024; // default bytes per part, including BOM
});
```

Maps requests to `<base>/<name>/<culture-or-default>/subject.txt`, `body.html`, `body.txt`.
Subject and at least one body must exist; empty files are allowed. No localization fallback or
explicit versions are supported. Missing exact directories return null; malformed/unsafe/unstable
content throws FileSystemTemplateException. Operational permission/I/O failures propagate.

The singleton has no mutable per-request shared state and performs async reads with bounded retries.
Registration rejects existing readers. Symlinks/reparse points are rejected, including base ancestors.
Use an application-controlled directory tree: portable metadata checks are not protection against
every hostile filesystem race or a transactional multipart snapshot.

The provider accepts only restrictive logical name/culture segments and rejects links/reparse points.
The configured base tree must still be controlled by the application: portable path checks are not an
OS-handle-level sandbox and cannot guarantee a transactional snapshot against hostile writers.
