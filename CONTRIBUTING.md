# Contributing

The .NET SDK version is pinned in `global.json`.

```sh
dotnet restore --locked-mode
dotnet build -c Release
dotnet test -c Release --no-build
dotnet pack -c Release --no-build -o artifacts
```

Builds treat warnings as errors. Package versions are central in `Directory.Packages.props`, and every project
commits its `packages.lock.json`. After changing a package reference or version, run `dotnet restore` without
`--locked-mode` and commit the updated lock files; CI restores in locked mode and fails on stale lock files.

The Aspire host projects (`examples/Aspire`, `tests/Corfedo.Troupe.Aspire.Hosting.Tests`) restore DCP and the
dashboard for the build machine's runtime identifier, so they keep one lock file per platform:
`packages.<rid>.lock.json` for `linux-x64` (CI) and `osx-arm64`. A package change updates only the file of the
platform it was restored on; run the restore on the other platform too, and commit both. The Aspire version is in
`Directory.Packages.props` and, for `Aspire.AppHost.Sdk`, in `global.json`.

The package version is `<Version>` in `Directory.Build.props`. Releases are built from `v<Version>` tags.

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) as described in the
[organization contributing guide](https://github.com/corfedo/.github/blob/main/CONTRIBUTING.md#commit-messages), for
example `feat: add a profile option`. Release notes are generated from them.

Contributions are licensed under Apache-2.0, the license of this repository.

AI-assisted contributions follow the
[organization policy](https://github.com/corfedo/.github/blob/main/CONTRIBUTING.md#ai-assisted-contributions).
