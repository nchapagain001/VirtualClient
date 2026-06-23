# Docker Integration

VirtualClient supports Docker as a native containerization infrastructure. Run any workload profile inside a Docker container with a single command. The framework handles container creation, platform detection, and cleanup automatically.

**Supported Platforms:** Linux x64, Linux ARM64 (Ubuntu, Debian, CentOS, RedHat, SUSE)

## Quick Start

```bash
# Run a workload in Docker
./VirtualClient docker --profile=GET-STARTED-OPENSSL.json --image=ubuntu:noble

# Run with multiple iterations
./VirtualClient docker --profile=PERF-CPU-OPENSSL.json --image=ubuntu:noble --iterations=5

# Use lightweight Alpine image
./VirtualClient docker --profile=GET-STARTED-OPENSSL.json --image=vc-alpine:latest
```

## How It Works

1. Check Docker is installed (auto-installs if missing)
2. Create container from your image
3. Detect container platform/architecture automatically
4. Download and install dependencies on host (volume-mounted to container)
5. Run your profile inside the container
6. Clean up container when complete

## Available Images

| Image | Details |
|-------|---------|
| `vc-ubuntu:noble` | Ubuntu 24.04 LTS with glibc (recommended, auto-built) |
| `vc-alpine:latest` | Lightweight Alpine with glibc compatibility (auto-built) |
| `ubuntu:22.04`, `ubuntu:24.04` | Public images from Docker Hub |
| `alpine:3.19`, `alpine:3.20` | Lightweight public images |
| Any Linux image | Must have glibc or compatibility layer |

## Three Ways to Run Workloads

**1. Run on host (native execution):**
```bash
./VirtualClient --profile=GET-STARTED-OPENSSL.json
```

**2. Run in Docker container (generic profile via docker subcommand):**
```bash
./VirtualClient docker --profile=GET-STARTED-OPENSSL.json --image=ubuntu:noble
```

**3. Run with native Docker profile (pre-configured for containers):**
```bash
./VirtualClient --profile=GET-STARTED-OPENSSL-DOCKER.json
```

**Key difference:**
- Option 1: Execution on host
- Options 2 & 3: Identical container execution result (different approaches)
  - Option 2: Add docker subcommand to any existing profile
  - Option 3: Use pre-built Docker-native profile

## Execution Flows: docker subcommand vs DockerExecution Component

Both **Option 2** (docker subcommand) and **Option 3** (DockerExecution component) produce identical results. Here's how each works:

### Option 2: Docker Subcommand
```bash
./VirtualClient docker --profile=GET-STARTED-OPENSSL.json --image=ubuntu:noble
```

**Execution flow:**
```
DockerCommand.ExecuteAsync()
├─ Create container (ubuntu:noble)
├─ Set VC_DOCKER_CONTAINER_ID environment variable
├─ Execute profile: GET-STARTED-OPENSSL.json
│  └─ Actions → OpenSslExecutor
│     └─ OpenSslExecutor runs on HOST
│        └─ When it creates processes:
│           └─ DockerProcessManager intercepts
│              └─ Routes via: docker exec <containerId> <openssl command>
└─ Cleanup container
```

**Characteristics:**
- Direct, explicit Docker invocation
- Image specified via `--image` flag
- Works with any existing profile
- Single command orchestrates the entire flow

### Option 3: DockerExecution Component
```bash
./VirtualClient --profile=GET-STARTED-OPENSSL-DOCKER.json
```

**Execution flow:**
```
ExecuteProfileCommand.ExecuteAsync()
├─ Execute profile: GET-STARTED-OPENSSL-DOCKER.json
│  └─ Actions → DockerExecution component
│     ├─ Create container (ubuntu:noble, from profile Parameters)
│     ├─ Set VC_DOCKER_CONTAINER_ID environment variable
│     ├─ Wrap ProcessManager with DockerProcessManager
│     └─ Components → OpenSslExecutor (child of DockerExecution)
│        └─ OpenSslExecutor runs on HOST
│           └─ When it creates processes:
│              └─ DockerProcessManager intercepts
│                 └─ Routes via: docker exec <containerId> <openssl command>
└─ Cleanup container
```

**Characteristics:**
- Container configuration embedded in profile
- Image specified via profile `Parameters.DockerImage`
- Supports nested component hierarchies
- More flexible for complex workloads

### Comparison

| Aspect | Docker Subcommand | DockerExecution Component |
|--------|-------------------|--------------------------|
| **Command** | `docker --profile=X.json --image=Y` | `--profile=X-DOCKER.json` |
| **Profile** | Generic (any existing profile) | Docker-specific profile |
| **Image** | Command-line flag | Profile parameter |
| **Result** | Identical: processes run in container | Identical: processes run in container |
| **Use Case** | Ad-hoc execution | Pre-configured workload |

### Key Insight: Both Use DockerProcessManager

Regardless of which approach you use, **component code runs on the host**, but **all processes created by components route to the container** via `DockerProcessManager`:

1. Component calls: `processManager.CreateProcess("openssl", args...)`
2. `DockerProcessManager` intercepts this call
3. Wraps it: `docker exec <containerId> openssl <args>`
4. Process executes inside container
5. Output returned to component

This means:
- ✓ No need to rewrite component code for Docker
- ✓ Components work identically on host or in container
- ✓ Transparent process routing to container

## Examples

**Basic execution:**
```bash
./VirtualClient docker --profile=GET-STARTED-OPENSSL.json --image=ubuntu:noble
```

**With telemetry logging:**
```bash
./VirtualClient docker \
  --profile=PERF-CPU-OPENSSL.json \
  --image=ubuntu:noble \
  --iterations=3 \
  --logger=csv \
  --logger=summary \
  --metadata="test=benchmark,region=eastus"
```

**Debug mode (keep container running):**
```bash
./VirtualClient docker \
  --profile=GET-STARTED-OPENSSL.json \
  --image=ubuntu:noble \
  --keep-container-alive=true
```

Then inspect:
```bash
# Container ID shown in output (e.g., abc123def456)
docker exec -it abc123def456 bash

# Inside container, check files:
ls -la /mnt/packages
ls -la /mnt/logs
exit

# Cleanup:
docker stop abc123def456 && docker rm abc123def456
```

## Auto-Installation

Docker is automatically installed if not found:
- Installs Docker packages
- Adds current user to docker group (passwordless access)
- Starts Docker daemon
- Verifies installation

No setup needed—just run the command.

## Troubleshooting

| Issue | Solution |
|-------|----------|
| "docker: command not found" | VirtualClient auto-installs Docker; run command again |
| "Permission denied" | Current user may need to log out/in after Docker installation |
| Need to inspect container | Use `--keep-container-alive=true` to preserve container |
| Files not accessible after run | Use debug mode above to keep container running |

See [Command Line Reference](./0010-command-line.md#docker) for complete options.

## Future Features

**Windows Docker Support**
- Windows container images (Windows Server, Nano Server)

**Multi-Container Orchestration**
- Run multiple containers on a single host
- Client-server workload support across containers
