# STAGE 1: Build Medal from source
FROM rust:alpine AS medal-builder
WORKDIR /build

# Install build dependencies
RUN apk add --no-cache git build-base && \
    rustup install nightly

# Copy Medal source files
COPY src/ ./src/
COPY Cargo.toml ./

# Build and strip binary for smaller size
RUN cargo +nightly build --release --bin medal && \
    strip target/release/medal

# STAGE 2: Build .NET Discord Bot
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build

# Copy bot project files (root level)
COPY MoonsecDeobfuscator.csproj ./
COPY Program.cs ./
COPY Deobfuscation/ ./Deobfuscation/

# Restore and publish
RUN dotnet restore MoonsecDeobfuscator.csproj && \
    dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app

# STAGE 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Install runtime dependencies
RUN apk add --no-cache curl ca-certificates

# Copy bot files
COPY --from=bot-builder /app/* ./

# Copy Medal binary
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# Create startup script to run both services
RUN echo '#!/bin/sh' > start.sh && \
    echo './medal serve --port 8080 &' >> start.sh && \
    echo 'sleep 3' >> start.sh && \
    echo 'dotnet MoonsecDeobfuscator.dll' >> start.sh && \
    chmod +x start.sh

EXPOSE 3000

CMD ["./start.sh"]
