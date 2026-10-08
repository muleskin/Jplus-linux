#!/bin/sh
# Container entrypoint: install the TLS cert, then run Jplus as `jarvis`.
#
# Jplus reads its certificate from cert.pem / key.pem beside the binary (/app).
# Mount the real certificate for $JARVIS_DOMAIN so that $JARVIS_CERT_DIR
# (default /certs) holds fullchain.pem + privkey.pem, the Let's Encrypt names.
# They are copied in as root so a root-only privkey.pem still works. With no
# cert mounted, Jplus generates a self-signed one (CN=localhost); browsers warn.
set -eu

if [ "$(id -u)" = "0" ]; then
    certs="${JARVIS_CERT_DIR:-/certs}"
    if [ -f "$certs/fullchain.pem" ] && [ -f "$certs/privkey.pem" ]; then
        install -m 644 -o jarvis -g jarvis "$certs/fullchain.pem" /app/cert.pem
        install -m 600 -o jarvis -g jarvis "$certs/privkey.pem"  /app/key.pem
        echo "jplus-entrypoint: using the certificate from $certs"
    else
        echo "jplus-entrypoint: no fullchain.pem + privkey.pem in $certs - Jplus will use a self-signed certificate" >&2
    fi
    # Named volumes start out root-owned on first use.
    chown jarvis:jarvis /data /home/jarvis
    exec setpriv --reuid=jarvis --regid=jarvis --init-groups -- "$0" "$@"
fi

# Requests for https://$JARVIS_DOMAIN carry that Host and Origin; Jplus refuses
# dotted host names that are not declared here (its DNS-rebinding check).
if [ -n "${JARVIS_DOMAIN:-}" ]; then
    origin="https://${JARVIS_DOMAIN}"
    [ "${JARVIS_LISTEN_PORT}" = "443" ] || origin="${origin}:${JARVIS_LISTEN_PORT}"
    export JARVIS_ALLOWED_ORIGINS="${JARVIS_ALLOWED_ORIGINS:+${JARVIS_ALLOWED_ORIGINS},}${origin}"
fi

exec /app/Jplus --host "${JARVIS_HOST}" --port "${JARVIS_LISTEN_PORT}" "$@"
