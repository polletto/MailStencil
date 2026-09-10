# Changelog

All notable changes are recorded here. MailStencil follows Semantic Versioning.

## [Unreleased]

## [0.1.0-preview.1] - Unreleased

### Added

- Storage-agnostic runtime contracts, dependency injection, culture fallback, and positive source caching.
- Bounded Scriban rendering and structured static validation against the declared model type.
- Restrictive read-only FileSystem and Azure Blob template providers.
- Console and ASP.NET Core examples, deterministic provider tests, and opt-in Azurite coverage.
- NuGet metadata, symbols, validation, package-consumer smoke tests, and cross-platform CI.

### Security

- Detached model projection prevents CLR method exposure and mutation of the original model.
- Template, projection, output, filesystem, blob, and JSON limits bound in-process work.
- MailStencil-generated warning logs are fixed and sanitized.
