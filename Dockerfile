# STAGE 1: Build Medal from source
FROM rust:alpine AS medal-builder
WORKDIR /build

# Install build dependencies
RUN apk add --no-cache git build-base && \
    rustup install nightly

# Copy Medal source files
COPY src/ ./src/
COPY Cargo.toml ./

# Build and strip binary
RUN cargo +nightly build --release --bin medal && \
    strip target/release/medal

# STAGE 2: Build .NET Discord Bot
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build

# Copy bot project files
COPY bot/ ./bot/
COPY MoonsecDeobfuscator.csproj ./
COPY Program.cs ./
COPY Deobfuscation/ ./Deobfuscation/
COPY Bytecode/ ./Bytecode/

# Restore and publish
RUN dotnet restore MoonsecDeobfuscator.csproj && \
    dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app

# STAGE 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Install dependencies for Medal
RUN apk add --no-cache curl lua5.4 lua5.4-dev icu-libs && \
    ln -sf /usr/lib/liblua5.4.so /usr/lib/liblua54.so

# Enable globalization support
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# Copy Medal binary
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# Copy bot files
COPY --from=bot-builder /app/* ./

# Verify Medal exists
RUN if [ ! -f ./medal ]; then echo "ERROR: Medal binary not found"; exit 1; fi

# Expose health check port
EXPOSE 3000

CMD ["dotnet", "MoonsecDeobfuscator.dll"]
