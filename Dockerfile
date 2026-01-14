FROM rust:alpine AS medal-builder
WORKDIR /build
RUN apk add --no-cache git build-base musl-dev && rustup toolchain install nightly && rustup default nightly
COPY . .
RUN cargo build --release --bin medal && strip target/release/medal

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS bot-builder
WORKDIR /build
COPY . .
RUN dotnet publish MoonsecDeobfuscator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app
RUN apk add --no-cache curl ca-certificates lua5.4-libs icu-libs libgcc libstdc++ gcompat
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so

COPY --from=bot-builder /app/ ./
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

RUN printf '#!/bin/sh\n\
./medal serve --port 3000 --luau --lua51 &\n\
while ! nc -z 127.0.0.1 3000; do sleep 1; done\n\
dotnet MoonsecDeobfuscator.dll\n' > start.sh && chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
