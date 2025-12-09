# Quick Fix Guide: "no such file or directory" Error

## The Error

```
exec /scripts/unified-entrypoint.sh: no such file or directory
```

## The Cause

**Windows line endings (CRLF) in shell scripts.**

## The Fix

```bash
# Rebuild the Docker image - that's it!
cd DockerImage
bash build.sh
```

The Dockerfile now includes `dos2unix` which automatically converts CRLF → LF.

## Why This Works

The rebuilt image will have scripts with correct Unix line endings (LF only), allowing Linux to find the shebang interpreter.

## If You're Still Having Issues

1. **Verify the rebuild worked:**
   ```bash
   docker images | grep fptuxaes/aes-dotnet8-console
   # Check the "CREATED" time - should be recent
   ```

2. **Force rebuild without cache:**
   ```bash
   docker rmi fptuxaes/aes-dotnet8-console:latest
   cd DockerImage
   docker build --no-cache -t fptuxaes/aes-dotnet8-console:latest -f Dockerfile.unified .
   ```

3. **Test the container:**
   ```bash
   docker run -d --name test fptuxaes/aes-dotnet8-console:latest
   docker logs test
   # Should show: [Entrypoint] Phase 1: Creating the Named Pipe...
   docker rm -f test
   ```

## For More Details

- Full explanation: [LINE_ENDINGS_FIX.md](LINE_ENDINGS_FIX.md)
- Troubleshooting guide: [DOCKER_BUILD_TROUBLESHOOTING.md](DOCKER_BUILD_TROUBLESHOOTING.md)
- Build instructions: [DockerImage/BUILD_INSTRUCTIONS.md](DockerImage/BUILD_INSTRUCTIONS.md)
