# STAGE 1: Build Medal (Rust Service)
FROM rust:alpine AS medal-builder
WORKDIR /build

# Install build dependencies for Rust on Alpine
RUN apk add --no-cache git build-base musl-dev && \
    rustup toolchain install nightly && \
    rustup default nightly

# Copy source and build the binary
COPY . .
ENV RUSTFLAGS="-A warnings"
RUN cargo build --release --bin medal && \
    strip target/release/medal

# STAGE 2: Build .NET Discord Bot
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build

# Copy source and publish
COPY . .
RUN dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app --verbosity quiet

# STAGE 3: Final Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# 1. Install system dependencies
# libgcc and libstdc++ are required for the Rust binary and Lua library.
RUN apk add --no-cache \
    curl \
    ca-certificates \
    lua5.4-libs \
    icu-libs \
    libgcc \
    libstdc++

# 2. FIX LUA LOADING ERROR:
# Alpine installs 'liblua.so.5.4'. NLua looks for 'liblua54.so'.
# Creating these symlinks maps the filenames correctly.
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /usr/lib/lua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so

# Ensure globalization works on Alpine
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# 3. Copy files from previous stages
# Copy the published .NET application
COPY --from=bot-builder /app/ ./

# Copy the Rust Medal binary and ensure it's executable
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# 4. FIX "NOT FOUND" ERROR:
# The start script MUST include the --luau and --lua51 flags.
# Without these flags, the Medal service will not register the decompile routes.
RUN printf '#!/bin/sh\n\
./medal serve --port 8080 --luau --lua51 &\n\
sleep 5\n\
dotnet MoonsecDeobfuscator.dll\n' > start.sh && \
    chmod +x start.sh

# Render usually expects port 3000 for web services
EXPOSE 3000

CMD ["./start.sh"]
