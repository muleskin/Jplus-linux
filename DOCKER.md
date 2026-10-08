# Running Jplus in Docker

`docker-compose.yml` runs Jplus behind the host's Traefik (`/docker/traefik-a5as`),
which owns ports 80/443. Traefik gets the Let's Encrypt certificate for
`jarvis.oillie.cloud`, redirects HTTP to HTTPS and forwards to Jplus, which
listens on `0.0.0.0:443` inside the container (plain HTTP, `JARVIS_TLS=off`).
No certbot run is needed.

## First run

1. `cp Resources/env.example .env` and set `FISH_API_KEY` and `JARVIS_PASSCODE`
   (the site is public, so the passcode is what keeps strangers out).
2. `docker compose up -d --build`
3. Log the brain in to your Claude subscription (once, kept in the `jarvis-home` volume):
   `docker compose exec -u jarvis jarvis claude`, then `docker compose restart`.
4. Open https://jarvis.oillie.cloud

## Settings (environment in docker-compose.yml)

| Variable | Default | Meaning |
|---|---|---|
| `JARVIS_HOST` | `0.0.0.0` | bind address |
| `JARVIS_LISTEN_PORT` | `443` | port inside the container |
| `JARVIS_DOMAIN` | `jarvis.oillie.cloud` | added to `JARVIS_ALLOWED_ORIGINS` so the Host/Origin checks accept it |
| `JARVIS_PASSCODE` | unset | require sign-in at `/login` (set it in `.env`) |
| `JARVIS_SESSION_DAYS` | `30` | how long a sign-in lasts |
| `JARVIS_TLS` | `on` (compose sets `off`) | `off` serves plain HTTP for a TLS-terminating proxy |
| `JARVIS_CERT_DIR` | `/certs` | with TLS on: directory holding `fullchain.pem` + `privkey.pem`; without them a self-signed `localhost` cert is used |

Data (`/data`) and the Claude Code login (`/home/jarvis`) are named volumes, so
rebuilding the image keeps them.
