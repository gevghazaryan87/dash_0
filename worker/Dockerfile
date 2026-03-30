# Stage 1: Build the application
FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:7.0 AS build
ARG TARGETPLATFORM
ARG TARGETARCH
ARG BUILDPLATFORM

RUN echo "I am running on $BUILDPLATFORM, building for $TARGETPLATFORM"

WORKDIR /source
COPY *.csproj .
RUN dotnet restore -a $TARGETARCH

COPY . .
RUN dotnet publish -c release -o /app -a $TARGETARCH --self-contained false --no-restore

# Stage 2: Create the final runtime image
FROM mcr.microsoft.com/dotnet/runtime:7.0
WORKDIR /app

# Copy the compiled program from the build stage
COPY --from=build /app .

# --- OPENTELEMETRY AUTO-INSTRUMENTATION SETUP ---
# We need root temporarily to install the OTel agent binaries
USER root

# Install dependencies: curl and unzip are required for the OTel installer script
RUN apt-get update && apt-get install -y curl unzip && rm -rf /var/lib/apt/lists/*

# 1. Create the directory for the OTel agent (matching your docker-compose path)
# 2. Download and run the official OTel .NET auto-instrumentation installer
# 3. Clean up the installer script after execution
RUN mkdir /otel-dotnet-auto && \
    curl -sSfL https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/latest/download/otel-dotnet-auto-install.sh -O && \
    OTEL_DOTNET_AUTO_HOME=/otel-dotnet-auto sh ./otel-dotnet-auto-install.sh && \
    rm ./otel-dotnet-auto-install.sh

# The environment variables in your docker-compose.yml will now 
# find the required .dll and profiler files in /otel-dotnet-auto
# -----------------------------------------------

ENTRYPOINT ["dotnet", "Worker.dll"]