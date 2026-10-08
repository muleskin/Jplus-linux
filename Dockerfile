# Jplus (JARVIS for Claude Code) in a container, serving HTTPS on 0.0.0.0:443.
#   docker compose up -d --build        (see docker-compose.yml and DOCKER.md)

# ── build: self-contained single-file binary ────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Jplus.csproj ./
RUN RID=linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
 && dotnet restore Jplus.csproj -r "$RID"
COPY . .
RUN RID=linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
 && dotnet publish Jplus.csproj -c Release -r "$RID" -o /out \
 && chmod +x /out/Jplus

# ── runtime ─────────────────────────────────────────────────────────────────
# Node is here for the brain: it runs the Claude Code CLI (npm package).
# tmux lets answer_dialog press keys in Claude Code sessions run inside it.
FROM node:22-bookworm-slim
ARG CLAUDE_CODE_VERSION=latest

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates libssl3 git curl tmux \
 && rm -rf /var/lib/apt/lists/* \
 && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
 && npm cache clean --force

# The app runs as an unprivileged user; the entrypoint starts as root only to
# install the TLS cert and fix volume ownership, then drops to this user.
RUN useradd --create-home --home-dir /home/jarvis --shell /bin/bash jarvis \
 && mkdir -p /app /data /certs \
 && chown jarvis:jarvis /app /data /home/jarvis

COPY --from=build --chown=jarvis:jarvis /out/Jplus /app/Jplus
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/jplus-entrypoint

# JARVIS_ENV_FILE: the settings page's .env lives on the data volume, so what
# it saves survives rebuilding the image (/app is part of the image).
ENV JARVIS_DATA_DIR=/data \
    JARVIS_ENV_FILE=/data/.env \
    JARVIS_HOST=0.0.0.0 \
    JARVIS_LISTEN_PORT=443 \
    JARVIS_DOMAIN=jarvis.oillie.cloud \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/app/.net \
    HOME=/home/jarvis

WORKDIR /app
VOLUME ["/data", "/home/jarvis"]
EXPOSE 443

ENTRYPOINT ["/usr/local/bin/jplus-entrypoint"]
