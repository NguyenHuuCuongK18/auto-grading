#!/bin/bash
# Test pipe-keeper and input handling

set -e

echo "========================================"
echo "Testing Pipe-Keeper and Input Handling"
echo "========================================"
echo ""

# Clean up
docker rm -f test-unified 2>/dev/null || true

echo "Step 1: Create unified container with pipe-keeper..."
docker run -d --name test-unified fptuxaes/aes-dotnet8-console:latest
sleep 2

echo ""
echo "Step 2: Check pipe-keeper status..."
docker exec test-unified supervisorctl status

echo ""
echo "Step 3: Check if pipe exists and is held open..."
docker exec test-unified ls -la /tmp/client_input
docker exec test-unified lsof /tmp/client_input 2>/dev/null || echo "lsof not available"

echo ""
echo "Step 4: Create a test client script..."
docker exec test-unified bash -c 'cat > /apps/client/test-client.sh << '\''EOF'\''
#!/bin/bash
echo "[Test Client] Started - PID $$"
echo "[Test Client] Reading from stdin..."
counter=0
while IFS= read -r line; do
    counter=$((counter + 1))
    echo "[Test Client] Input #$counter: '\''$line'\''"
    if [ -z "$line" ]; then
        echo "[Test Client] Empty input received - exiting"
        break
    fi
done
echo "[Test Client] Exited after $counter inputs"
EOF
chmod +x /apps/client/test-client.sh'

echo ""
echo "Step 5: Update supervisord to use test client..."
docker exec test-unified bash -c 'sed -i "s|exec dotnet.*|exec /apps/client/test-client.sh < /tmp/client_input|" /etc/supervisor/conf.d/supervisord.conf'
docker exec test-unified supervisorctl reread
docker exec test-unified supervisorctl update

echo ""
echo "Step 6: Start the test client..."
docker exec test-unified supervisorctl start client
sleep 2

echo ""
echo "Step 7: Check if client is still running (should be waiting for input)..."
docker exec test-unified supervisorctl status client

echo ""
echo "Step 8: Send first input..."
docker exec test-unified bash -c 'echo "TEST_INPUT_1" > /tmp/client_input'
sleep 1

echo ""
echo "Step 9: Check client status..."
docker exec test-unified supervisorctl status client

echo ""
echo "Step 10: Send second input..."
docker exec test-unified bash -c 'echo "TEST_INPUT_2" > /tmp/client_input'
sleep 1

echo ""
echo "Step 11: Send empty input (should cause exit)..."
docker exec test-unified bash -c 'echo "" > /tmp/client_input'
sleep 2

echo ""
echo "Step 12: Check final client status..."
docker exec test-unified supervisorctl status client

echo ""
echo "Step 13: View client log..."
docker exec test-unified cat /apps/client/client-stage-0.log

echo ""
echo "Cleanup..."
docker rm -f test-unified

echo ""
echo "Test complete!"
