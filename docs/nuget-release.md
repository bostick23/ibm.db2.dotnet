# NuGet releases with Trusted Publishing

Publish from `main` by publishing a GitHub Release. The workflow
`.github/workflows/publish-nuget.yml` uses NuGet Trusted Publishing: GitHub OIDC
is exchanged for a short-lived API key immediately before upload. No permanent
NuGet API key or IBM i credentials are needed in Actions.

## One-time configuration

### GitHub

In `bostick23/ibm.db2.dotnet`, open **Settings → Environments**:

1. Create an environment named **`nuget`**.
2. Under **Deployment branches and tags**, choose **Selected branches and
   tags** and add a **Tag** rule matching **`v*`**. Do not add a branch rule.
3. Add the environment **variable** `NUGET_USER` with the NuGet profile name
   of the user owning the trusted publishing policy. Use the profile name,
   not an email address or an API key.
4. Leave required reviewers and wait timers disabled. Publishing the GitHub
   Release is the explicit release decision.

Environment rules restrict tag-based deployments. The workflow also verifies
that the tagged commit belongs to the history of `main`.
[GitHub environment documentation](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments).

### NuGet.org

Sign in with the package-owner account, open your profile menu →
**Trusted Publishing**, and add a policy:

| Setting | Value |
|---|---|
| Repository Owner | `bostick23` |
| Repository | `ibm.db2.dotnet` |
| Workflow File | `publish-nuget.yml` |
| Environment | `nuget` |
| Package pattern | `Db2i.AdoNet` |
| Scope | Publish new versions of the existing package |
| Policy owner | The account or organization owning `Db2i.AdoNet` |

Enter the workflow **filename only**, without `.github/workflows/`. `NUGET_USER`
must identify the NuGet user associated with this policy. Ensure the policy is
active before the first release. The official login action requests an OIDC
token and exchanges it for a temporary key valid for one hour.
[NuGet Trusted Publishing documentation](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Prepare a version

1. Update `<Version>` and `<PackageReleaseNotes>` in `src/Db2i/Db2i.csproj`.
   The first release using this workflow is **`0.5.0-alpha.4`**.
2. Move the relevant changelog entries into the new version and update README
   and work status. Record any IBM i verification still pending.
3. Run the Release suite, commit the changes on `main`, and push them.
4. Wait for the normal **CI** workflow to succeed on that commit.

Use a tag of exactly `v` followed by the package version. For example,
`v0.5.0-alpha.4` corresponds to `0.5.0-alpha.4`. Versions containing a
prerelease suffix require the GitHub Release to be marked **Pre-release**;
stable versions require that flag to be off. Build metadata (`+...`) is not
supported in release versions.

A permanent release branch is unnecessary while maintaining one version line.
The workflow accepts tagged commits already merged into `main`, including
older commits in its history.

## Start publication

Open **Releases → Draft a new release** on GitHub:

1. Create `v0.5.0-alpha.4` on the verified `main` commit, or select an existing
   tag pointing to that commit.
2. Enter the title and release notes, including real-host validation limits.
3. Select **Pre-release** for this alpha version.
4. Click **Publish release**.

Saving a draft or pushing a tag alone does not publish to NuGet. The
`release: published` event handles stable releases and prereleases, including
prereleases published from a draft.
[GitHub release events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#release).

The **Publish NuGet** workflow runs the release gates and tests, builds both
library targets, creates and checks `.nupkg`/`.snupkg`, then publishes the same
verified artifacts. Only the publishing job can request OIDC credentials.
Packages and SHA-256 checksums are attached to the GitHub Release.

Real IBM i tests are opt-in and run separately in the local environment. Actions
does not load `.env.local`; passing CI does not establish real-host validation.

## Retry and verify

If a run fails, fix external configuration when applicable and use
**Actions → Publish NuGet → failed run → Re-run failed jobs**. Use **Re-run all
jobs** if the build artifacts have expired. Concurrent runs for the same tag
are serialized without canceling an active publication.

The package and symbol pushes run separately with `--skip-duplicate`, allowing
retry after the package succeeded but symbols or GitHub attachments failed.
Every retry still validates the package metadata and checksums. Releases and
NuGet versions are immutable inputs: never move a published tag or reuse a
version for changed code. A code/workflow fix requires a new version and tag.

After a successful run, check:

- Both jobs in **Publish NuGet** are green.
- The GitHub Release contains the package, symbols, and `SHA256SUMS`.
- [Db2i.AdoNet 0.5.0-alpha.4](https://www.nuget.org/packages/Db2i.AdoNet/0.5.0-alpha.4)
  is visible on NuGet.org; indexing and symbol validation can take a few minutes.
- A consumer can restore `Db2i.AdoNet` with the explicit released version.

OIDC authentication and both package uploads were verified end to end by the
first `0.5.0-alpha.4` release on October 8, 2026
([successful workflow run](https://github.com/bostick23/ibm.db2.dotnet/actions/runs/37794211013)).
Do not set `NUGET_API_KEY` as a
repository secret: the login action provides the temporary key for each run.
