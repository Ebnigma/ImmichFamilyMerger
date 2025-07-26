# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY [".", "ImmichFamilyMerger/"]
RUN dotnet restore "./ImmichFamilyMerger/ImmichFamilyMerger.csproj"
WORKDIR "/src/ImmichFamilyMerger"
RUN dotnet build "./ImmichFamilyMerger.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ImmichFamilyMerger.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Example: --userApiKeys=user1:apikey1,user2:apikey2 --appApiKey=appkey --baseUrl=https://example.com --albumId=albumid
ENTRYPOINT dotnet ImmichFamilyMerger.dll --userApiKeys="${USER_API_KEYS}" --appApiKey="${APP_API_KEY}" --baseUrl="${BASE_URL}" --albumId="${ALBUM_ID}" --sleepTime="${SLEEP_TIME}"