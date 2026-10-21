# PinBot server image. Build context: repository root.
#   docker build -f deploy/Dockerfile -t pinbot-server .
# compiled once on the build machine: the output is framework-dependent IL with no native host, so it runs on every
# architecture the runtime image has (amd64 NAS boxes, arm64 boards) without emulating the SDK
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY src/PinBot.Contracts/ src/PinBot.Contracts/
COPY src/PinBot.Server/ src/PinBot.Server/
RUN dotnet publish src/PinBot.Server/PinBot.Server.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
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
