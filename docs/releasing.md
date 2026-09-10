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

1. choose and commit the project license, then set matching NuGet license metadata;
2. create the public repository and set its real project/repository URLs;
3. update `SECURITY.md` with the verified private reporting route;
4. recheck package-ID ownership immediately before the first push;
5. create a protected GitHub environment for release approval;
6. configure NuGet.org trusted publishing for the exact owner, repository, workflow, and environment;
7. grant `id-token: write` only to the publication job and exchange OIDC for a short-lived NuGet key
   immediately before `dotnet nuget push`;
8. publish immutable CI artifacts and never store a long-lived NuGet API key.

The normal CI and package jobs are intentionally nonpublishing and use read-only repository permissions.
