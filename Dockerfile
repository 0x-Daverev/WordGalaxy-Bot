# PacketBot.Server in a Docker container.
# Configuration only through environment variables (see docs/DOCKER.md);
# module settings and purchase records live in the /data volume.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Restore first so the package layer is cached between code changes.
# Version of every project (VersionPrefix).
COPY Directory.Build.props ./
COPY PacketBot.Core/PacketBot.Core.csproj PacketBot.Core/
COPY PacketBot.Server/PacketBot.Server.csproj PacketBot.Server/
RUN dotnet restore PacketBot.Server/PacketBot.Server.csproj
COPY PacketBot.Core/ PacketBot.Core/
COPY PacketBot.Server/ PacketBot.Server/
RUN dotnet publish PacketBot.Server/PacketBot.Server.csproj -c Release --no-restore \
      -o /app -p:DebugType=none

# Debian ASP.NET runtime (web panel): includes ICU (Spanish number format)
# and tzdata.
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Settings go to $XDG_DATA_HOME/WordGalaxyPacketBot/settings = /data/...
# The folder exists in the image owned by the non-root user, so a new
# named volume mounted on /data starts with the right owner.
ENV XDG_DATA_HOME=/data \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PACKETBOT_FAILURE_EXIT_DELAY=300 \
    PACKETBOT_WEB_PORT=8080 \
    ASPNETCORE_HTTP_PORTS=
# Web panel (only with PACKETBOT_WEB_PASSWORD); publish it through a TLS proxy.
EXPOSE 8080
RUN mkdir -p /data/WordGalaxyPacketBot/settings && chown -R app:app /data
USER app
VOLUME /data
ENTRYPOINT ["dotnet", "PacketBot.Server.dll"]
