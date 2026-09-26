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
that sign-in. Nothing is stored, and nothing expires. Microsoft's guide:
<https://code.visualstudio.com/api/working-with-extensions/publishing-extension#secure-automated-publishing-to-visual-studio-marketplace>.
Once:

1. **Create the identity**: a user-assigned managed identity, the kind
   Microsoft's guide uses (Azure portal → Managed Identities → Create; for
   example resource group `offramp-release`, name `offramp-marketplace`). It
   needs an Azure subscription to live in but costs nothing, and it needs no
   Azure role: its only permission is its Marketplace membership. Note its
   **Client ID** and the **Tenant ID** (Overview). An app registration also
   signs in, but a service principal has been reported to pass `verify-pat`
   and then fail to publish ("You need to be logged in with your corporate
   credentials", microsoft/vscode-vsce#1023).
2. **Trust the release job**: on the identity, Settings → Federated
   credentials → Add credential → scenario "GitHub Actions deploying Azure
   resources": organization `Andorbal`, repository `offramp`, entity
   **Environment**, environment `vscode-marketplace`, any name. That is the
   subject `repo:Andorbal/offramp:environment:vscode-marketplace` (the match
   is case-sensitive), issuer `https://token.actions.githubusercontent.com`,
   audience `api://AzureADTokenExchange`.
3. **Tell GitHub**: Settings → Environments → New environment
   `vscode-marketplace`. Add required reviewers if releases should wait for a
   person. If you restrict deployment branches and tags, allow the tag pattern
   `v*` (releases) and `main` (the helper below). In the environment, add the
   variables (not secrets; they are identifiers) `AZURE_CLIENT_ID` and
   `AZURE_TENANT_ID`. Leave `AZURE_SUBSCRIPTION_ID` unset: the identity has no
   Azure role, so there is no subscription to select.
4. **Get its Marketplace ID**: Actions → **Marketplace identity** → Run
   workflow (on `main`). It signs in as the release does and prints the
   identity's Azure DevOps profile ID (the `id` of
   `az rest -u https://app.vssps.visualstudio.com/_apis/profile/profiles/me
   --resource 499b84ac-1321-427f-aa17-267ca6975798`) in the run summary. This
   first run ends red at the publish-rights check, since the identity is not a
   member yet.
5. **Make it a publisher member**: <https://marketplace.visualstudio.com/manage/publishers/AndrewBenz>
   → Members → add that ID with the **Contributor** role. Run **Marketplace
   identity** again: it now passes.
6. **Remove any `VSCE_PAT` secret** left from before.

Without `AZURE_CLIENT_ID` the release job warns and skips the Marketplace.
`verify-pat` (the helper's check) proves the sign-in and the membership, not
the publish itself, so still watch the first stable release's
`publish-vscode` job. If the sign-in fails with "No subscriptions found" even
with `allow-no-subscriptions`, give the identity the Reader role on its
resource group and set `AZURE_SUBSCRIPTION_ID` too.

## Pre-releases

Tag `vX.Y.Z-rc.1` to publish a prerelease; the workflow marks the GitHub
Release as prerelease and NuGet lists it as such. CHANGELOG gets a section for
it like any release.

## Hotfixes

Branch from the tag, fix, PR to `main` as usual, then tag `vX.Y.(Z+1)` on
`main` if `main` is releasable, or on the hotfix branch otherwise (MinVer
handles either).
