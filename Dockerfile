# STAGE 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# 1. Install Alpine-specific native dependencies
# We include libgcc and libstdc++ because native Lua and your Rust 'medal' binary
# require these to function on Alpine's musl-based system.
RUN apk add --no-cache \
    curl \
    ca-certificates \
    lua5.4-libs \
    icu-libs \
    libgcc \
    libstdc++

# 2. Map Alpine's library name to the name NLua expects
# This creates a 'shortcut' so when .NET asks for 'liblua54.so', 
# it points to the actual file Alpine installed.
RUN ln -sf /usr/lib/liblua.so.5.4 /usr/lib/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /app/liblua54.so && \
    ln -sf /usr/lib/liblua.so.5.4 /usr/lib/lua54.so

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# Copy your published bot files
COPY --from=bot-builder /app/ ./

# Copy and set permissions for the Medal binary
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# Create startup script
RUN printf '#!/bin/sh\n./medal serve --port 8080 &\nsleep 3\ndotnet MoonsecDeobfuscator.dll\n' > start.sh && \
    chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
