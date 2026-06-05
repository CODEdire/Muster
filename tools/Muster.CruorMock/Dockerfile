# Local-only container for the Cruor mock. Built by the AppHost in run mode (see CruorMockExtensions),
# never published. Build context is the repo root so Directory.Build.props / Directory.Packages.props
# (central package management) are visible to the restore.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the central package manifests + this project only.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY tools/Muster.CruorMock/Muster.CruorMock.csproj tools/Muster.CruorMock/
RUN dotnet restore tools/Muster.CruorMock/Muster.CruorMock.csproj

COPY tools/Muster.CruorMock/ tools/Muster.CruorMock/
RUN dotnet publish tools/Muster.CruorMock/Muster.CruorMock.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# SQLite DB lives under /data — mount a volume here to persist it across container restarts.
RUN mkdir -p /data
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 \
    CRUOR_DB_PATH=/data/cruor.db
ENTRYPOINT ["dotnet", "Muster.CruorMock.dll"]
