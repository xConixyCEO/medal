# STAGE 1: Must be named 'medal-builder'
FROM rust:alpine AS medal-builder
WORKDIR /build
RUN apk add --no-cache git build-base musl-dev && \
    rustup toolchain install nightly && \
    rustup default nightly
COPY . .
RUN cargo build --release --bin medal && strip target/release/medal

# STAGE 2: Must be named 'bot-builder'
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build
COPY . .
RUN dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app --verbosity quiet

# STAGE 3: Final Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Install native dependencies
RUN apk add --no-cache \
    curl \
    ca-certificates \
    lua5.4-libs \
    icu-libs \
    libgcc \
    libstdc++

# Fix the Lua symlinks so NLua can find the library
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# These lines were failing because they couldn't find the stages above
COPY --from=bot-builder /app/ ./
COPY --from=medal-builder /build/target/release/medal ./medal

RUN chmod +x ./medal && \
    printf '#!/bin/sh\n./medal serve --port 8080 &\nsleep 3\ndotnet MoonsecDeobfuscator.dll\n' > start.sh && \
    chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
