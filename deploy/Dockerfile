# PinBot server image. Build context: repository root.
#   docker build -f deploy/Dockerfile -t pinbot-server .
# compiled once on the build machine: the output is framework-dependent IL with no native host, so it runs on every
# architecture the runtime image has (amd64 NAS boxes, arm64 boards) without emulating the SDK
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY src/PinBot.Contracts/ src/PinBot.Contracts/
COPY src/PinBot.Server/ src/PinBot.Server/
RUN dotnet publish src/PinBot.Server/PinBot.Server.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY --from=build /app .
# the VPin agent for /download/pinbot.exe and release.json (this release's number and notes), put there by the
# release workflow
COPY deploy/agent/ /app/agent/
# the .NET 8+ images run as the unprivileged "app" user and listen on 8080
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=60s --timeout=5s --start-period=30s \
  CMD ["dotnet", "PinBot.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "PinBot.Server.dll"]
