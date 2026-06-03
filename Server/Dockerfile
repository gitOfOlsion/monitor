ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY Shared/PeripheralMonitor.Shared.csproj  Shared/
COPY Server/PeripheralMonitor.Server.csproj  Server/
# Copy build orchestration files so dotnet restore picks up centralised versions
COPY Directory.Build.props .
COPY Directory.Build.targets .
RUN dotnet restore Server/PeripheralMonitor.Server.csproj
COPY Shared/ Shared/
COPY Server/ Server/
RUN dotnet publish Server/PeripheralMonitor.Server.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5000
ENTRYPOINT ["dotnet", "PeripheralMonitor.Server.dll"]
