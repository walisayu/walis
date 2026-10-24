# syntax=docker/dockerfile:1

# Base images are pinned by digest so a rebuild gets exactly the layers that were reviewed; the tag is kept for readability only.
# To bump: `docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0` (or curl the registry's manifest and read the
# Docker-Content-Digest header) and replace the digest together with the version in the comment.
# sdk:10.0 = 10.0.402 (runtime 10.0.13), resolved 2026-10-21
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src

# The build context is the repository root (docker build -f source/Dockerfile .): global.json pins the SDK, nuget.config
# limits the feed to nuget.org and Directory.Build.props turns on lock files and auditing for the restore below.
COPY global.json nuget.config Directory.Build.props ./
COPY source/Walis.Api/Walis.Api.csproj source/Walis.Api/packages.lock.json source/Walis.Api/
COPY source/Walis.Core/Walis.Core.csproj source/Walis.Core/packages.lock.json source/Walis.Core/
# --locked-mode: the graph must match the committed packages.lock.json or the build stops (NU1004).
# The csproj lists every release RID, so this restore also downloads their runtime packs; the layer is cached afterwards.
RUN dotnet restore source/Walis.Api/Walis.Api.csproj --locked-mode

COPY source/Walis.Api/ source/Walis.Api/
COPY source/Walis.Core/ source/Walis.Core/

FROM build AS api-publish
RUN dotnet publish source/Walis.Api/Walis.Api.csproj -c Release -o /out/api --no-restore

# aspnet:10.0 = 10.0.13, resolved 2026-10-21
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS api
USER root
# Walis runs user-written build scripts inside the container, so the runtime needs the common CI tools:
# the built-in actions use git/ssh/rsync/zip/curl directly, and bash is the default shell for scripts.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash ca-certificates curl git openssh-client rsync unzip zip \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

# Create the directories first and hand them to the runtime identity; a named volume mounted for the first time inherits this owner,
# otherwise the volume belongs to root and the app cannot write walis.db.
RUN mkdir -p /app/data /workspace \
    && chown -R $APP_UID:$APP_UID /app/data /workspace \
    && chmod 700 /app/data /workspace

COPY --from=api-publish /out/api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Walis.Api.dll"]
