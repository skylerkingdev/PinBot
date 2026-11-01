# PinBot updater image: keeps a server on the maintainer's signed releases (src/PinBot.Updater). Build context: repo root.
#   docker build -f deploy/updater.Dockerfile --build-arg UPDATE_PUBLIC_KEY=... --build-arg UPDATE_REPO=owner/name .
# Without the two build args the updater does nothing (a development build).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG UPDATE_PUBLIC_KEY=""
ARG UPDATE_REPO=""
WORKDIR /src
COPY src/PinBot.Contracts/ src/PinBot.Contracts/
COPY src/PinBot.Updater/ src/PinBot.Updater/
RUN dotnet publish src/PinBot.Updater/PinBot.Updater.csproj -c Release -o /app /p:UseAppHost=false \
    "/p:UpdatePublicKey=$UPDATE_PUBLIC_KEY" "/p:UpdateRepo=$UPDATE_REPO"

# the docker CLI and its compose plugin (static binaries) for the image's own architecture
# 27: the CLI line proven against Synology's Docker; a newer major needs testing there first
FROM docker:27-cli@sha256:851f91d241214e7c6db86513b270d58776379aacc5eb9c4a87e5b47115e3065c AS docker

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine@sha256:54d48f739ebffdef82388056d420af37ed39c722b8d5ee5dedca6878537c2737
COPY --from=docker /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
WORKDIR /app
COPY --from=build /app .
# root: the Docker socket it is given is root's
USER root
HEALTHCHECK --interval=15s --timeout=10s --start-period=10s --retries=1000 CMD ["dotnet", "/app/pinbot-updater.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/pinbot-updater.dll"]
