# Linux agent image — for Windows use: dotnet publish -r win-x64 --self-contained
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY Shared/PeripheralMonitor.Shared.csproj  Shared/
COPY Agent/PeripheralMonitor.Agent.csproj    Agent/
COPY Directory.Build.props .
COPY Directory.Build.targets .
RUN dotnet restore Agent/PeripheralMonitor.Agent.csproj
COPY Shared/ Shared/
COPY Agent/  Agent/
RUN dotnet publish Agent/PeripheralMonitor.Agent.csproj \
    -c Release -r linux-x64 --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "PeripheralMonitor.Agent.dll"]
