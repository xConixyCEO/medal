# STAGE 1: Build Medal from source
FROM rust:alpine AS medal-builder
WORKDIR /build

# Install build dependencies
RUN apk add --no-cache git build-base && \
    rustup install nightly

# Copy everything (Rust will ignore .NET files via .dockerignore)
COPY . .

# Build with all warnings suppressed
ENV RUSTFLAGS="-A warnings"
RUN cargo +nightly build --release --bin medal && \
    strip target/release/medal

# STAGE 2: Build .NET Discord Bot
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build

# Copy everything (Docker will ignore Rust files via .dockerignore)
COPY . .

# Change to src/ directory where the project file is
WORKDIR /build/src

# Suppress all .NET warnings and publish
RUN dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app --verbosity quiet

# STAGE 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Install Medal and NLua dependencies
RUN apk add --no-cache curl ca-certificates lua5.4 lua5.4-dev icu-libs && \
    ln -sf /usr/lib/liblua5.4.so /usr/lib/liblua54.so

# Enable globalization support (fixes CultureNotFoundException)
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# Copy bot files from builder
COPY --from=bot-builder /app/* ./

# Copy Medal binary from builder
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# Verify both binaries exist
RUN if [ ! -f ./medal ]; then echo "ERROR: Medal binary not found"; exit 1; fi && \
    if [ ! -f ./MoonsecDeobfuscator.dll ]; then echo "ERROR: Bot DLL not found"; exit 1; fi

# Create startup script to run both services
RUN printf '#!/bin/sh\n./medal serve --port 8080 &\nsleep 3\ndotnet MoonsecDeobfuscator.dll\n' > start.sh && \
    chmod +x start.sh

EXPOSE 3000

CMD ["./start.sh"]
