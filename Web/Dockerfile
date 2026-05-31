FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Shared/PeripheralMonitor.Shared.csproj  Shared/
COPY Web/PeripheralMonitor.Web.csproj        Web/
RUN dotnet restore Web/PeripheralMonitor.Web.csproj

COPY Shared/ Shared/
COPY Web/    Web/
RUN dotnet publish Web/PeripheralMonitor.Web.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5001
ENTRYPOINT ["dotnet", "PeripheralMonitor.Web.dll"]
