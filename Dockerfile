# --- STAGE 1: Build Medal (Rust) ---
FROM rust:alpine AS medal-builder
WORKDIR /build
RUN apk add --no-cache git build-base musl-dev && \
    rustup toolchain install nightly && \
    rustup default nightly
COPY . .
RUN cargo build --release --bin medal && strip target/release/medal

# --- STAGE 2: Build Bot (.NET) ---
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build
COPY . .
RUN dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app --verbosity quiet

# --- STAGE 3: Final Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Install native dependencies for Lua and Medal
RUN apk add --no-cache \
    curl \
    ca-certificates \
    lua5.4-libs \
    icu-libs \
    libgcc \
    libstdc++ \
    gcompat

# FIX: Link Alpine's Lua library to the name NLua expects
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /usr/lib/lua54.so

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

COPY --from=bot-builder /app/ ./
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# STARTUP: Run Medal on 8080, wait for it to be ready, then start Bot on 3000
RUN printf '#!/bin/sh\n\
./medal serve --port 8080 --luau --lua51 &\n\
echo "Waiting for internal Medal service..."\n\
while ! nc -z 127.0.0.1 8080; do\n\
  sleep 1\n\
done\n\
dotnet MoonsecDeobfuscator.dll\n' > start.sh && \
    chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
