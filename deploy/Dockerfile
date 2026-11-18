# latticelog-server container image (P17-T14). Not built or published yet: the base images are pulled from mcr.microsoft.com
# on first build. Build from the repository root:
#   docker build -f deploy/Dockerfile -t latticelog-server:dev .
# Run (data and tokens on volumes, TLS certificate as a secret):
#   docker run -p 7400:7400 -v latticelog-data:/data -v ./tokens.json:/config/tokens.json:ro \
#     -v ./server.pfx:/run/secrets/server.pfx:ro -e LATTICELOG_CERT_PASSWORD=... latticelog-server:dev

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/LatticeLog.ServerHost/LatticeLog.ServerHost.csproj -c Release -o /out --no-self-contained

# Chiseled image: no shell, no package manager, non-root user (uid 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /out .
ENV LATTICELOG_DATA=/data \
    LATTICELOG_TOKENS=/config/tokens.json \
    LATTICELOG_LISTEN=https://0.0.0.0:7400 \
    LATTICELOG_CERT=/run/secrets/server.pfx
VOLUME ["/data"]
EXPOSE 7400
USER $APP_UID
# Health: GET /v1/health (no token needed; returns 503 if a namespace store failed).
ENTRYPOINT ["./latticelog-server"]
