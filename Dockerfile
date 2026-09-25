# syntax=docker/dockerfile:1

# Built on the host's own architecture, whatever the target image is. The output
# is static files (the Vite bundle), so nothing about it is platform-specific — but
# without --platform, a linux/arm64 image build runs this whole stage under QEMU
# on an amd64 runner, and `npm ci` there is both slow (90s vs 18s native) and
# prone to hanging outright: on 2026-09-23 two consecutive main builds sat in it
# for six hours until the runner killed them, with identical inputs to a build
# that had passed the day before. Emulate only the stages that produce
# platform-specific artifacts.
FROM --platform=$BUILDPLATFORM node:22-alpine AS web-build
WORKDIR /web
RUN npm install -g npm@11.6.2
COPY src/web/package*.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src

# The commit this image is built from, stamped into the assembly's
# InformationalVersion and surfaced in the UI, the /api/v1/system/status payload
# and service.version. It has to be passed in: .dockerignore excludes .git
# (correctly — it is large and secret-adjacent), so the SDK cannot read the
# repository the way it does for a local build.
#
# An *empty* value means "this is a release", so the default must not be empty.
# The release is the one claim this must never make by accident, and every build
# that is not CI-on-a-tag passes no build argument at all: the dev stack in
# docker-compose.yml, and any self-hoster running `docker build .`. Those default
# to "local" and read as 0.9.0+local — not as the 0.9.0 release, whose notes they
# would otherwise link a user to for a build that is not it.
#
# The asymmetry is deliberate. A release has to be asserted, by passing this
# explicitly empty (verified: an empty --build-arg does override a non-empty
# default). If that assertion ever fails to arrive, a release image says
# "0.9.0+local" — visibly wrong, and wrong in the safe direction. Nothing can
# claim to be a release by staying silent.
ARG SOURCE_REVISION="local"

# Directory.Build.props is where <Version> lives, so the image cannot be built
# without it. Copying it also switches on RestorePackagesWithLockFile, which was
# never in effect in this build before — hence the lockfile alongside the csproj.
#
# RestoreLockedMode is set here rather than inherited: the props file gates it on
# GITHUB_ACTIONS, which is not set inside a build container, so without this the
# lockfile would be silently regenerated on drift instead of failing. Locked mode
# is the point — the image should resolve the transitive versions CI resolved, and
# a stale lockfile should stop the build rather than quietly produce a different
# dependency graph in the artifact people actually run.
COPY src/Directory.Build.props ./
COPY src/api/DmarcAnalyzer.Api.csproj src/api/packages.lock.json ./api/
RUN dotnet restore ./api/DmarcAnalyzer.Api.csproj -p:RestoreLockedMode=true
COPY src/api/ ./api/
RUN dotnet publish ./api/DmarcAnalyzer.Api.csproj -c Release -o /out --no-restore \
    -p:SourceRevisionId="$SOURCE_REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Links the GHCR package to this repository (and powers "view source" on ghcr.io).
LABEL org.opencontainers.image.source="https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp" \
      org.opencontainers.image.description="Open-source, self-hosted, agency-first DMARC analyzer" \
      org.opencontainers.image.licenses="Apache-2.0"
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd -r dmarcanalyzer && useradd -r -g dmarcanalyzer dmarcanalyzer
COPY --from=dotnet-build --chown=dmarcanalyzer:dmarcanalyzer /out ./
COPY --from=web-build --chown=dmarcanalyzer:dmarcanalyzer /web/dist ./wwwroot

ENV ASPNETCORE_URLS=http://+:8080
ENV APP_MODE=api

USER dmarcanalyzer
EXPOSE 8080

ENTRYPOINT ["dotnet", "DmarcAnalyzer.Api.dll"]
