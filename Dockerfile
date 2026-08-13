# Container for MCP-directory liveness checks (e.g. Glama): it starts the server and answers
# introspection (initialize + tools/list) over stdio.
#
# IMPORTANT: Super BI MCP's Power BI features are Windows-only - they drive the local Analysis
# Services / Power BI Desktop engine, which is not present in a Linux container. This image exists
# so directories can verify the server is alive and enumerate its tools; for real use, run the
# native Windows binary (see the README - `npx github:cyphonica/powerbi-pbix-mcp` or the release).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SuperBiMcp.csproj -c Release -r linux-x64 --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
# stdout is the JSON-RPC channel; the server speaks MCP over stdin/stdout.
ENTRYPOINT ["dotnet", "SuperBiMcp.dll"]
