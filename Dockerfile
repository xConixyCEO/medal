# STAGE 1: Build Medal from source
FROM rust:alpine AS medal-builder
WORKDIR /build

# Install build dependencies
RUN apk add --no-cache git build-base && \
    rustup install nightly

# Copy all files (Dockerfile will ignore .NET files)
COPY . .

# Build and strip binary
RUN cargo +nightly build --release --bin medal && \
    strip target/release/medal

# STAGE 2: Build .NET Discord Bot
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build

# Copy all files (Dockerfile will ignore Rust files)
COPY . .

# Restore and publish (only .NET project files will be used)
RUN dotnet restore MoonsecDeobfuscator.csproj && \
    dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app

# STAGE 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Install dependencies
RUN apk add --no-cache curl ca-certificates

# Copy built bot files
COPY --from=bot-builder /app/* ./

# Copy built Medal binary
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# Create startup script
RUN echo '#!/bin/sh' > start.sh && \
    echo './medal serve --port 8080 &' >> start.sh && \
    echo 'sleep 3' >> start.sh && \
    echo 'dotnet MoonsecDeobfuscator.dll' >> start.sh && \
    chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
