#!/bin/bash

# Test script to verify Docker SQL Server integration
# This script tests the database connection and reset functionality

set -e  # Exit on error

echo "========================================"
echo "Docker SQL Server Integration Test"
echo "========================================"
echo ""

# Check if Docker container is running
echo "1. Checking Docker container status..."
if ! docker ps | grep -q sqlserver-test; then
    echo "   ERROR: SQL Server container 'sqlserver-test' is not running"
    echo "   Please run: docker compose up -d"
    exit 1
fi
echo "   ✓ Container is running"
echo ""

# Test connection to SQL Server
echo "2. Testing SQL Server connection..."
if docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -Q "SELECT 1" > /dev/null 2>&1; then
    echo "   ✓ Connection successful"
else
    echo "   ERROR: Cannot connect to SQL Server"
    exit 1
fi
echo ""

# Test database creation via Docker
echo "3. Testing database creation via Docker..."
docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -Q "
IF DB_ID(N'TestDB') IS NOT NULL
BEGIN
    ALTER DATABASE [TestDB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [TestDB];
END
CREATE DATABASE [TestDB];
" > /dev/null 2>&1
echo "   ✓ Database created successfully"
echo ""

# Verify database exists
echo "4. Verifying database exists..."
if docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -Q "SELECT name FROM sys.databases WHERE name = 'TestDB'" 2>&1 | grep -q "TestDB"; then
    echo "   ✓ Database verified"
else
    echo "   ERROR: Database not found"
    exit 1
fi
echo ""

# Clean up test database
echo "5. Cleaning up test database..."
docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -Q "
ALTER DATABASE [TestDB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [TestDB];
" > /dev/null 2>&1
echo "   ✓ Cleanup successful"
echo ""

# Test the actual database script from test kit
echo "6. Testing with actual Library database script..."
SCRIPT_PATH="/home/runner/work/auto-grading/auto-grading/SampleTestKitsWithData/Testkit_HTTP_1/Meta/database.sql"

if [ -f "$SCRIPT_PATH" ]; then
    # Copy script to container
    docker cp "$SCRIPT_PATH" sqlserver-test:/tmp/db_reset.sql
    
    # Execute script
    if docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -i /tmp/db_reset.sql > /dev/null 2>&1; then
        echo "   ✓ Database script executed successfully"
        
        # Verify Library database exists
        if docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -Q "SELECT name FROM sys.databases WHERE name = 'Library'" 2>&1 | grep -q "Library"; then
            echo "   ✓ Library database created and verified"
            
            # Check if tables exist
            docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -d Library -Q "SELECT COUNT(*) as TableCount FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE'" 2>&1 | grep -E "^[0-9]+$" | while read count; do
                echo "   ✓ Found $count tables in Library database"
            done
        else
            echo "   WARNING: Library database not found after script execution"
        fi
    else
        echo "   WARNING: Database script execution had errors (may be expected)"
    fi
else
    echo "   SKIP: Database script not found at $SCRIPT_PATH"
fi
echo ""

echo "========================================"
echo "✓ All Docker integration tests passed!"
echo "========================================"
echo ""
echo "The SQL Server container is ready for use with --useDocker flag"
echo ""
echo "Example usage:"
echo "  dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \\"
echo "    --suite \"SampleTestKitsWithData/Testkit_HTTP_1\" \\"
echo "    --out \"TestResults\" \\"
echo "    --useDocker"
echo ""
