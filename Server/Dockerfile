ARG DOTNET_VERSION=10.0

# ── Stage 1: Restore all projects ──────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS restore
WORKDIR /src
COPY Directory.Build.props ./
COPY Directory.Build.targets ./
COPY Shared/PeripheralMonitor.Shared.csproj  Shared/
COPY Server/PeripheralMonitor.Server.csproj  Server/
COPY Agent/PeripheralMonitor.Agent.csproj    Agent/
RUN dotnet restore Server/PeripheralMonitor.Server.csproj
RUN dotnet restore Agent/PeripheralMonitor.Agent.csproj

# ── Stage 2: Build server ──────────────────────────────────────────────
FROM restore AS build-server
COPY Shared/ Shared/
COPY Server/ Server/
COPY Agent/  Agent/
RUN dotnet publish Server/PeripheralMonitor.Server.csproj \
    -c Release -o /app/publish

# ── Stage 3: Build agent for Windows ──────────────────────────────────
# The single-file exe is also the Windows installer: the server appends each
# institution's config to it at download time (see AgentPackageService).
# Compression roughly halves the download size.
FROM restore AS build-agent-win
COPY Shared/ Shared/
COPY Agent/  Agent/
RUN dotnet publish Agent/PeripheralMonitor.Agent.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o /agent/windows

# ── Stage 4: Build agent for Linux ───────────────────────────────────
FROM restore AS build-agent-linux
COPY Shared/ Shared/
COPY Agent/  Agent/
RUN dotnet publish Agent/PeripheralMonitor.Agent.csproj \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o /agent/linux

# ── Stage 5: Runtime ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# Copy server
COPY --from=build-server /app/publish .

# Copy agent packages (served by AgentPackageService)
COPY --from=build-agent-win   /agent/windows /app/agent-packages/windows/
COPY --from=build-agent-linux /agent/linux   /app/agent-packages/linux/

# Copy installer scripts
COPY Agent/Installers/install.ps1 /app/agent-packages/installers/install.ps1
COPY Agent/Installers/install.bat /app/agent-packages/installers/install.bat
COPY Agent/Installers/install.sh  /app/agent-packages/installers/install.sh
COPY Agent/Installers/linux-run-header.sh /app/agent-packages/installers/linux-run-header.sh


EXPOSE 5000
ENTRYPOINT ["dotnet", "PeripheralMonitor.Server.dll"]
