# The Lodge server as a container image (Lodge 2.37+). Build from the repository root:
#   docker build -f server/docker/Dockerfile -t elans-lodge .
# Everything the Lodge keeps lives in /data (one volume); settings come from LODGE_* environment variables (the same as
# /etc/lodge/lodge.env of a normal install). It listens on 8080 inside the container; put your web server (HTTPS) in front
# of it as for a normal install (server/deploy/reverse-proxy.md). See server/docker/README.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY shared/Elans.Shared/ shared/Elans.Shared/
COPY server/Lodge.Server/ server/Lodge.Server/
RUN dotnet publish server/Lodge.Server -c Release -o /out --nologo -v q -p:UseAppHost=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# lodge-admin and lodge-backup are the same scripts as on a normal install; they need these (bash, tar, flock are there;
# gawk: names compare without case in any language, which Debian's default awk can't do)
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl openssl ca-certificates gawk \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out /app
COPY server/deploy/lodge-admin server/deploy/lodge-backup /usr/local/bin/
RUN chmod 0755 /usr/local/bin/lodge-admin /usr/local/bin/lodge-backup \
 && mkdir -p /data /backups && chown app:app /data /backups

# LODGE_INSTALL=docker: the Lodge tells the Outpost it runs in a container, so "update your server" shows the Docker command
ENV LODGE_URLS=http://0.0.0.0:8080 \
    LODGE_DATA=/data \
    LODGE_BACKUP_DIR=/backups \
    LODGE_PATHBASE=/lodge \
    LODGE_INSTALL=docker \
    LODGE_ENV_FILE=/dev/null \
    LODGE_CODES_FILE=/data/codes \
    LODGE_OWNER=app:app \
    DOTNET_gcServer=0

USER app
VOLUME ["/data", "/backups"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS "http://127.0.0.1:8080${LODGE_PATHBASE}/health" >/dev/null || exit 1
ENTRYPOINT ["/app/lodge"]
