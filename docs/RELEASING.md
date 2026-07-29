# Releasing

How a version of `LadybugDb.Client` and `LadybugDb.Client.Native` gets from a commit on `main`
to two packages on nuget.org.

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
   and publishes both packages.

   Alternatively, run the workflow manually from the Actions tab (`workflow_dispatch`) and supply a
   `version` input — useful for re-publishing after a transient failure without cutting a new tag,
   since `--skip-duplicate` makes re-running safe even if the previous attempt partially succeeded.

## What the workflow actually does

In order, on `ubuntu-latest`:

1. Determine the version from the tag (or the `workflow_dispatch` input) and validate it looks like
   SemVer.
2. `bash scripts/fetch-liblbug.sh` — fetch and checksum-verify the pinned native binaries for all
   six RIDs, same as CI.
3. `dotnet restore`, `dotnet build -c Release -p:Version=<version>`.
4. `dotnet test` for both `LadybugDb.Client.Tests` and `LadybugDb.Client.IntegrationTests`, against
   the just-built `Release` binaries. **A publish never happens from artifacts that weren't
   tested** — if either test project fails, the job stops before packing or pushing anything.
5. `dotnet pack -c Release -p:Version=<version>` — produces exactly two packages
   (`LadybugDb.Client` and `LadybugDb.Client.Native`; the two test projects are `IsPackable=false`).
6. `NuGet/login@v1` exchanges this job's GitHub OIDC token for a nuget.org API key good for one
   hour. This step runs right before the push steps, not earlier in the job, since the key is
   short-lived.
7. `dotnet nuget push` for each package, with `--skip-duplicate` so re-running the workflow (e.g.
   after a flaky push) isn't fatal if a package version already exists on nuget.org.

No long-lived nuget.org API key is stored anywhere in this repo or its secrets — this is
[NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), backed
by GitHub OIDC.

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

None of this can be done by an agent or from this repository — it requires the owner's nuget.org
account and this repo's GitHub settings.

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
   Publishing policy can be scoped to them, reserve `LadybugDb.Client` and `LadybugDb.Client.Native`
   first. If this is a private/new nuget.org policy, it starts temporarily active for **7 days**
   and locks to this repo's owner/repository IDs on the first successful publish — expect that
   window, and don't be alarmed if the policy shows as "pending" until the first tag ships.

2. **Set the `NUGET_USER` repository variable.** The workflow's `NuGet/login@v1` step needs your
   nuget.org **profile name** (not email address) as the `user` input, and this repo doesn't know
   it — it's wired as `${{ vars.NUGET_USER }}` deliberately rather than guessed or hardcoded. In
   this repo's GitHub Settings → **Secrets and variables** → **Actions** → **Variables** tab, add:
   - Name: `NUGET_USER`
   - Value: your nuget.org profile name (as shown in the nuget.org account URL/profile page)

That is the whole setup — two steps, no third.

> **If you later want a manual approval gate** between "tag pushed" and "packages published",
> create a GitHub Environment (Settings → **Environments**) with required reviewers, add
> `environment: <name>` to the `publish` job in `release.yml`, **and** set the same name in the
> nuget.org policy's Environment field. All three must agree; changing one alone breaks publishing.

Until all three of these are done, the workflow will run but fail at either the `NuGet/login@v1`
step (empty/wrong `user`, or no matching Trusted Publishing policy) or the `dotnet nuget push` step
(no valid policy to authorize the push).

## Verifying a publish succeeded

- **In the workflow run:** both `Push LadybugDb.Client` and `Push LadybugDb.Client.Native` steps
  should complete without error. A `--skip-duplicate` push of a version that's already live prints
  a message and still exits 0 — that's expected on a re-run, not a sign anything is wrong.
- **On nuget.org:** check
  [nuget.org/packages/LadybugDb.Client](https://www.nuget.org/packages/LadybugDb.Client) and
  [nuget.org/packages/LadybugDb.Client.Native](https://www.nuget.org/packages/LadybugDb.Client.Native)
  for the new version. New versions can take a few minutes to appear while nuget.org finishes
  indexing.
- **From a consuming project:**

  ```console
  dotnet add package LadybugDb.Client --version 0.2.0
  dotnet add package LadybugDb.Client.Native --version 0.2.0
  ```

## What is unverified

This workflow has not been exercised end-to-end against real nuget.org infrastructure — there is
no test-publish mode for Trusted Publishing, and doing so would require the one-time setup above to
already be in place with a real account. Specifically unverified:

- That `NuGet/login@v1` successfully exchanges this repo's GitHub OIDC token for a nuget.org API
  key once the Trusted Publishing policy above exists.
- That the resulting API key has push rights sufficient for `dotnet nuget push` on both packages.
- The exact behavior/wording nuget.org returns on a `--skip-duplicate` push against an existing
  version.
- End-to-end timing: whether the 1-hour API key window is comfortably enough for build+test+pack to
  finish before push (it should be — CI's own build+test+pack normally completes in a couple of
  minutes — but this has not been timed for this specific workflow).

The workflow YAML has been validated with `actionlint` and a YAML parser, and its non-publishing
steps (fetch, restore, build, test, pack) are the same commands CI already runs successfully on
every PR — only the final OIDC login and `dotnet nuget push` steps are new and untested against the
real service.
