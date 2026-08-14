FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /source

COPY src/FileSentry.Api/FileSentry.Api.csproj src/FileSentry.Api/
RUN dotnet restore src/FileSentry.Api/FileSentry.Api.csproj

COPY src/FileSentry.Api/ src/FileSentry.Api/
RUN dotnet publish src/FileSentry.Api/FileSentry.Api.csproj \
    --configuration $BUILD_CONFIGURATION \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p \
        /var/lib/filesentry/temp \
        /var/lib/filesentry/quarantine \
        /var/lib/filesentry/clean \
    && chown -R $APP_UID:$APP_UID /app /var/lib/filesentry \
    && chmod 700 \
        /var/lib/filesentry \
        /var/lib/filesentry/temp \
        /var/lib/filesentry/quarantine \
        /var/lib/filesentry/clean

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=5 \
    CMD ["curl", "--fail", "--silent", "--show-error", "--max-time", "4", "http://127.0.0.1:8080/health/live"]

ENTRYPOINT ["dotnet", "FileSentry.Api.dll"]
