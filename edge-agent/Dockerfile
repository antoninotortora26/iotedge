# Stage 1: build & publish
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build

WORKDIR /src

# Copy solution-level files needed for restore
COPY nuget.config ./
COPY netcoreappVersion.props ./
COPY versionInfo.json ./

# Copy only the csproj files first to leverage Docker layer caching on restore
COPY edge-util/src/es6numberserializer/es6numberserializer.csproj                                                         edge-util/src/es6numberserializer/
COPY edge-util/src/jsoncanonicalizer/jsoncanonicalizer.csproj                                                               edge-util/src/jsoncanonicalizer/
COPY edge-util/src/Microsoft.Azure.Devices.Edge.Util/Microsoft.Azure.Devices.Edge.Util.csproj                             edge-util/src/Microsoft.Azure.Devices.Edge.Util/
COPY edge-util/src/Microsoft.Azure.Devices.Edge.Storage/Microsoft.Azure.Devices.Edge.Storage.csproj                       edge-util/src/Microsoft.Azure.Devices.Edge.Storage/
COPY edge-util/src/Microsoft.Azure.Devices.Edge.Storage.RocksDb/Microsoft.Azure.Devices.Edge.Storage.RocksDb.csproj       edge-util/src/Microsoft.Azure.Devices.Edge.Storage.RocksDb/
COPY edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Core/Microsoft.Azure.Devices.Edge.Agent.Core.csproj               edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Core/
COPY edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Docker/Microsoft.Azure.Devices.Edge.Agent.Docker.csproj           edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Docker/
COPY edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Edgelet/Microsoft.Azure.Devices.Edge.Agent.Edgelet.csproj         edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Edgelet/
COPY edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Edgelet.Docker/Microsoft.Azure.Devices.Edge.Agent.Edgelet.Docker.csproj edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Edgelet.Docker/
COPY edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.IoTHub/Microsoft.Azure.Devices.Edge.Agent.IoTHub.csproj           edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.IoTHub/
COPY edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Diagnostics/Microsoft.Azure.Devices.Edge.Agent.Diagnostics.csproj edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Diagnostics/
COPY edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Service/Microsoft.Azure.Devices.Edge.Agent.Service.csproj         edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Service/

# Restore (cached unless .csproj files change)
RUN dotnet restore edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Service/Microsoft.Azure.Devices.Edge.Agent.Service.csproj \
    --locked-mode

# Copy all source
COPY edge-util/src/ edge-util/src/
COPY edge-agent/src/ edge-agent/src/
COPY stylecop.json stylecop.props stylecop.ruleset ./

# Publish without debug symbols to minimize output size
RUN dotnet publish edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Service/Microsoft.Azure.Devices.Edge.Agent.Service.csproj \
    --configuration Release \
    --output /app/publish \
    -p:DebugType=None \
    -p:DebugSymbols=false

# Stage 2: runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime

# RocksDB requires snappy at runtime
RUN apk add --no-cache snappy

# Copy the optimized pre-built librocksdb.so from the local build context
# This is the same file used by the official Microsoft image (7.87 MB vs 12.8 MB from Alpine)
COPY edge-agent/docker/linux/librocksdb/linux-x64/librocksdb.so /usr/local/lib/librocksdb.so
RUN ldconfig /usr/local/lib 2>/dev/null || true

WORKDIR /app

COPY --from=build /app/publish ./

CMD ["/bin/sh", "/app/agentStart.sh"]
