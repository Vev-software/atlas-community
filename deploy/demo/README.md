# Atlas Community — public demo

A throwaway, self-updating, internet-facing instance of Atlas Community. Anyone can open it and
click around; it **wipes itself every night** and tracks the newest released `main`. Think of it as
the running counterpart to the static homepage: a "try it live" link you can hand to people.

> This is intentionally **not** the secure self-host. It runs in **single-tenant mode with no
> login** (everyone gets full architect access) because nothing here is real and it resets nightly.
> For a real self-host, use the repository-root `docker-compose.yml` (bundled Keycloak).

> Container engine: the reference host runs **podman**, so the commands below use `podman`; the
> `docker` equivalents (`docker compose`, `docker login`) are identical. `podman compose` reads the
> same compose file.

## What's here

| File | Purpose |
|------|---------|
| `docker-compose.yml` | Demo stack: Atlas (GHCR image, single-tenant) + cloudflared + auto-update watcher. |
| `.env.example` | Copy to `.env`; set the Cloudflare Tunnel token. |
| `reset-demo.sh` | Wipe the SQLite volume, pull the newest image, restart clean (detects podman/docker). |
| `atlas-demo-reset.service` / `.timer` | systemd units that run the reset nightly at 04:00. |

## Quick start (on the demo host)

```bash
git clone https://github.com/Vev-software/atlas-community.git /opt/atlas-community-demo
cd /opt/atlas-community-demo/deploy/demo
cp .env.example .env
# edit .env: paste CLOUDFLARE_TUNNEL_TOKEN
podman compose up -d      # Docker: docker compose up -d
```

Then, in the Cloudflare Zero Trust dashboard, map your public hostname (e.g. `demo.example.com`)
to `http://atlas:8080` on the tunnel. **Do not** add a Cloudflare Access policy — the demo is meant
to be open.

## Nightly reset

```bash
sudo cp atlas-demo-reset.service atlas-demo-reset.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now atlas-demo-reset.timer
systemctl list-timers atlas-demo-reset.timer
```

## Auto-update

The included `watchtower` service polls GHCR and redeploys when `:latest` moves (i.e. when a new
version is released to `main`) — safe here because the instance is stateless. On a podman host you can
instead use the podman-native path: the `atlas` service carries the `io.containers.autoupdate=registry`
label, so `systemctl --user enable --now podman-auto-update.timer` keeps it current and you can drop
the watchtower service. Either way, the nightly reset also `pull`s, so the demo lands on the newest
image within 24h regardless.

## Where this runs

Intended for the same Proxmox host as the private Atlas Enterprise instance (see the Atlas Enterprise
deployment guide for the full host, Cloudflare, backup and update walkthrough). Because it is public
and untrusted, run it in its **own VM/LXC on an isolated network segment**, away from the private
instance and your LAN.
