# latticelog-server container image (P17-T14). Built and smoke-tested in CI (`deploy/smoke-test.sh`).
# Build from the repository root:
#   docker build -f deploy/Dockerfile -t latticelog-server:dev .
# Run (data on a volume, tokens and the TLS certificate mounted read-only):
#   docker run -p 7400:7400 -v latticelog-data:/data -v ./tokens.json:/config/tokens.json:ro \
#     -v ./server.pfx:/run/secrets/server.pfx:ro -e LATTICELOG_CERT_PASSWORD=... latticelog-server:dev

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# global.json pins the SDK of the development machines; the image brings its own .NET 10 SDK.
RUN rm -f global.json \
 && dotnet publish src/LatticeLog.ServerHost/LatticeLog.ServerHost.csproj -c Release -o /out --no-self-contained \
 && mkdir -p /data-template

# Chiseled image: no shell, no package manager, non-root user (uid 1654, $APP_UID in the .NET images).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /out .
# The data directory must belong to the non-root user, or the server cannot create its stores. A named volume mounted
# on /data takes this ownership the first time it is used.
COPY --from=build --chown=1654:1654 /data-template /data
ENV LATTICELOG_DATA=/data \
    LATTICELOG_TOKENS=/config/tokens.json \
    LATTICELOG_LISTEN=https://0.0.0.0:7400 \
    LATTICELOG_CERT=/run/secrets/server.pfx
VOLUME ["/data"]
EXPOSE 7400
USER 1654
# Health: GET /v1/health (no token needed; returns 503 if a namespace store failed).
ENTRYPOINT ["./latticelog-server"]
