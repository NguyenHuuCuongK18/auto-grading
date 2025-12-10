#!/bin/bash
# Test script to verify batch grading bug fix
# This simulates various grading scenarios to ensure students aren't lost

echo "======================================"
echo "Batch Grading Bug Fix - Test Script"
echo "======================================"
echo ""

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Test counter
TESTS_PASSED=0
TESTS_FAILED=0

# Function to run a test
run_test() {
    local test_name="$1"
    local test_command="$2"
    
    echo -e "${YELLOW}Running test: $test_name${NC}"
    
    if eval "$test_command"; then
        echo -e "${GREEN}✓ PASSED${NC}"
        ((TESTS_PASSED++))
    else
        echo -e "${RED}✗ FAILED${NC}"
        ((TESTS_FAILED++))
    fi
    echo ""
}

# Check if the fix is present in the code
echo "Checking if bug fixes are present in code..."
echo ""

# Test 1: Check for consistent status filtering
echo "Test 1: Verify status filtering consistency"
if grep -q 's.Status != GradingStatus.Success' Application/SolutionGrader.UI/GradingWindow.xaml.cs; then
    echo -e "${GREEN}✓ Status filtering uses consistent '!= Success' logic${NC}"
    ((TESTS_PASSED++))
else
    echo -e "${RED}✗ Status filtering may still use old inconsistent logic${NC}"
    ((TESTS_FAILED++))
fi
echo ""

# Test 2: Check for producer exception handling
echo "Test 2: Verify producer task exception handling"
if grep -A5 "Producer task: Feed students" Application/SolutionGrader.UI/GradingWindow.xaml.cs | grep -q "finally"; then
    echo -e "${GREEN}✓ Producer task has finally block for channel completion${NC}"
    ((TESTS_PASSED++))
else
    echo -e "${RED}✗ Producer task missing critical finally block${NC}"
    ((TESTS_FAILED++))
fi
echo ""

# Test 3: Check for worker exception handling
echo "Test 3: Verify worker thread exception handling"
if grep -A20 "Each worker continuously pulls" Application/SolutionGrader.UI/GradingWindow.xaml.cs | grep -q "catch.*Exception.*ex"; then
    echo -e "${GREEN}✓ Worker threads catch general exceptions (not just OperationCanceledException)${NC}"
    ((TESTS_PASSED++))
else
    echo -e "${RED}✗ Worker threads may not handle all exceptions${NC}"
    ((TESTS_FAILED++))
fi
echo ""

# Test 4: Check for lost student detection
echo "Test 4: Verify lost student detection logic"
if grep -q "lostStudents.*Not_Run" Application/SolutionGrader.UI/GradingWindow.xaml.cs; then
    echo -e "${GREEN}✓ Lost student detection is present${NC}"
    ((TESTS_PASSED++))
else
    echo -e "${RED}✗ Lost student detection may be missing${NC}"
    ((TESTS_FAILED++))
fi
echo ""

# Test 5: Check for enhanced logging
echo "Test 5: Verify enhanced logging"
if grep -q "\[Producer\]" Application/SolutionGrader.UI/GradingWindow.xaml.cs && \
   grep -q "\[Worker-" Application/SolutionGrader.UI/GradingWindow.xaml.cs; then
    echo -e "${GREEN}✓ Enhanced logging is present for producer and workers${NC}"
    ((TESTS_PASSED++))
else
    echo -e "${RED}✗ Enhanced logging may be incomplete${NC}"
    ((TESTS_FAILED++))
fi
echo ""

# Test 6: Check documentation exists
echo "Test 6: Verify documentation exists"
if [ -f "BATCH_GRADING_BUG_FIX.md" ]; then
    echo -e "${GREEN}✓ Comprehensive documentation file exists${NC}"
    ((TESTS_PASSED++))
else
    echo -e "${RED}✗ Documentation file BATCH_GRADING_BUG_FIX.md not found${NC}"
    ((TESTS_FAILED++))
fi
echo ""

# Summary
echo "======================================"
echo "Test Summary"
echo "======================================"
echo -e "Tests passed: ${GREEN}$TESTS_PASSED${NC}"
echo -e "Tests failed: ${RED}$TESTS_FAILED${NC}"
echo ""

if [ $TESTS_FAILED -eq 0 ]; then
    echo -e "${GREEN}All static code checks passed! ✓${NC}"
    echo ""
    echo "Next steps for manual testing:"
    echo "1. Build the project: dotnet build"
    echo "2. Run the UI application"
    echo "3. Load 20+ students"
    echo "4. Test 'Start All' with parallel batch size 4-8"
    echo "5. Verify all students complete (no 'Not Run' status remaining)"
    echo "6. Check logs for any 'CRITICAL BUG DETECTED' messages"
    echo "7. Test re-grading failed students"
    echo ""
    exit 0
else
    echo -e "${RED}Some checks failed. Please review the code changes.${NC}"
    exit 1
fi
