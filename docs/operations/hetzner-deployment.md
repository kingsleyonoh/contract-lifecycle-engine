# Hetzner VPS Deployment — Operator Runbook

> Audience: operators provisioning and maintaining the Contract Lifecycle Engine production host.
> Scope: VPS provisioning, user / firewall / Docker setup, first deploy, Caddy + TLS, .env
> management, backups, rollback, incident response.
> Shipped: Batch 026 (2026-04-17).

## 1. Overview

The Contract Lifecycle Engine runs on a single Hetzner Cloud VPS fronted by Caddy (auto-TLS via
Let's Encrypt). The production stack is defined in `docker-compose.prod.yml` at the repo root and
is pulled + restarted by the `deploy.yml` GitHub Actions workflow on every push to `main`.

### Stack components (prod)

| Service | Image | Purpose | Exposed |
|---------|-------|---------|---------|
| `caddy` | `lucaslorentz/caddy-docker-proxy:ci-alpine` | Reverse proxy + auto-TLS for `contracts.kingsleyonoh.com` | 80/tcp, 443/tcp (host) |
| `app` | `ghcr.io/kingsleyonoh/contract-lifecycle-engine:latest` | Contract Engine API | 5000/tcp (internal only) |
| `db` | `postgres:16-alpine` | Primary database | internal only |
| `nats` | `nats:2-alpine` | Compliance Ledger JetStream (optional profile) | internal only |

All services join a Docker-managed bridge network. Only Caddy is reachable from the public
internet; everything else is firewalled at both the ufw and Docker network layers.

## 2. VPS Provisioning

### Recommended size
- **CX22** (2 vCPU / 4 GB RAM / 40 GB SSD) — sufficient for < 10 tenants + < 10k obligations.
- **CX32** (2 vCPU / 8 GB RAM / 80 GB SSD) — recommended when RAG extraction job volume grows.
- **OS:** Ubuntu 24.04 LTS (noble).
- **Location:** any Hetzner region; `fsn1` (Falkenstein) keeps latency low for EU traffic.

### DNS
Before provisioning, create an A record for `contracts.kingsleyonoh.com` pointing to the planned
VPS IPv4 address in the Hetzner DNS Console (or whichever DNS provider you use). Caddy's first
cert issuance will fail silently until the DNS record resolves to this host — verify with
`dig +short contracts.kingsleyonoh.com` before starting Caddy for the first time.

### SSH bootstrap (from your admin workstation)

```sh
# After provisioning, grab the root SSH key and initial IP from the Hetzner Console.
ssh root@<vps-ip>

# First login — set hostname, update packages.
hostnamectl set-hostname contract-engine-prod
apt update && apt upgrade -y
```

## 3. Non-root Deploy User

The `deploy` user owns `/opt/contract-engine` and is the identity the GitHub Actions deploy
workflow uses via `HETZNER_SSH_KEY`. No sudo password is needed — we only run Docker commands.

```sh
# As root on the VPS:
adduser --disabled-password --gecos "" deploy
usermod -aG docker deploy              # after installing Docker below
mkdir -p /home/deploy/.ssh
chmod 700 /home/deploy/.ssh

# Paste the GitHub Actions public key (matches HETZNER_SSH_KEY secret):
nano /home/deploy/.ssh/authorized_keys
chmod 600 /home/deploy/.ssh/authorized_keys
chown -R deploy:deploy /home/deploy/.ssh

# Seed the app directory — the deploy workflow will scp files into here.
mkdir -p /opt/contract-engine
chown -R deploy:deploy /opt/contract-engine
```

Generate the SSH keypair on your admin workstation (or in a CI sandbox) — ED25519 recommended:

```sh
ssh-keygen -t ed25519 -C "github-actions-deploy" -f ~/.ssh/contract_engine_deploy
# Public key → /home/deploy/.ssh/authorized_keys on the VPS
# Private key → HETZNER_SSH_KEY secret in the GitHub repo settings
```

## 4. Firewall (ufw)

Ubuntu 24.04 ships ufw inactive; activate with an explicit allow-list. 22/tcp is restricted to
the admin IP only — GitHub Actions does NOT need SSH access to 22; it uses the SSH key pair
configured above to reach 22 from GitHub's dynamic runner IPs. If you want to tighten further,
scope 22 to a VPN only.

```sh
# As root on the VPS:
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp comment "ssh"
ufw allow 80/tcp comment "http (Caddy + Let's Encrypt)"
ufw allow 443/tcp comment "https (Caddy)"
ufw enable
ufw status verbose
```

Port 80 MUST stay open — Let's Encrypt's HTTP-01 challenge needs it for both initial issuance
and every renewal. Blocking 80 breaks TLS 60–90 days after the last cert.

## 5. Docker + Compose Plugin

```sh
# As root on the VPS, install Docker Engine from Docker's APT repo.
apt install -y ca-certificates curl gnupg
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
chmod a+r /etc/apt/keyrings/docker.gpg
echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \
  $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null
apt update
apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
docker --version && docker compose version
```

Add `deploy` to the docker group so the workflow can run `docker compose` without sudo:

```sh
usermod -aG docker deploy
```

## 6. First Deploy

### 6.1 Clone the repo + place compose file

```sh
# As deploy user on the VPS:
cd /opt/contract-engine
# The compose file lives here; the first deploy workflow run will scp it + the deploy scripts.
# For manual bootstrapping you can clone via https and just copy the needed files:
git clone https://github.com/kingsleyonoh/contract-lifecycle-engine.git repo
cp repo/docker-compose.prod.yml .
cp -r repo/scripts scripts
chmod +x scripts/deploy/*.sh
rm -rf repo
```

### 6.2 Create `.env` on the VPS

`.env` is NEVER committed. Create it on the VPS with production values:

```sh
# As deploy user:
nano /opt/contract-engine/.env
```

Required values (copy from `.env.example` and replace placeholders):

```
# Database (service uses DATABASE_URL; POSTGRES_* feed the db container)
POSTGRES_USER=contract_engine
POSTGRES_PASSWORD=<strong-random-password>
POSTGRES_DB=contract_engine
DATABASE_URL=Host=db;Port=5432;Database=contract_engine;Username=contract_engine;Password=<same-as-above>

# Runtime
ASPNETCORE_ENVIRONMENT=Production
AUTO_MIGRATE=true
AUTO_SEED=true
SELF_REGISTRATION_ENABLED=false

# Document storage mount — path inside the container; volume is provisioned by compose
DOCUMENT_STORAGE_PATH=/app/data/documents

# Observability
SENTRY_DSN=<from Sentry project settings>
LOG_LEVEL=Information

# Ecosystem integrations (set _ENABLED=true once URLs + keys are wired)
RAG_PLATFORM_URL=https://ai.kingsleyonoh.com
RAG_PLATFORM_API_KEY=<secret>
RAG_PLATFORM_ENABLED=true

NOTIFICATION_HUB_URL=https://notify.kingsleyonoh.com
NOTIFICATION_HUB_API_KEY=<secret>
NOTIFICATION_HUB_ENABLED=true

WEBHOOK_SIGNING_SECRET=<openssl rand -hex 32>
WEBHOOK_ENGINE_ENABLED=true

WORKFLOW_ENGINE_URL=https://workflows.kingsleyonoh.com
WORKFLOW_ENGINE_API_KEY=<secret>
WORKFLOW_ENGINE_ENABLED=false

NATS_URL=nats://nats:4222
COMPLIANCE_LEDGER_ENABLED=false

INVOICE_RECON_URL=
INVOICE_RECON_API_KEY=
INVOICE_RECON_ENABLED=false
```

`chmod 600 /opt/contract-engine/.env` so other system users can't read it.

### 6.3 First-time startup

```sh
# As deploy user:
cd /opt/contract-engine
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d
docker compose -f docker-compose.prod.yml ps
```

Verify:
- `app` → `Up` and `healthy` after ~20s (HEALTHCHECK hits `/health` every 30s)
- `db` → `Up` and `healthy`
- `caddy` → `Up`

### 6.4 Grab the initial API key

`AUTO_SEED=true` creates the default tenant on first boot and prints the API key once. Capture
it from the logs — on a restart it is NOT printed again.

```sh
docker compose -f docker-compose.prod.yml logs app | grep "API Key:"
```

Store the key in a password manager. Anyone holding it has full write access to the default
tenant.

### 6.5 Verify TLS + health

From your workstation:

```sh
# HTTPS cert issued + basic health
curl -i https://contracts.kingsleyonoh.com/health

# Database health
curl -i https://contracts.kingsleyonoh.com/health/db

# Integration readiness
curl -i https://contracts.kingsleyonoh.com/health/ready

# Authenticated round-trip
curl -H "X-API-Key: cle_live_<your-key>" https://contracts.kingsleyonoh.com/api/tenants/me
```

Caddy issues the cert on first request to 443 — the first `curl` may take ~5s while the ACME
HTTP-01 handshake completes.

## 7. Ongoing Deploys

After the first bootstrap, every push to `main` triggers `.github/workflows/deploy.yml`:

1. Re-runs the full test suite against a workflow-hosted Postgres 16 service.
2. Builds the Docker image and pushes `:latest` + `:sha-<short-sha>` to GHCR.
3. SCPs `docker-compose.prod.yml` + `scripts/deploy/*.sh` to `/opt/contract-engine` on the VPS.
4. SSHes in as `deploy` and runs:
   - `remember-previous-tag.sh` — records the currently-running image digest to `.previous-tag`
   - `docker compose pull app` — fetch the new `:latest`
   - `docker compose up -d` — rolling restart
   - `smoke-and-rollback.sh https://contracts.kingsleyonoh.com/health/ready` — poll 30× with 2s
     between attempts; on persistent non-200, re-tag the previous digest to `:latest` and
     restart (see §8).

See `docs/operations/ci-cd-pipeline.md` for the CI/CD side of the story.

## 8. Rollback

### Automatic rollback (smoke test failure)

`smoke-and-rollback.sh` fails the deploy workflow and re-tags the previous image digest back to
`:latest`. Caddy keeps serving while the app container restarts.

### Manual rollback (to an arbitrary SHA)

```sh
# As deploy user on the VPS:
cd /opt/contract-engine
docker pull ghcr.io/kingsleyonoh/contract-lifecycle-engine:sha-<short-sha>
docker tag ghcr.io/kingsleyonoh/contract-lifecycle-engine:sha-<short-sha> \
           ghcr.io/kingsleyonoh/contract-lifecycle-engine:latest
docker compose -f docker-compose.prod.yml up -d app
curl -i https://contracts.kingsleyonoh.com/health/ready
```

Immutable SHA tags on GHCR make this safe — the digest is guaranteed to be the one that passed
CI at the time it was pushed.

## 9. Backups

### 9.1 Database (nightly `pg_dump`)

Add a cron entry under the `deploy` user:

```sh
# crontab -e (as deploy):
0 3 * * * /opt/contract-engine/scripts/backup-db.sh >> /var/log/contract-engine-backup.log 2>&1
```

Minimal backup script (create this on the VPS manually — not committed to avoid accidental
exposure of backup paths in the repo):

```sh
#!/usr/bin/env bash
# /opt/contract-engine/scripts/backup-db.sh
set -euo pipefail
DATE=$(date +%Y%m%d-%H%M)
OUT=/opt/contract-engine/backups
mkdir -p "${OUT}"
docker compose -f /opt/contract-engine/docker-compose.prod.yml exec -T db \
  pg_dump -U contract_engine contract_engine | gzip > "${OUT}/contract_engine_${DATE}.sql.gz"
# Keep 14 days of dumps
find "${OUT}" -name "contract_engine_*.sql.gz" -mtime +14 -delete
```

### 9.2 Hetzner daily snapshots (recommended)

Enable **Hetzner Cloud daily snapshots** in the Console. Snapshots run at the platform level and
capture the full VPS state including Docker volumes. Backup plus snapshot gives you belt +
braces — use `pg_dump` for rapid point-in-time restore into a fresh DB, snapshot for full-system
disaster recovery.

### 9.3 Uploaded documents

The `documents` named volume holds all uploaded contract PDFs. Include it in the snapshot plan.
For off-site redundancy, mount it as an additional `restic`/`rclone` backup source (out of scope
for v1 — track via ops backlog).

## 10. Log Collection

Everything goes to Docker's JSON log driver — no external log aggregator is wired in v1.

```sh
# Live tail of the app container:
docker compose -f docker-compose.prod.yml logs -f app

# Last 200 lines:
docker compose -f docker-compose.prod.yml logs --tail 200 app

# Filter by request_id (Serilog enriches every log event with this):
docker compose -f docker-compose.prod.yml logs app | grep '"request_id":"req_abc123"'
```

For longer retention, pipe the JSON stream into a central log store (Loki + Promtail or
Grafana Cloud Logs are both lightweight options). Sentry already captures errors separately.

## 11. Incident Response Checklist

When paged by BetterStack (see `docs/operations/betterstack-setup.md`):

1. **Acknowledge in BetterStack** to stop the escalation timer.
2. Run the probes manually from your workstation:
   ```sh
   curl -i https://contracts.kingsleyonoh.com/health
   curl -i https://contracts.kingsleyonoh.com/health/db
   curl -i https://contracts.kingsleyonoh.com/health/ready
   ```
3. **SSH into the VPS** as `deploy` and check container health:
   ```sh
   cd /opt/contract-engine
   docker compose -f docker-compose.prod.yml ps
   docker compose -f docker-compose.prod.yml logs --tail 200 app
   ```
4. **If the app container is in `Restarting` loop**, the most common causes are:
   - Missing / malformed `.env` values — `docker compose config` will surface substitution errors
   - DB migration failure — check for migration stack traces in `app` logs
   - Exhausted disk — `df -h` on the host; prune old Docker images with `docker system prune -a`
5. **If DB is unhealthy**, check `docker compose logs db`. Typical causes: disk full, OOM kill
   by kernel. `docker stats` shows current memory usage.
6. **If Caddy is failing to renew certs**, check `docker compose logs caddy` for ACME errors.
   Usually caused by port 80 being blocked (firewall drift) or DNS changes.
7. **If escalation is needed**, trigger a manual rollback via the `deploy.yml` workflow's
   `workflow_dispatch` with the previous SHA as input — or roll back manually (§8).
8. **Post-incident**, capture the root cause in `docs/progress.md` under Gotchas so the next
   operator benefits.

## 12. Security Notes

- Never commit `.env`. `.gitignore` already excludes it.
- `chmod 600 /opt/contract-engine/.env` so other VPS users cannot read secrets.
- Rotate `POSTGRES_PASSWORD` by: (a) creating a new Postgres user with replication perms, (b)
  updating `DATABASE_URL`, (c) restarting the app, (d) revoking the old user. Don't attempt
  in-place password rotation on the active user — Npgsql connection pooling will cache the old
  credential for several minutes.
- Rotate `WEBHOOK_SIGNING_SECRET` per `docs/operations/webhook-engine-setup.md` §2.
- Hetzner's default SSH config allows password auth on day one — disable with:
  ```sh
  sed -i 's/^#PasswordAuthentication yes/PasswordAuthentication no/' /etc/ssh/sshd_config
  sed -i 's/^PermitRootLogin yes/PermitRootLogin prohibit-password/' /etc/ssh/sshd_config
  systemctl restart ssh
  ```
- Review `docker image ls` monthly and `docker system prune -a` to remove unused tags. GHCR
  retains the source of truth; pruning the VPS does not lose rollback targets.

## 13. Decommissioning

To tear down the production host (e.g. moving to a different provider):

1. Final `pg_dump` of the database.
2. Final `tar -czvf documents.tar.gz /var/lib/docker/volumes/contract-engine_documents/_data`.
3. `docker compose -f docker-compose.prod.yml down -v` — stops containers + removes volumes.
4. Delete the VPS in Hetzner Console.
5. Delete the DNS A record.
6. Restore the dump + documents on the new host before repointing DNS.
