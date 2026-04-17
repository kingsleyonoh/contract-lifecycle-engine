# CI/CD Pipeline — Operator Runbook

> Audience: operators configuring and maintaining the GitHub Actions pipelines that build, test,
> and deploy the Contract Lifecycle Engine.
> Scope: workflow anatomy, required secrets, image tagging, rollback paths, troubleshooting.
> Shipped: Batch 026 (2026-04-17).

## 1. Overview

Two GitHub Actions workflows drive the CI/CD pipeline:

| Workflow | File | Fires on | Purpose |
|----------|------|----------|---------|
| `ci`     | `.github/workflows/ci.yml` | push / PR on `dev` + `main` | Build + test gate |
| `deploy` | `.github/workflows/deploy.yml` | push on `main`, manual dispatch | Test + build image + push to GHCR + SSH deploy |

Every push to either branch MUST go green on `ci` before it can merge. The branch protection
rules (configure in **Repo settings → Branches → main**) should require:
- `ci / test` check
- Up-to-date branches before merging
- Linear history (squash or rebase merges only)

On merge to `main`, `deploy` kicks in automatically and runs the full deploy chain. See
`docs/operations/hetzner-deployment.md` for the VPS side.

## 2. Required GitHub Secrets

Configure under **Repo settings → Secrets and variables → Actions**:

| Secret | Used by | Purpose | Rotation |
|--------|---------|---------|----------|
| `HETZNER_HOST` | deploy | Hostname or IP of the production VPS | on VPS migration |
| `HETZNER_USER` | deploy | SSH login user (recommended: `deploy`) | rarely |
| `HETZNER_SSH_KEY` | deploy | Private SSH key (ED25519 PEM) matching `/home/deploy/.ssh/authorized_keys` | when an operator leaves |
| `GITHUB_TOKEN` | deploy (auto) | Workflow-bound token used to push to GHCR | automatic per-run |

`GITHUB_TOKEN` is minted per workflow run — no manual configuration. The `deploy.yml`
`permissions:` block grants `packages: write` so the token can push the image to
`ghcr.io/kingsleyonoh/contract-lifecycle-engine`.

### Adding / rotating `HETZNER_SSH_KEY`

```sh
# Generate on your workstation (or an air-gapped machine):
ssh-keygen -t ed25519 -C "github-actions-deploy" -f ~/.ssh/contract_engine_deploy

# Public key → paste into /home/deploy/.ssh/authorized_keys on the VPS
cat ~/.ssh/contract_engine_deploy.pub

# Private key → copy the ENTIRE contents (including BEGIN/END markers) into the GitHub secret
cat ~/.ssh/contract_engine_deploy
```

The `appleboy/ssh-action` and `appleboy/scp-action` steps accept PEM-format private keys
directly. Do not quote or base64-encode the value.

### Verification

The `deploy` workflow has a `verify-secrets` job that fails fast with `::error::` annotations
if any secret is missing or empty. If a deploy turns red before a test job even runs, check the
`verify-secrets` job output first.

## 3. CI Workflow Anatomy (`ci.yml`)

### Triggers
- `push` to `dev` + `main`
- `pull_request` targeting `dev` + `main`
- `workflow_dispatch` (manual re-run)

### Concurrency
`concurrency.group: ci-${{ github.ref }}` with `cancel-in-progress: true` — a new push to the
same branch cancels the in-flight run. Saves Actions minutes on rapid-fire commits.

### Single job: `test`
1. Checkout.
2. Setup .NET 8.
3. NuGet cache keyed on `hashFiles('**/*.csproj', '**/packages.lock.json')`. On a cache miss the
   restore takes ~45s; on hit ~5s.
4. `dotnet restore ContractEngine.sln`.
5. `dotnet build ContractEngine.sln --no-restore -c Release`.
6. Non-E2E test assemblies run sequentially with TRX + Cobertura coverage:
   - `ContractEngine.Core.Tests` (~313 tests as of Batch 026)
   - `ContractEngine.Api.Tests` (~182 tests)
   - `ContractEngine.Integration.Tests` (~219 tests)
7. Upload `*.trx` + coverage artifacts with 14-day retention.

### Why E2E is NOT in CI
`tests/ContractEngine.E2E.Tests/` spawns Kestrel subprocesses bound to fixed ports 5050–5061.
GitHub Actions runners are shared within a single job, but parallel workflow runs on the same
repo can collide on port binds. We run E2E manually before promoting to `main`:
```sh
dotnet test tests/ContractEngine.E2E.Tests/
```
Document any E2E-only failures in the PR description before merging.

### Why Postgres 16 service?
The integration tests open a real connection via `DatabaseFixture`. Using SQLite or Testcontainers
would drift from the production schema — Postgres-specific features (JSONB, `NULLS NOT DISTINCT`,
`gen_random_uuid()`) only materialise on real Postgres. The Postgres 16 service container matches
the production image.

## 4. Deploy Workflow Anatomy (`deploy.yml`)

### Triggers
- `push` to `main` only
- `workflow_dispatch` for operator-initiated redeploys

### Concurrency
`concurrency.group: deploy-production` with `cancel-in-progress: false` — concurrent deploys to
the same production host are forbidden. If two deploys queue, the second waits.

### Jobs (sequential)
1. **`verify-secrets`** — fail fast if any Hetzner secret is missing.
2. **`test`** — re-run the full non-E2E suite as a safety net.
3. **`build-and-push`** —
   - `docker/setup-buildx-action@v3` for multi-platform + cache support
   - `docker/login-action@v3` to GHCR using `github.actor` + `GITHUB_TOKEN`
   - `docker/build-push-action@v5` with `cache-from: type=gha` + `cache-to: type=gha,mode=max`
     so repeat deploys reuse the `dotnet publish` layer. First build ~4 min; incremental ~60s.
   - Tags: `:latest` + `:sha-<short>` (first 7 chars of `github.sha`).
4. **`deploy-to-vps`** —
   - `appleboy/scp-action@v0.1.7` copies `docker-compose.prod.yml` + `scripts/deploy/*.sh` to
     `/opt/contract-engine` on the VPS.
   - `appleboy/ssh-action@v1.2.0` runs:
     1. `chmod +x scripts/deploy/*.sh`
     2. `bash scripts/deploy/remember-previous-tag.sh` — snapshot the running image digest
     3. `docker compose -f docker-compose.prod.yml pull app`
     4. `docker compose -f docker-compose.prod.yml up -d`
     5. `bash scripts/deploy/smoke-and-rollback.sh https://contracts.kingsleyonoh.com/health/ready`

### Post-deploy smoke test
`smoke-and-rollback.sh` polls `/health/ready` up to 30 times with 2s intervals (~60s total). HTTP
200 = deploy succeeded. Persistent non-200 triggers the auto-rollback flow (§5).

## 5. Rollback Paths

### 5.1 Auto-rollback (smoke test failure)
`smoke-and-rollback.sh` reads `.previous-tag` (written by `remember-previous-tag.sh` before the
pull) and re-tags that digest as `:latest`. Caddy keeps serving while Docker Compose restarts
the `app` container.

Failure mode: if the previous deploy was a cold start, `.previous-tag` contains the sentinel
`NONE` and the script exits non-zero without rolling back. The deploy workflow fails red and a
human must intervene.

### 5.2 Manual rollback to an arbitrary SHA
Either (a) push a revert commit on `main` (triggers a clean forward-deploy), or (b) SSH to the
VPS and re-tag:
```sh
ssh deploy@<vps-host>
cd /opt/contract-engine
docker pull ghcr.io/kingsleyonoh/contract-lifecycle-engine:sha-<short-sha>
docker tag ghcr.io/kingsleyonoh/contract-lifecycle-engine:sha-<short-sha> \
           ghcr.io/kingsleyonoh/contract-lifecycle-engine:latest
docker compose -f docker-compose.prod.yml up -d app
```

Option (a) is preferred — it keeps `main` as the source of truth. Only use (b) when `main` is
broken in a way a forward-deploy cannot fix quickly.

### 5.3 Manual dispatch redeploy
From the GitHub Actions UI, **Run workflow → Use workflow from: main** on `deploy.yml`
re-executes the full chain against the current `main` HEAD. Useful when the VPS was down
during the normal deploy or when a secret was rotated and the workflow needs to rebuild.

## 6. Image Tags on GHCR

Every push to `main` creates two tags:

- `:latest` — mutable pointer, always the most recent successful deploy
- `:sha-<short>` — immutable, guaranteed to be the exact image that passed CI for that commit

`docker compose pull` on the VPS always fetches `:latest`. `sha-<short>` tags are for manual
rollbacks and audit trails.

### Retention
GHCR retains tags indefinitely by default. Monthly cleanup is manual — older SHA tags beyond
the latest ~30 commits can be deleted via the GitHub UI or `gh api` calls. Don't delete tags
that are currently pulled on any live VPS; the rollback path will break.

## 7. Troubleshooting

### 7.1 `ci` fails on a commit that was green locally
- **NuGet cache miss dependency drift.** Delete the cache entry under Actions → Caches and
  re-run. The hash keys on csproj file contents; a different package-lock can resurrect stale
  packages.
- **Flaky test.** Common on `ContractEngine.Integration.Tests` when the workflow's Postgres
  service is slow to become healthy. The service healthcheck has 10 retries × 10s = 100s
  budget, which is enough in practice; if it times out, the job log shows `host is not ready`.
  Re-run.
- **xUnit collection serialisation.** Multiple `WebApplicationFactory<Program>` subclasses race
  on the process-global `Serilog.Log.Logger`. See CODEBASE_CONTEXT gotcha for Batch 004 — tests
  are tagged `[Collection("WebApplication")]` but a new factory without the collection attribute
  can break this.

### 7.2 `deploy` fails at `verify-secrets`
- Secret name mismatch. Check exact capitalisation: `HETZNER_HOST`, `HETZNER_USER`,
  `HETZNER_SSH_KEY`. Values cannot have trailing whitespace (GitHub strips this on save, but
  copy-paste from a terminal sometimes injects it).
- Re-create the secret by deleting and re-adding.

### 7.3 `deploy` fails at `build-and-push`
- **GHCR login failure.** The repo's `packages: write` permission must be enabled. Check
  `permissions:` block in `deploy.yml` and repo-level settings → Actions → General →
  Workflow permissions: "Read and write permissions".
- **Docker buildx cache stale.** Retries usually fix it. If persistent, add a cache-busting
  change or delete GHA caches via Actions → Caches.

### 7.4 `deploy` fails at `deploy-to-vps`
- **SSH auth failure.** Verify `HETZNER_SSH_KEY` matches `/home/deploy/.ssh/authorized_keys`
  on the VPS. Test manually from your workstation:
  ```sh
  ssh -i ~/.ssh/contract_engine_deploy deploy@<HETZNER_HOST> "docker ps"
  ```
- **SCP target dir missing.** `/opt/contract-engine` must exist and be `chown deploy:deploy`
  on the VPS. The Hetzner runbook's §3 covers the initial setup.
- **`docker compose` not found.** Ubuntu 22.04 and older may ship `docker-compose` (v1) only.
  24.04 ships the compose plugin. Verify with `docker compose version` on the VPS. Old v1
  command (`docker-compose up`) is NOT drop-in compatible — upgrade.

### 7.5 Smoke test fails with 200 locally but 503 from GitHub Actions runner
- The deploy workflow runs the smoke test FROM the VPS (via SSH), so this shouldn't happen.
  If it does, something is intercepting outbound HTTP from the VPS. Check `ufw status` — only
  default-deny-inbound should be set; outbound is default-allow.
- Caddy may be issuing a new cert on first 443 hit post-deploy. The HTTP-01 challenge needs
  port 80 reachable. Check `docker compose logs caddy | grep acme`.

### 7.6 Post-deploy 502 from Caddy
- `app` container isn't listening on 5000 yet. HEALTHCHECK waits 10s start period, so the
  first few probes may 502. The smoke-and-rollback script tolerates up to 60s of this.
- If it persists beyond 60s, the app failed to start. SSH in, `docker compose logs app`.

## 8. Metrics + Observability
- Workflow-run telemetry lives in GitHub Actions → Insights. No external APM is wired.
- Sentry captures runtime errors from the DEPLOYED app, not the workflow itself. For workflow
  failures, read the Actions job log.
- BetterStack monitors the deployed health endpoints (see `docs/operations/betterstack-setup.md`).

## 9. Local Parity

Before pushing to `main`, run locally what CI runs:

```sh
# Mirror of ci.yml `test` job — but including E2E (which CI skips).
dotnet build ContractEngine.sln -c Release
dotnet test
dotnet test tests/ContractEngine.E2E.Tests/
```

And mirror of the Docker build used by `deploy.yml`:

```sh
docker build -t contract-engine:local .
docker compose -f docker-compose.prod.yml config   # sanity-check the compose merge
```

This catches layering + .dockerignore issues that would otherwise only surface in GHA.

## 10. Evolution Playbook

| You want to... | Do this |
|----------------|---------|
| Add a smoke test at a new endpoint | Append a second `curl` loop to `smoke-and-rollback.sh`. Keep total wall-time < 90s. |
| Support blue-green deploys | Add a second `app-blue` service in `docker-compose.prod.yml` + Caddy upstream weight. Requires DB migration rethink (shared DB during cutover). Tracked in Phase 4 backlog. |
| Promote via tag (not branch) | Change deploy trigger to `on: push: tags: ['v*']`, add a `contents: write` permission if creating release notes. |
| Deploy to a second region | Clone `.github/workflows/deploy.yml` → `deploy-us.yml`, parameterise on `HETZNER_HOST_US`. Do NOT let both workflows fire in parallel on the same `main` push without a region-aware rollback strategy. |
