# --- STAGE 1: Build Shiny (Medal Fork) ---
FROM rust:alpine AS shiny-builder
WORKDIR /build
RUN apk add --no-cache git build-base musl-dev && \
    rustup toolchain install nightly && \
    rustup default nightly
COPY . .
RUN cargo build --release --bin medal && strip target/release/medal

# --- STAGE 2: Build .NET Bot ---
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build
COPY . .
RUN dotnet add package Polly && \
    dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app

# --- STAGE 3: Final Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app
RUN apk add --no-cache curl ca-certificates lua5.4-libs icu-libs icu-data-full libgcc libstdc++ gcompat

# Native Lua Link
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so

COPY --from=bot-builder /app/ ./
COPY --from=shiny-builder /build/target/release/medal ./shiny
RUN chmod +x ./shiny

# Your custom environment variable naming
ENV PORT1=8080

# Startup: Shiny on 3000, Bot on PORT1 (8080)
RUN printf '#!/bin/sh\n\
./shiny serve --port 3000 --luau &\n\
echo "Waiting for Shiny on port 3000..."\n\
while ! nc -z 127.0.0.1 3000; do sleep 1; done\n\
echo "Starting Bot on PORT1: $PORT1"\n\
dotnet MoonsecDeobfuscator.dll\n' > start.sh && chmod +x start.sh

# Expose the internal and external ports
EXPOSE 3000
EXPOSE 8080

CMD ["./start.sh"]
