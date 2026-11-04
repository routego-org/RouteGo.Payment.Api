# Image: routego/mock-gateway. Runs on routego-edge (exposed through Nginx at /mock-gateway/) or locally.
# Build from the repo root:  docker build -f tools/MockGateway/Dockerfile -t routego/mock-gateway .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The root build files make the coding standard apply to this tool as it does to the service.
COPY Directory.Build.props .editorconfig BannedSymbols.txt ./
COPY docs/coding-standards/enforcement/ docs/coding-standards/enforcement/

COPY tools/MockGateway/MockGateway.csproj tools/MockGateway/
RUN dotnet restore tools/MockGateway/MockGateway.csproj -r linux-x64

COPY tools/MockGateway/ tools/MockGateway/
RUN dotnet publish tools/MockGateway/MockGateway.csproj -c Release -r linux-x64 --self-contained false \
    --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MockGateway.dll"]
