# STAGE 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

# Install dependencies for NLua
RUN apk add --no-cache curl ca-certificates lua5.4 lua5.4-dev icu-libs

# Create symlink in a location NLua actually checks
RUN ln -sf /usr/lib/liblua5.4.so /app/liblua54.so && \
    ln -sf /usr/lib/liblua5.4.so /app/lua54.so

# Enable globalization support
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# Copy bot files
COPY --from=bot-builder /app/* ./

# Copy Medal binary
COPY --from=medal-builder /build/target/release/medal ./medal
RUN chmod +x ./medal

# Create startup script
RUN printf '#!/bin/sh\n./medal serve --port 8080 &\nsleep 3\ndotnet MoonsecDeobfuscator.dll\n' > start.sh && \
    chmod +x start.sh

EXPOSE 3000
CMD ["./start.sh"]
