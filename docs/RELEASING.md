# Releasing

Offramp ships as the `offramp` .NET global tool on nuget.org, the
`Offramp.Analyzers` package, and the VS Code extension (`editors/vscode`, a
`.vsix` attached to each GitHub Release). Versions come from git tags via
MinVer; the extension takes the tag's `X.Y.Z` when it is packaged.

## Versioning

- Semantic Versioning. Pre-1.0: `0.MINOR.PATCH`; a `MINOR` bump may change
  command output shapes and is called out in CHANGELOG under **Changed** with
  a migration note. From 1.0, output schemas are stable within a major.
- Untagged builds get `-alpha.0.<height>` suffixes automatically.
- The tag is the release: `vX.Y.Z`. No version numbers are stored in files.

## Steps

1. Ensure `main` is green.
2. Edit `CHANGELOG.md`: rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD`,
   add a fresh empty `## [Unreleased]` above it, and update the link
   references at the bottom.
3. Open a PR titled `release: vX.Y.Z` with only that change; merge it.
4. Tag the merge commit and push the tag:
   ```bash
   git tag -a vX.Y.Z -m "Offramp vX.Y.Z"
   git push origin vX.Y.Z
   ```
5. `release.yml` runs: builds, tests on all three OSes, packs, pushes to
   nuget.org with the `NUGET_API_KEY` secret, and creates a GitHub Release
   whose notes are the matching CHANGELOG section (extracted by
   `eng/changelog-section.sh`), with the `.nupkg` files and the VS Code
   extension's `.vsix` attached. For a stable tag it also publishes the
   extension (`AndrewBenz.offramp`) to the Visual Studio Marketplace with the
   `VSCE_PAT` secret and, when `OVSX_PAT` is set, to Open VSX. Prerelease tags
   publish neither: the extension's version is the tag's `X.Y.Z`, which the
   stable release will need.
6. Verify: `dotnet tool update -g offramp` shows the new version;
   `offramp --version` matches.

## Secrets and permissions

- `NUGET_API_KEY`: scoped to push `offramp` and `Offramp.*` packages. Rotate
  yearly.
- `VSCE_PAT`: an Azure DevOps personal access token for the `AndrewBenz`
  Marketplace publisher, scope **Marketplace › Manage**, organization "All
  accessible organizations". Without it the release warns and skips the
  Marketplace. Rotate before it expires (at most a year).
- `OVSX_PAT` (optional): an Open VSX access token for the `AndrewBenz`
  namespace (create the namespace once with `npx ovsx create-namespace
  AndrewBenz`).
- These secrets belong to the `nuget` environment the publish job runs in (or
  to the repository).
- The release workflow needs `contents: write` to create the release, granted
  in the workflow file, nothing else.

## Pre-releases

Tag `vX.Y.Z-rc.1` to publish a prerelease; the workflow marks the GitHub
Release as prerelease and NuGet lists it as such. CHANGELOG gets a section for
it like any release.

## Hotfixes

Branch from the tag, fix, PR to `main` as usual, then tag `vX.Y.(Z+1)` on
`main` if `main` is releasable, or on the hotfix branch otherwise (MinVer
handles either).
