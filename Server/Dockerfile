FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Shared/PeripheralMonitor.Shared.csproj    Shared/
COPY Server/PeripheralMonitor.Server.csproj    Server/
RUN dotnet restore Server/PeripheralMonitor.Server.csproj

COPY Shared/ Shared/
COPY Server/  Server/
RUN dotnet publish Server/PeripheralMonitor.Server.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5000
ENTRYPOINT ["dotnet", "PeripheralMonitor.Server.dll"]
