# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first so the layer is cached until a project or package version changes.
# .editorconfig carries analyzer settings; without it warnings-as-errors fails the publish.
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY src/Gamex.UserLibrary.Domain/*.csproj src/Gamex.UserLibrary.Domain/
COPY src/Gamex.UserLibrary.Application/*.csproj src/Gamex.UserLibrary.Application/
COPY src/Gamex.UserLibrary.Infrastructure/*.csproj src/Gamex.UserLibrary.Infrastructure/
COPY src/Gamex.UserLibrary.Api/*.csproj src/Gamex.UserLibrary.Api/
RUN dotnet restore src/Gamex.UserLibrary.Api/Gamex.UserLibrary.Api.csproj

COPY src/ src/
RUN dotnet publish src/Gamex.UserLibrary.Api/Gamex.UserLibrary.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gamex.UserLibrary.Api.dll"]
