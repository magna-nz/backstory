# Multi-stage build for the Backstory MCP server.
#
# Glama.ai uses this image to build, start, and inspect the server. The MCP server
# speaks JSON-RPC over stdio, so there is no port to expose. Run it with -i.
#
# Build:  docker build -t backstory .
# Run:    docker run -i --rm backstory     # starts `backstory serve`

# ---- Build stage --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Backstory.Cli/Backstory.Cli.csproj -c Release -o /app

# ---- Runtime stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

# MCP is stdio-based, so the server runs the `serve` subcommand. No port to expose.
ENTRYPOINT ["dotnet", "Backstory.Cli.dll", "serve"]
