# 🎯 ACTION REQUIRED: Fix the "no such file or directory" Error

## What You Need to Do

**Just rebuild your Docker image:**

```bash
cd DockerImage
bash build.sh
```

**That's it!** The script will automatically fix the line ending issue.

## What Happened

Your shell scripts have Windows line endings (CRLF - `\r\n`) instead of Unix line endings (LF - `\n`). This causes Linux to fail when trying to execute them.

## What We Fixed

We updated the Dockerfile to automatically convert CRLF → LF using the `dos2unix` tool. When you rebuild the image, all shell scripts will have the correct line endings.

## Verify the Fix

After rebuilding, test it:

```bash
docker run -d --name test-unified fptuxaes/aes-dotnet8-console:latest
docker logs test-unified
```

You should see:
```
[Entrypoint] Phase 1: Creating the Named Pipe...
[Entrypoint] Phase 2: Handing over to Supervisord...
... supervisord started with pid 1
```

Clean up:
```bash
docker rm -f test-unified
```

## If You Still Have Issues

See the troubleshooting guides:
- **Quick Fix:** [QUICK_FIX.md](QUICK_FIX.md)
- **Detailed Explanation:** [LINE_ENDINGS_FIX.md](LINE_ENDINGS_FIX.md)
- **Complete Troubleshooting:** [DOCKER_BUILD_TROUBLESHOOTING.md](DOCKER_BUILD_TROUBLESHOOTING.md)

## Why This Works

The Dockerfile now includes these steps:

1. Install `dos2unix` tool
2. Convert all `.sh` files from CRLF to LF
3. Make scripts executable

This happens automatically during the Docker build, so you don't need to manually fix the source files (though we also added `.gitattributes` to prevent the issue in the future).

## After Rebuilding

Once the image is rebuilt, you can use the Auto Grading System normally through the UI. The error will be gone.

---

**Need help?** Open an issue on GitHub with the output of:
```bash
docker version
docker images | grep fptuxaes
```
