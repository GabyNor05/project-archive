# Multi-stage build: the SDK image (big) compiles, the runtime image (small) runs.
# Build from the repo root:  docker build -f src/Archive.Api/Dockerfile -t project-archive-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Archive.Core/Archive.Core.csproj src/Archive.Core/
COPY src/Archive.Api/Archive.Api.csproj src/Archive.Api/
COPY src/Archive.Api/packages.lock.json* src/Archive.Api/
RUN dotnet restore src/Archive.Api/Archive.Api.csproj
COPY src/ src/
RUN dotnet publish src/Archive.Api/Archive.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The aspnet image ships a non-root user; never run a web server as root.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Archive.Api.dll"]
