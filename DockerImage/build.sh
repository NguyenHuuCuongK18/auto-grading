#!/bin/bash
# Build script for auto-grading Docker images

set -e

echo "Building auto-grading Docker images..."
echo ""

# Build main student code container
echo "1. Building student code container (fptuxaes/aes-dotnet8-console:latest)..."
docker build -t fptuxaes/aes-dotnet8-console:latest -f Dockerfile .
echo "   ✓ Student code container built successfully"
echo ""

# Build network monitor container
echo "2. Building network monitor container (fptuxaes/network-monitor:latest)..."
docker build -t fptuxaes/network-monitor:latest -f NetworkMonitor.Dockerfile .
echo "   ✓ Network monitor container built successfully"
echo ""

echo "All images built successfully!"
echo ""
echo "Available images:"
docker images | grep -E "(fptuxaes/aes-dotnet8-console|fptuxaes/network-monitor)"
