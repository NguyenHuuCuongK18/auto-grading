# Auto-grading Docker Image
# 
# This image is required for the grading system to work correctly.
# It includes procps package which provides ps, pkill, pgrep commands
# needed for process cleanup between test cases.
#
# BUILD COMMAND:
#   docker build -t fptuxaes/aes-dotnet8-console:latest ./DockerImage
#
# ALTERNATIVE BUILD (if base image not available):
#   1. Edit line 16 below to use: FROM mcr.microsoft.com/dotnet/sdk:8.0
#   2. Then build: docker build -t fptuxaes/aes-dotnet8-console:latest ./DockerImage
#
# The grading system will use this image by default.
# You can also specify a custom image in TestKit/Header.xlsx Config sheet
# by adding a row: CodeImageName | your-image-name:tag
#
# IMPORTANT: If you get "error: failed to fetch metadata" during build,
# it means the base image 'fptuxaes/aes-dotnet8:latest' is not available.
# Replace it with one of these official Microsoft images:
#   - mcr.microsoft.com/dotnet/sdk:8.0 (includes build tools)
#   - mcr.microsoft.com/dotnet/aspnet:8.0 (runtime only, smaller)
#
FROM fptuxaes/aes-dotnet8:latest

# Install procps package for ps/pkill/pgrep commands (needed for process cleanup between test cases)
# Without these commands, the "Address already in use" error occurs when restarting applications
RUN apt-get update && apt-get install -y procps && rm -rf /var/lib/apt/lists/*

# CRITICAL FIX: Disable ALL services from base image that interfere with grading
# The base image (fptuxaes/aes-dotnet8:latest) has NGINX installed (layer 9) and 
# an entrypoint script (layer 19) that starts NGINX as a reverse proxy
RUN if [ -f /scripts/watch-apps.sh ]; then mv /scripts/watch-apps.sh /scripts/watch-apps.sh.disabled; fi
RUN if [ -f /scripts/generate-nginx-config.sh ]; then mv /scripts/generate-nginx-config.sh /scripts/generate-nginx-config.sh.disabled; fi
RUN if [ -f /scripts/entrypoint.sh ]; then mv /scripts/entrypoint.sh /scripts/entrypoint.sh.disabled; fi

# CRITICAL: Completely disable NGINX to prevent proxy interference
# NGINX acts as a reverse proxy and responds on ports even when student code exits
# This causes FALSE POSITIVES where broken student code (e.g., "Hello World" that exits)
# appears to work because NGINX handles the TCP connections
RUN systemctl stop nginx 2>/dev/null || service nginx stop 2>/dev/null || true && \
    systemctl disable nginx 2>/dev/null || update-rc.d nginx disable 2>/dev/null || true && \
    rm -f /etc/nginx/sites-enabled/* && \
    rm -f /etc/nginx/sites-available/* && \
    rm -f /etc/nginx/conf.d/* && \
    if [ -f /etc/nginx/nginx.conf ]; then mv /etc/nginx/nginx.conf /etc/nginx/nginx.conf.disabled; fi

# Create a startup script that aggressively kills NGINX on container start
# This ensures no NGINX processes from base image layers can interfere
RUN echo '#!/bin/bash\n\
# Kill all NGINX processes if any exist\n\
pkill -9 nginx 2>/dev/null || true\n\
ps aux | grep nginx | grep -v grep | awk "{print \$2}" | xargs -r kill -9 2>/dev/null || true\n\
# Verify NGINX is dead\n\
if pgrep -x nginx > /dev/null; then\n\
    echo "ERROR: NGINX still running after kill attempts!"\n\
    exit 1\n\
fi\n\
echo "Container ready - no NGINX processes detected"\n\
exec tail -f /dev/null\n\
' > /startup.sh && chmod +x /startup.sh

WORKDIR /apps

ENV DOTNET_RUNNING_IN_CONTAINER=true

# Keep container alive - startup script ensures ZERO NGINX interference
ENTRYPOINT ["/startup.sh"]