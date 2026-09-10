# Release process

MailStencil uses Semantic Versioning. Before 1.0, minor versions may contain documented breaking API or
behavior changes; patch versions contain compatible fixes. Prerelease labels identify packages that are
not yet covered by a stable compatibility promise. After 1.0, breaking changes require a new major version.

The default repository version is centralized in `Directory.Build.props` as `VersionPrefix` and
`VersionSuffix`. CI and release candidates may override it once:

```sh
dotnet pack -c Release --no-build --no-restore \
  -p:Version=0.1.0-preview.1 -o artifacts/packages
```

A release candidate must pass restore, Release build/test, dependency audit, package inspection, both
local-package consumer smoke tests, samples, and cross-platform CI. The four runtime packages are packed
together at one version. Test, sample, and tooling projects remain non-packable. Create a tag only after
package contents and changelog are final; the intended first tag is `v0.1.0-preview.1`.

No publishing workflow is included in this milestone. Before publishing, the owner must:

1. confirm the repository's private vulnerability-reporting route and update `SECURITY.md` if needed;
2. recheck package-ID ownership immediately before the first push;
3. create a protected GitHub environment for release approval;
4. configure NuGet.org trusted publishing for the exact owner, repository, workflow, and environment;
5. grant `id-token: write` only to the publication job and exchange OIDC for a short-lived NuGet key
   immediately before `dotnet nuget push`;
6. publish immutable CI artifacts and never store a long-lived NuGet API key.

The repository URL is `https://github.com/polletto/MailStencil`, and packages use the MIT license expression
corresponding to the root `LICENSE` file.

The normal CI and package jobs are intentionally nonpublishing and use read-only repository permissions.
