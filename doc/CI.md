# CI gates

What every change must pass before it reaches `main`, and how `main` is protected - the same
rules as EWP's repository.

In short: **nothing reaches `main` without a pull request whose "CI result" check is green - the
solution and the check tools build, the unit tests pass, and no deployed NuGet package has a known
HIGH or CRITICAL vulnerability.** Images are then built from `main` only
([Containers.md](Containers.md)).

## The gates

[ci.yml](../.github/workflows/ci.yml) runs on every pull request and every push to `main` (no
path filter: a required check must always report).

| Job | What it checks | Fails when |
|---|---|---|
| .NET | Restore -> vulnerable packages (`dotnet list package --vulnerable --include-transitive`; the deployables' packages, not the test project's) -> Release build of `PartnerBank.slnx` -> the two check tools build (`tools/HandshakeCheck`, `tools/JourneyCheck` - single-file apps outside the solution) -> unit tests (`tst/PartnerBank.Api.Tests`) | A HIGH or CRITICAL advisory; a build error; a failing test. Lower advisories are listed on the run's summary |
| **CI result** | Sums up the job above | It did not succeed. **This is the one check branch protection requires** |

Not gated here, and why:

- **HandshakeCheck and JourneyCheck runs**: they act as EWP against a running Partner Bank (EWP's
  client certificate and signing key); CI only proves they compile.
- **Container images**: [container-images.yml](../.github/workflows/container-images.yml) builds,
  scans and start-checks the images a pull request changes, but it has a path filter, so it
  cannot be a required check. Its gates still stop a bad image from being published on `main`.

## Branch protection: ruleset for `main`

Set once in GitHub: **Settings -> Rules -> Rulesets -> New ruleset -> New branch ruleset**.

| Setting | Value |
|---|---|
| Ruleset name | `main` |
| Enforcement status | Active |
| Bypass list | Empty (the rules apply to the owner too) |
| Target branches | Add target -> Include default branch |
| Restrict deletions | On |
| Block force pushes | On |
| Require a pull request before merging | On; required approvals **0** (one developer); allowed merge methods: **Squash** |
| Require status checks to pass | On; add **`CI result`** (source: GitHub Actions); "Require branches to be up to date" off |

`CI result` appears in the list only after the workflow has run once - add the ruleset after the
first green run of `ci.yml`.

Also once: **Settings -> Advanced Security** (free for a public repository) -> **Secret
Protection** on, and **Push protection** on.

## Working with it

From the laptop or the PC:

```text
git switch main && git pull
git switch -c <step-name>
... commit as usual ...
git push -u origin <step-name>
```

On GitHub: **Compare & pull request**. The PR description is the commit description; with
squash merge it becomes the one commit on `main`. When "CI result" is green: **Squash and merge**,
then delete the branch. Then locally: `git switch main && git pull`.

Dependabot's pull requests (actions, base images) go through the same gates.