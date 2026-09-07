# Releasing

How a version of `LadybugDb.Client` gets from a commit on `main` to a package on nuget.org. (The
engine binaries are upstream's `LadybugDB.Native` packages; this repository publishes none.)

- [How a release ships](#how-a-release-ships)
- [What the workflow actually does](#what-the-workflow-actually-does)
- [Versioning](#versioning)
- [One-time setup the repo owner must do](#one-time-setup-the-repo-owner-must-do)
- [Verifying a publish succeeded](#verifying-a-publish-succeeded)
- [What is unverified](#what-is-unverified)

## How a release ships

1. Make sure `main` is in the state you want to ship.
2. Decide the version, e.g. `0.2.0` or `0.2.0-beta.1` (see [Versioning](#versioning)).
3. Tag it and push the tag:

   ```console
   git tag v0.2.0
   git push origin v0.2.0
   ```

   Pushing a tag matching `v[0-9]+.[0-9]+.[0-9]+*` triggers
   [`.github/workflows/release.yml`](../.github/workflows/release.yml), which builds, tests, packs,
   and publishes the package.

   Alternatively, run the workflow manually from the Actions tab (`workflow_dispatch`) and supply a
   `version` input — useful for re-publishing after a transient failure without cutting a new tag,
   since `--skip-duplicate` makes re-running safe even if the previous attempt partially succeeded.

## What the workflow actually does

In order, on `ubuntu-latest`:

1. Determine the version from the tag (or the `workflow_dispatch` input) and validate it looks like
   SemVer.
2. `dotnet restore` (which also brings in the `LadybugDB.Native` package the test projects
   reference), `dotnet build -c Release -p:Version=<version>`.
3. `dotnet test` for both `LadybugDb.Client.Tests` and `LadybugDb.Client.IntegrationTests`, against
   the just-built `Release` binaries. **A publish never happens from artifacts that weren't
   tested** — if either test project fails, the job stops before packing or pushing anything.
4. `dotnet pack -c Release -p:Version=<version>` — produces exactly one package
   (`LadybugDb.Client`; every other project is `IsPackable=false`).
5. `NuGet/login@v1` exchanges this job's GitHub OIDC token for a nuget.org API key good for one
   hour. This step runs right before the push steps, not earlier in the job, since the key is
   short-lived.
6. `dotnet nuget push`, with `--skip-duplicate` so re-running the workflow (e.g.
   after a flaky push) isn't fatal if a package version already exists on nuget.org.

No long-lived nuget.org API key is stored anywhere in this repo or its secrets — this is
[NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), backed
by GitHub OIDC.

## Package validation and the API baseline

`LadybugDb.Client.csproj` has `EnablePackageValidation=true`, so every `dotnet pack` checks the
package against itself (compatible frameworks and runtimes). The check that matters for consumers,
"did this version break the previous one's public API", needs a published version to compare
against, and starts on the release after the first publish:

1. After the first version (say `0.2.0`) is live on nuget.org, add to
   `LadybugDb.Client/LadybugDb.Client.csproj` (and, once it ships, to
   `LadybugDb.Client.Extensions/LadybugDb.Client.Extensions.csproj`):

   ```xml
   <PackageValidationBaselineVersion>0.2.0</PackageValidationBaselineVersion>
   ```

2. From then on `dotnet pack` downloads that version and fails on a removed or changed public
   member. Bump the property to the newest published version with each release.
3. A deliberate break (pre-1.0 this is allowed; after 1.0 it means a major bump) is recorded by
   adding the reported suppression to `CompatibilitySuppressions.xml` next to the csproj, and by
   a `Changed`/`Removed` entry in `CHANGELOG.md`.

## Versioning

The pushed **tag is the single source of truth** for the published version. The workflow strips
the leading `v` from the tag name (`v0.2.0` → `0.2.0`) and passes it as `-p:Version=0.2.0` to every
`dotnet build`/`test`/`pack` invocation, which overrides whatever `VersionPrefix`/`VersionSuffix`
[`Directory.Build.props`](../Directory.Build.props) has at that commit (currently
`VersionPrefix=0.1.0`, `VersionSuffix=alpha`, i.e. the placeholder pre-release version used for
local/CI builds that never publish). This means:

- `Directory.Build.props`'s version never needs to be bumped by hand as part of a release — it's
  only a reasonable default for `dotnet build`/`dotnet pack` when nobody passed `-p:Version`.
- The tag format must be a valid SemVer version (`X.Y.Z` or `X.Y.Z-prerelease`), or the workflow
  fails fast in its "Determine package version" step before touching the build.
- Tag `v0.2.0-alpha.1` to ship a pre-release to nuget.org; tag `v0.2.0` for a stable release. NuGet
  treats these as ordinary SemVer 2.0 pre-release/stable semantics — nothing release-specific to
  configure for that.

## One-time setup the repo owner must do

There is exactly one step, and it requires the owner's nuget.org account — it cannot be done from
this repository.

1. **Create the nuget.org Trusted Publishing policy.** On nuget.org, sign in as the account that
   should own these packages, go to your profile → **Trusted Publishing**, and add a policy with
   exactly:
   - **Repository Owner:** `HarryCordewener`
   - **Repository:** `ladybugdb-dotnet-client`
   - **Workflow File:** `release.yml` (the file name only, not the `.github/workflows/` path)
   - **Environment:** **leave empty.** This workflow does not use a GitHub Environment. A policy
     scoped to an environment will not match a token minted outside one, and the exchange fails
     with no useful error — so this field must stay blank unless you later add an `environment:`
     key to `release.yml`, in which case both must be changed together.

   If nuget.org requires the target package IDs to already exist or be reserved before a Trusted
   Publishing policy can be scoped to them, reserve `LadybugDb.Client` first. If this is a private/new nuget.org policy, it starts temporarily active for **7 days**
   and locks to this repo's owner/repository IDs on the first successful publish — expect that
   window, and don't be alarmed if the policy shows as "pending" until the first tag ships.

That is the whole setup. There is no variable or secret to configure: the `NuGet/login@v1` step's
`user` input is hardcoded to `harrycordewener` in `release.yml`. A nuget.org profile name is public
(it appears in the profile URL), so it is not a secret, and inlining it removes both a setup step
and a silent failure mode — an unset repository variable resolves to an empty string, which fails
the token exchange with no useful error.

**If the packages ever change nuget.org owner**, edit that `user:` value in `release.yml` to match
the new profile, and create a Trusted Publishing policy under that account.

> **If you later want a manual approval gate** between "tag pushed" and "packages published",
> create a GitHub Environment (Settings → **Environments**) with required reviewers, add
> `environment: <name>` to the `publish` job in `release.yml`, **and** set the same name in the
> nuget.org policy's Environment field. All three must agree; changing one alone breaks publishing.

Until all three of these are done, the workflow will run but fail at either the `NuGet/login@v1`
step (empty/wrong `user`, or no matching Trusted Publishing policy) or the `dotnet nuget push` step
(no valid policy to authorize the push).

## Verifying a publish succeeded

- **In the workflow run:** the `Push LadybugDb.Client` step should complete without error. A `--skip-duplicate` push of a version that's already live prints
  a message and still exits 0 — that's expected on a re-run, not a sign anything is wrong.
- **On nuget.org:** check
  [nuget.org/packages/LadybugDb.Client](https://www.nuget.org/packages/LadybugDb.Client)
  for the new version. New versions can take a few minutes to appear while nuget.org finishes
  indexing.
- **From a consuming project:**

  ```console
  dotnet add package LadybugDb.Client --version 0.2.0
  dotnet add package LadybugDB.Native --version 0.19.1
  ```

## What is unverified

This workflow has not been exercised end-to-end against real nuget.org infrastructure — there is
no test-publish mode for Trusted Publishing, and doing so would require the one-time setup above to
already be in place with a real account. Specifically unverified:

- That `NuGet/login@v1` successfully exchanges this repo's GitHub OIDC token for a nuget.org API
  key once the Trusted Publishing policy above exists.
- That the resulting API key has push rights sufficient for `dotnet nuget push`.
- The exact behavior/wording nuget.org returns on a `--skip-duplicate` push against an existing
  version.
- End-to-end timing: whether the 1-hour API key window is comfortably enough for build+test+pack to
  finish before push (it should be — CI's own build+test+pack normally completes in a couple of
  minutes — but this has not been timed for this specific workflow).

The workflow YAML has been validated with `actionlint` and a YAML parser, and its non-publishing
steps (restore, build, test, pack) are the same commands CI already runs successfully on
every PR — only the final OIDC login and `dotnet nuget push` steps are new and untested against the
real service.
