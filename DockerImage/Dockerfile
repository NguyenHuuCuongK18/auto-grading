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

# Disable the watch scripts from base image that interfere with grading
RUN if [ -f /scripts/watch-apps.sh ]; then mv /scripts/watch-apps.sh /scripts/watch-apps.sh.disabled; fi
RUN if [ -f /scripts/generate-nginx-config.sh ]; then mv /scripts/generate-nginx-config.sh /scripts/generate-nginx-config.sh.disabled; fi
RUN if [ -f /scripts/entrypoint.sh ]; then mv /scripts/entrypoint.sh /scripts/entrypoint.sh.disabled; fi

WORKDIR /apps

ENV DOTNET_RUNNING_IN_CONTAINER=true

# Keep container alive - grading system will exec into it to run student code
ENTRYPOINT ["tail", "-f", "/dev/null"]