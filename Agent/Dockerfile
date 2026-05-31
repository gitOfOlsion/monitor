# Builds the agent for Linux (for use as a Docker-based agent or systemd service)
# For Windows: publish with `dotnet publish -r win-x64 --self-contained` on developer machine

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Shared/PeripheralMonitor.Shared.csproj  Shared/
COPY Agent/PeripheralMonitor.Agent.csproj    Agent/
RUN dotnet restore Agent/PeripheralMonitor.Agent.csproj

COPY Shared/ Shared/
COPY Agent/  Agent/
RUN dotnet publish Agent/PeripheralMonitor.Agent.csproj \
    -c Release -r linux-x64 --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "PeripheralMonitor.Agent.dll"]
