# --- STAGE 3: Final Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# 1. Install ICU libraries and full globalization support
RUN apk add --no-cache \
    curl ca-certificates lua5.4-libs icu-libs icu-data-full libgcc libstdc++ gcompat

# 2. Disable Invariant Mode so .NET can use en-US
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV LC_ALL=en_US.UTF-8
ENV LANG=en_US.UTF-8

# Native Library Symlink for NLua
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so

COPY --from=bot-builder /app/ ./
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# STARTUP: Medal on 3000, Bot on 8080
RUN printf '#!/bin/sh\n\
./medal serve --port 3000 --luau --lua51 &\n\
echo "Waiting for Medal service on 3000..."\n\
while ! nc -z 127.0.0.1 3000; do sleep 1; done\n\
dotnet MoonsecDeobfuscator.dll\n' > start.sh && chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
