FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ImmichFamilyMerger/ImmichFamilyMerger.csproj ImmichFamilyMerger/
COPY ImmichFamilyMerger/packages.lock.json ImmichFamilyMerger/
RUN dotnet restore ImmichFamilyMerger/ImmichFamilyMerger.csproj --locked-mode
COPY ImmichFamilyMerger/ ImmichFamilyMerger/
RUN dotnet publish ImmichFamilyMerger/ImmichFamilyMerger.csproj \
    --configuration "$BUILD_CONFIGURATION" \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
LABEL org.opencontainers.image.licenses="GPL-3.0-only"
RUN mkdir -p /data && chown "$APP_UID:$APP_UID" /data
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY --chown=$APP_UID:$APP_UID LICENSE .

USER $APP_UID
VOLUME ["/data"]
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "ImmichFamilyMerger.dll"]
