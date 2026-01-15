# STAGE 1: Build Shiny (Rust)
FROM rust:alpine AS shiny-builder
WORKDIR /build
RUN apk add --no-cache git build-base musl-dev && \
    rustup toolchain install nightly && \
    rustup default nightly
COPY . .
RUN cargo build --release --bin medal && strip target/release/medal

# STAGE 2: Build Bot (.NET)
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build
COPY . .
# Add Polly for the retry logic and publish
RUN dotnet add package Polly && \
    dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app

# STAGE 3: Final Production Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app
RUN apk add --no-cache curl ca-certificates lua5.4-libs icu-libs icu-data-full libgcc libstdc++ gcompat

# Native Lua support for NLua
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so

# Globalization settings
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV LC_ALL=en_US.UTF-8
ENV LANG=en_US.UTF-8

COPY --from=bot-builder /app/ ./
COPY --from=shiny-builder /build/target/release/medal ./shiny
RUN chmod +x ./shiny

# Startup Script: Starts Shiny on 3000 (Internal) and Bot on 8080 (Health)
RUN printf '#!/bin/sh\n\
./shiny serve --port 3000 --luau &\n\
echo "Waiting for Shiny service on 3000..."\n\
while ! nc -z 127.0.0.1 3000; do sleep 1; done\n\
dotnet MoonsecDeobfuscator.dll\n' > start.sh && chmod +x start.sh

EXPOSE 3000
EXPOSE 8080
CMD ["./start.sh"]
