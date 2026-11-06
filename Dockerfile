# Image: routego/payment. Runs on the routego-secure EC2 instance via docker compose (5006:8080).
# Build from the repo root:  docker build -t routego/payment .

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Root build files first: the coding-standard analyzers and .editorconfig run during the build,
# and BannedSymbols.txt comes from the docs/coding-standards submodule.
COPY Directory.Build.props .editorconfig BannedSymbols.txt ./
# (Removed: COPY docs/coding-standards/enforcement/ — that submodule is not in this repo, so the line broke
#  every build. Directory.Build.props only references it when it exists.)

# Restore on the csproj alone so the package layer is cached until dependencies change.
COPY RouteGo.Payment.Api/RouteGo.Payment.Api.csproj RouteGo.Payment.Api/
RUN dotnet restore RouteGo.Payment.Api/RouteGo.Payment.Api.csproj -r linux-x64

COPY RouteGo.Payment.Api/ RouteGo.Payment.Api/
# -r linux-x64 matches the restore; framework-dependent, so the aspnet image supplies the runtime.
RUN dotnet publish RouteGo.Payment.Api/RouteGo.Payment.Api.csproj -c Release -r linux-x64 --self-contained false \
    --no-restore -o /app/publish

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The aspnet image already listens on 8080 and runs as the non-root 'app' user.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RouteGo.Payment.Api.dll"]
