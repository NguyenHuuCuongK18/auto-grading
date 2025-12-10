#!/bin/bash
# Test script to verify the "Start All/Start Selected" fix

set -e

echo "=========================================="
echo "Testing: Start All/Start Selected Fix"
echo "=========================================="
echo ""

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Check if solution builds
echo "Step 1: Building solution..."
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR" || { echo "Failed to change to script directory"; exit 1; }

if dotnet build SolutionGrader.sln -c Release > /tmp/build.log 2>&1; then
    echo -e "${GREEN}✓ Build successful${NC}"
else
    echo -e "${RED}✗ Build failed${NC}"
    echo "See /tmp/build.log for details"
    exit 1
fi

# Check for the fix in the code
echo ""
echo "Step 2: Verifying fix is present..."
GRADING_SERVICE_FILE="Application/SolutionGrader.UI/Services/GradingOrchestrationService.cs"

if grep -q "Grade ALL students passed to this service" "$GRADING_SERVICE_FILE"; then
    echo -e "${GREEN}✓ Fix comment found${NC}"
else
    echo -e "${RED}✗ Fix comment not found${NC}"
    exit 1
fi

if grep -q "foreach (var student in students)" "$GRADING_SERVICE_FILE"; then
    echo -e "${GREEN}✓ Correct loop found (iterating over all students)${NC}"
else
    echo -e "${RED}✗ Incorrect loop (still filtering students?)${NC}"
    exit 1
fi

# Check that the buggy filter was removed (ignore commented lines)
# Pattern: Look for the specific problematic code pattern (not commented)
if grep -n "var studentsToGrade.*=.*students\.Where.*Status.*==" "$GRADING_SERVICE_FILE" | grep -v "//" | grep -q "var studentsToGrade"; then
    echo -e "${RED}✗ Buggy filter still present!${NC}"
    exit 1
else
    echo -e "${GREEN}✓ Buggy filter removed (or commented out)${NC}"
fi

echo ""
echo "Step 3: Code analysis..."

# Verify the correct loop is present
if grep -q "foreach (var student in students)" "$GRADING_SERVICE_FILE"; then
    echo -e "${GREEN}✓ Correct grading loop confirmed (iterates over all students)${NC}"
else
    echo -e "${RED}✗ Incorrect grading loop${NC}"
    exit 1
fi

echo ""
echo "Step 4: Checking diagnostic logging..."
if grep -q "Total students to grade:" "$GRADING_SERVICE_FILE"; then
    echo -e "${GREEN}✓ Diagnostic logging present${NC}"
else
    echo -e "${YELLOW}⚠ Warning: Diagnostic logging may be missing${NC}"
fi

echo ""
echo "=========================================="
echo -e "${GREEN}All automated checks passed!${NC}"
echo "=========================================="
echo ""
echo "Manual Testing Required:"
echo "1. Run SolutionGrader.UI application"
echo "2. Load students and click 'Start All'"
echo "3. Verify all students are graded (none remain 'Not Run')"
echo "4. Check logs for 'Total students to grade: X'"
echo "5. Verify NO 'SKIPPING students' warnings appear"
echo ""
echo "See FIX_VERIFICATION_GUIDE.md for detailed test scenarios"
echo ""
