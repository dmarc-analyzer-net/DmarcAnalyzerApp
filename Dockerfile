# syntax=docker/dockerfile:1

FROM node:22-alpine AS web-build
WORKDIR /web
RUN npm install -g npm@11.6.2
COPY src/web/package*.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY src/api/DmarcAnalyzer.Api.csproj ./api/
RUN dotnet restore ./api/DmarcAnalyzer.Api.csproj
COPY src/api/ ./api/
RUN dotnet publish ./api/DmarcAnalyzer.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Links the GHCR package to this repository (and powers "view source" on ghcr.io).
LABEL org.opencontainers.image.source="https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp" \
      org.opencontainers.image.description="Open-source, self-hosted, agency-first DMARC analyzer" \
      org.opencontainers.image.licenses="Apache-2.0"
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=dotnet-build /out ./
COPY --from=web-build /web/dist ./wwwroot

ENV ASPNETCORE_URLS=http://+:8080
ENV APP_MODE=api

EXPOSE 8080

ENTRYPOINT ["dotnet", "DmarcAnalyzer.Api.dll"]
