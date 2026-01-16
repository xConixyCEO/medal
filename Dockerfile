# --- STAGE 1: Build Shiny (Medal Fork) ---
# Optimization: Use a pre-warmed image or cache dependencies
FROM rust:alpine AS shiny-builder
WORKDIR /build
RUN apk add --no-cache git build-base musl-dev

# Optimization: Install nightly once. 
# Better yet: If you can use stable, it saves minutes.
RUN rustup toolchain install nightly && rustup default nightly

# Optimization: Copy only dependency files first to cache the 'cargo fetch' layer
COPY Cargo.toml Cargo.lock ./
# Note: This requires a dummy main.rs to work perfectly, 
# but for now, we'll keep it simple by copying the source.
COPY . .
RUN cargo build --release --bin medal && strip target/release/medal

# --- STAGE 2: Build .NET Bot ---
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build

# Optimization: Restore packages separately to cache them
COPY MoonsecDeobfuscator.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app --no-restore

# --- STAGE 3: Final Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Combine RUN commands to reduce layers
RUN apk add --no-cache \
    curl \
    ca-certificates \
    lua5.4-libs \
    icu-libs \
    icu-data-full \
    libgcc \
    libstdc++ \
    gcompat && \
    ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so

COPY --from=bot-builder /app/ ./
COPY --from=shiny-builder /build/target/release/medal ./shiny
RUN chmod +x ./shiny

ENV PORT1=8080

# Startup script
RUN printf '#!/bin/sh\n\
./shiny serve --port 3000 --luau &\n\
while ! nc -z 127.0.0.1 3000; do sleep 1; done\n\
dotnet MoonsecDeobfuscator.dll\n' > start.sh && chmod +x start.sh

EXPOSE 3000
EXPOSE 8080

CMD ["./start.sh"]
