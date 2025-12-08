#!/bin/bash
# Test single student grading with unified container

set -e

echo "=========================================="
echo "Testing Unified Container Grading"
echo "=========================================="
echo ""

# Create Docker network if it doesn't exist
docker network create auto-grading-network 2>/dev/null || echo "Network already exists"

# Clean up any existing containers
echo "Cleaning up existing containers..."
docker rm -f $(docker ps -a -q --filter "name=ag-") 2>/dev/null || echo "No containers to clean up"

echo ""
echo "Running grading for ONE student..."
echo ""

cd /home/runner/work/auto-grading/auto-grading

# Grade a single student
dotnet Application/SolutionGrader.Cli/bin/Release/net8.0/SolutionGrader.Cli.dll dockergrade \
    --submit batchstudent \
    --testkit Testkit_Q1_PRN222 \
    --out batchstudent/TestResults \
    --paper 1 \
    --student AnhDThe187386 \
    --timeout 180 \
    --tc-timeout 30 \
    --parallel 1

echo ""
echo "=========================================="
echo "Grading Complete!"
echo "=========================================="
echo ""

# Show containers (should be cleaned up)
echo "Checking containers..."
docker ps -a | grep -E "ag-" || echo "✓ All containers cleaned up"

echo ""
echo "Checking results..."
find batchstudent/TestResults -name "*.xlsx" -o -name "*.pcap" -o -name "*-stage-*.log" | head -30

echo ""
echo "Done!"
