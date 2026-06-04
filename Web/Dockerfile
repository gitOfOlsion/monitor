ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY Shared/PeripheralMonitor.Shared.csproj  Shared/
COPY Web/PeripheralMonitor.Web.csproj        Web/
COPY Directory.Build.props .
COPY Directory.Build.targets .
RUN dotnet restore Web/PeripheralMonitor.Web.csproj
COPY Shared/ Shared/
COPY Web/    Web/
RUN dotnet publish Web/PeripheralMonitor.Web.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5001
ENTRYPOINT ["dotnet", "PeripheralMonitor.Web.dll"]
