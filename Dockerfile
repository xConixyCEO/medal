# STAGE 1: Build Medal from source
FROM rust:alpine AS medal-builder
WORKDIR /build

# Install build dependencies
RUN apk add --no-cache git build-base musl-dev && \
    rustup toolchain install nightly && \
    rustup default nightly

# Copy all files
COPY . .

# Build with all warnings suppressed
ENV RUSTFLAGS="-A warnings"
RUN cargo build --release --bin medal && \
    strip target/release/medal

# STAGE 2: Build .NET Discord Bot
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build

# Copy all files
COPY . .

# Publish .NET app
RUN dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app --verbosity quiet

# STAGE 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# 1. Install NLua/Medal dependencies
# We add libgcc and libstdc++ because the Rust binary and Lua need them on Alpine
RUN apk add --no-cache \
    curl \
    ca-certificates \
    lua5.4-libs \
    icu-libs \
    libgcc \
    libstdc++

# 2. FIX THE LUA ERROR: 
# Alpine's 'lua5.4-libs' puts the file at /usr/lib/liblua.so.5.4
# .NET looks for 'liblua54.so' or 'lua54.so'. We create symlinks for both.
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /usr/lib/lua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# Copy bot files
COPY --from=bot-builder /app/ ./

# Copy Medal binary from Stage 1
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# Create startup script
RUN printf '#!/bin/sh\n./medal serve --port 8080 &\nsleep 3\ndotnet MoonsecDeobfuscator.dll\n' > start.sh && \
    chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
