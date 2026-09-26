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
   extension's `.vsix` attached. For a stable tag, the `publish-vscode` job
   then publishes the extension (`AndrewBenz.offramp`) to the Visual Studio
   Marketplace, signed in with Microsoft Entra ID (below), and, when `OVSX_PAT`
   is set, to Open VSX. Prerelease tags publish neither: the extension's version
   is the tag's `X.Y.Z`, which the stable release will need.
6. Verify: `dotnet tool update -g offramp` shows the new version;
   `offramp --version` matches.

## Secrets and permissions

- `NUGET_API_KEY`: scoped to push `offramp` and `Offramp.*` packages. Rotate
  yearly.
- `NUGET_API_KEY` belongs to the `nuget` environment the publish job runs in
  (or to the repository).
- `OVSX_PAT` (optional): an Open VSX access token for the `AndrewBenz`
  namespace (create the namespace once with `npx ovsx create-namespace
  AndrewBenz`), in the `vscode-marketplace` environment or the repository.
- The release workflow needs `contents: write` to create the release, and the
  `publish-vscode` job `id-token: write` to sign in to Microsoft Entra ID; both
  are granted in the workflow file, nothing else.

### The Visual Studio Marketplace: Microsoft Entra ID, no token

Azure DevOps personal access tokens no longer publish to the Marketplace
(global PATs are retired on 2026-12-01). The release signs in with GitHub's
OIDC token, exchanged for a Microsoft Entra ID token for an identity that is a
member of the `AndrewBenz` publisher; `vsce publish --azure-credential` uses
that sign-in. Nothing is stored, and nothing expires. Once:

1. **Create the identity** in the Microsoft Entra tenant you sign in to the
   Marketplace with: an app registration (Entra admin center → App
   registrations → New registration, for example `offramp-marketplace`; no
   Azure subscription needed), or a user-assigned managed identity (needs an
   Azure subscription; Microsoft's guide uses this one).
2. **Trust the release job**: on the identity, add a federated credential
   (App registration → Certificates & secrets → Federated credentials → Add
   credential → "GitHub Actions deploying Azure resources"): organization
   `Andorbal`, repository `offramp`, entity type **Environment**, environment
   `vscode-marketplace`. That is the subject
   `repo:Andorbal/offramp:environment:vscode-marketplace`, issuer
   `https://token.actions.githubusercontent.com`, audience
   `api://AzureADTokenExchange`.
3. **Make it a publisher member**: <https://marketplace.visualstudio.com/manage/publishers/AndrewBenz>
   → Members → add the identity with the **Contributor** role. Use the
   identifier Microsoft's guide says the Members page takes for it (for a
   managed identity, its resource ID):
   <https://code.visualstudio.com/api/working-with-extensions/publishing-extension>.
4. **Tell GitHub**: create the environment `vscode-marketplace` (Settings →
   Environments; add required reviewers if releases should wait for a
   person), and add the variables (not secrets; they are identifiers)
   `AZURE_CLIENT_ID` (the identity's application/client ID) and
   `AZURE_TENANT_ID`, plus `AZURE_SUBSCRIPTION_ID` for a managed identity.
5. **Remove any `VSCE_PAT` secret** left from before.

Without `AZURE_CLIENT_ID` the job warns and skips the Marketplace. A dry run
cannot exercise this sign-in: the first stable release after the setup is the
test, so watch its `publish-vscode` job.

## Pre-releases

Tag `vX.Y.Z-rc.1` to publish a prerelease; the workflow marks the GitHub
Release as prerelease and NuGet lists it as such. CHANGELOG gets a section for
it like any release.

## Hotfixes

Branch from the tag, fix, PR to `main` as usual, then tag `vX.Y.(Z+1)` on
`main` if `main` is releasable, or on the hotfix branch otherwise (MinVer
handles either).
