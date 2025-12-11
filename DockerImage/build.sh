#!/bin/bash
# Build script for auto-grading Docker images
#
# This script builds:
# 1. Unified student code container (runs client/server code)
# 2. Network monitor sidecar (SharpPcap-based, matches MiddlewareSniffPort)

set -e

# Change to repository root (script may be called from any directory)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
cd "$REPO_ROOT"

echo "Building auto-grading Docker images..."
echo "Repository root: $REPO_ROOT"
echo ""

# Build unified student code container
echo "1. Building unified student code container (fptuxaes/aes-dotnet8-console:latest)..."
docker build -t fptuxaes/aes-dotnet8-console:latest -f DockerImage/Dockerfile.unified .
echo "   ✓ Unified student code container built successfully"
echo ""

# Build network monitor container (SharpPcap-based sidecar)
echo "2. Building SharpPcap-based network monitor container (fptuxaes/network-monitor:latest)..."
echo "   This uses SharpPcap/PacketDotNet for real-time capture, matching MiddlewareSniffPort."
docker build -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile .
echo "   ✓ Network monitor container built successfully"
echo ""

echo "All images built successfully!"
echo ""
echo "Available images:"
docker images | grep -E "(fptuxaes/aes-dotnet8-console|fptuxaes/network-monitor)"

