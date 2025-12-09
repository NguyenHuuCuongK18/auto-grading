#!/bin/bash
# Test grading script for unified container architecture

set -e

echo "============================================"
echo "Unified Container Architecture - Test Grading"
echo "============================================"
echo ""

# Configuration
SUBMIT_DIR="/home/runner/work/auto-grading/auto-grading/batchstudent"
TESTKIT_DIR="/home/runner/work/auto-grading/auto-grading/Testkit_Q1_PRN222"
OUTPUT_DIR="/home/runner/work/auto-grading/auto-grading/batchstudent/TestResults"
CLI_PATH="/home/runner/work/auto-grading/auto-grading/Application/SolutionGrader.Cli/bin/Release/net8.0/SolutionGrader.Cli.dll"

# Create output directory
mkdir -p "$OUTPUT_DIR"

echo "Configuration:"
echo "  Submit folder: $SUBMIT_DIR"
echo "  Test kit:      $TESTKIT_DIR"
echo "  Output:        $OUTPUT_DIR"
echo ""

# List students
echo "Discovering students..."
dotnet "$CLI_PATH" list \
    --submit "$SUBMIT_DIR" \
    --testkit "$TESTKIT_DIR"

echo ""
echo "============================================"
echo "Starting Docker-based grading..."
echo "============================================"
echo ""

# Grade ONE student first as a test
echo "Grading first student (AnhDThe187386)..."
dotnet "$CLI_PATH" dockergrade \
    --submit "$SUBMIT_DIR" \
    --testkit "$TESTKIT_DIR" \
    --out "$OUTPUT_DIR" \
    --network "auto-grading-network" \
    --batch-size 1 \
    --timeout 300 \
    --tc-timeout 30

echo ""
echo "============================================"
echo "Grading completed!"
echo "============================================"
echo ""

# Show results
echo "Results location: $OUTPUT_DIR"
echo ""
echo "Checking for result files..."
find "$OUTPUT_DIR" -name "*.xlsx" -o -name "*.log" -o -name "*.pcap" | head -20

echo ""
echo "Checking Docker containers (should be cleaned up)..."
docker ps -a | grep -E "ag-unified|ag-monitor|ag-" || echo "No auto-grading containers found (cleanup successful)"

echo ""
echo "Test grading complete!"
