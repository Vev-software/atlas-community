# syntax=docker/dockerfile:1

# Atlas Community Edition — self-hosted container image.
#
# The build context is this repository. Dependencies restore from nuget.org, so the container
# build no longer depends on a sibling checkout or a local feed. See docs/DEVELOPMENT.md
# § "Run with Docker".
#
#   docker build -t atlas-community:local .
#
# or just `docker compose up` from inside atlas-community.

# ---- build -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on just the manifests, so the (slow) restore layer is cached and only reruns
# when dependencies change.
COPY global.json nuget.config ./
COPY Directory.Build.props Directory.Packages.props ./
COPY src ./src

WORKDIR /src
# Strip any host build artefacts so the container restore/publish is hermetic. A host obj/ carries
# absolute host paths in project.assets.json and must never leak into the Linux build. (Docker/BuildKit
# users are also covered by Dockerfile.dockerignore; podman/buildah ignore that file, so we belt-and-brace.)
RUN find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
RUN dotnet restore src/Atlas.Api/Atlas.Api.csproj

# Publish the API (framework-dependent; the runtime image already carries ASP.NET Core).
RUN dotnet publish src/Atlas.Api/Atlas.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---- runtime ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only here for the container HEALTHCHECK below (the ASP.NET runtime image ships neither
# curl nor wget). Installed as root before we drop privileges.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# A writable data directory for the SQLite database, owned by the non-root `app` user that the
# .NET images provide. Mount a volume here to persist the catalogue across container restarts.
RUN mkdir -p /data && chown app:app /data

USER app
WORKDIR /app
COPY --from=build --chown=app:app /app ./

# Listen on 8080 (the .NET non-root default) and keep the database on the mounted volume.
# Self-host runs as a single tenant: identity is a fixed tenant from config, not request headers, so a
# caller cannot name another tenant or escalate roles (atlas#34). Override the tenant/roles if you like;
# multi-tenant identity is Fabric OIDC (fabric#3).
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Atlas="Data Source=/data/atlas.db" \
    Atlas__Identity__Mode=single-tenant
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Atlas.Api.dll"]
