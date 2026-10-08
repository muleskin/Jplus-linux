# Running Jplus in Docker

The image serves HTTPS on `0.0.0.0:443` for `jarvis.oillie.cloud`.

## First run

1. Get a certificate for the domain on the host, e.g.
   `sudo certbot certonly --standalone -d jarvis.oillie.cloud`
   (`docker-compose.yml` mounts `/etc/letsencrypt` read-only and reads
   `live/jarvis.oillie.cloud/{fullchain,privkey}.pem`).
2. `cp Resources/env.example .env` and set `FISH_API_KEY` (plus anything else).
3. `docker compose up -d --build`
4. Log the brain in to your Claude subscription (once, kept in the `jarvis-home` volume):
   `docker compose exec -u jarvis jarvis claude`, then `docker compose restart`.
5. Open https://jarvis.oillie.cloud

## Settings (environment in docker-compose.yml)

| Variable | Default | Meaning |
|---|---|---|
| `JARVIS_HOST` | `0.0.0.0` | bind address |
| `JARVIS_LISTEN_PORT` | `443` | port inside the container |
| `JARVIS_DOMAIN` | `jarvis.oillie.cloud` | added to `JARVIS_ALLOWED_ORIGINS` so the Host/Origin checks accept it |
| `JARVIS_CERT_DIR` | `/certs` | directory holding `fullchain.pem` + `privkey.pem` |

Without a certificate the server falls back to a self-signed `localhost` one.
After certbot renews, run `docker compose restart` to pick up the new cert.

Data (`/data`) and the Claude Code login (`/home/jarvis`) are named volumes, so
rebuilding the image keeps them.
