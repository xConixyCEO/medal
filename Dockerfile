# STAGE 1: Build Medal from source
FROM rust:alpine AS medal-builder
WORKDIR /build

# Install build dependencies
RUN apk add --no-cache git build-base && \
    rustup install nightly

# Copy all files
COPY . .

# Build with all warnings suppressed
ENV RUSTFLAGS="-A warnings"
RUN cargo +nightly build --release --bin medal && \
    strip target/release/medal

# STAGE 2: Build .NET Discord Bot
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build

# Copy all files
COPY . .

# The .csproj is in repository root
WORKDIR /build

# Publish .NET app
RUN dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app --verbosity quiet

# STAGE 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Install NLua dependencies
RUN apk add --no-cache curl ca-certificates lua5.4 lua5.4-dev icu-libs && \
    ln -sf /usr/lib/liblua5.4.so /app/liblua54.so && \
    ln -sf /usr/lib/liblua5.4.so /app/lua54.so

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# Copy bot files
COPY --from=bot-builder /app/* ./

# Copy Medal binary
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# Create startup script
RUN printf '#!/bin/sh\n./medal serve --port 8080 &\nsleep 3\ndotnet MoonsecDeobfuscator.dll\n' > start.sh && \
    chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
