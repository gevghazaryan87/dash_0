# Docker Troubleshooting Guide: Fixing Hanging Containers

This guide details the diagnosis and resolution of the recent issue where several Docker containers in your stack (`clickhouse`, `db`, and `redis`) were failing to launch. It also provides a list of useful Docker commands that were used during the troubleshooting process.

## The Problem: Hanging Host Bind Mounts

The root cause of the issue was tied to how **Docker Desktop for Linux** handles file sharing between the Linux host environment and the lightweight virtual machine (VM) it runs internally to host containers.

In your `docker-compose.yml`, three services were experiencing similar issues:
1. **ClickHouse**: Attempted to mount the `./ch_data` folder from your host into `/var/lib/clickhouse`. Accessing this mapped directory from the container was hanging indefinitely, causing the database server to freeze during initialization.
2. **PostgreSQL (`db`) & Redis**: Both were mounting the `./healthchecks` folder from your host to run bash scripts that determine if the containers are healthy. Executing these scripts from inside the containers completely froze, triggering health check timeouts (`"Health check exceeded timeout (30s)"`). 

Because these core services were marked as `unhealthy` by Docker, dependent applications like `result`, `vote`, and `worker` refused to even attempt an initialization launch.

This freezing behavior is a known issue with the file-sharing system (like virtiofs or gRPC FUSE) that bridges the Linux host filesystem with the Docker Desktop VM. It can cause file read/write operations on bind mounts (where a host path like `./ch_data` or `./healthchecks` is mapped into the container) to block completely.

## The Solution: Avoiding Host File-Sharing

To resolve the freezing issue, the problematic bind mounts were replaced with more robust, performant alternatives native to Docker:

### 1. Replaced ClickHouse Bind Mount
The `clickhouse` service was altered to use a Docker-managed named volume (`clickhouse-data`) instead of the host `./ch_data` folder. Docker stores named volumes entirely within its own VM/storage domain, bypassing the buggy file-sharing bridge entirely. This is highly recommended for database workloads, as it offers vastly superior read/write performance.

### 2. Modernized Healthchecks
We removed the `./healthchecks` bind mount completely for the `db` and `redis` containers. Instead of depending on mounted external bash scripts, the health checks were rewritten to utilize standardized, built-in commands natively available inside the containers:
- **PostgreSQL**: Replaced with `pg_isready -U postgres`
- **Redis**: Replaced with `redis-cli ping | grep PONG`

After updating the configurations, the Docker Desktop service was restarted to clear out any frozen ghost processes. Finally, launching the stack using `docker compose up -d` successfully brought all applications and databases online beautifully.

---

## Essential Docker Troubleshooting Commands

Here is a handy reference of the commands utilized to diagnose the issue. These are excellent commands to learn for future Docker debugging:

### 1. View Service Status
Check the status of all containers defined in your stack, their health status, and mapped ports.
```bash
docker compose ps -a
```

### 2. Fetch Container Logs
Review what a container has been outputting. This is always the first step to diagnose why an application is failing cleanly instead of freezing.
```bash
docker logs <container_name>

# Example: Watch the last 50 lines continuously
docker logs -f --tail 50 clickhouse
```

### 3. Inspect Container Deep State
View low-level configuration, exact start times, and historical health check logs to see exactly why a container was marked unhealthy. 
```bash
docker inspect <container_name>

# Example: Filter specifically for health check results
docker inspect dash_0-db-1 --format='{{json .State.Health}}'
```

### 4. Execute Interactive Shell
Drop into a running container to verify network connectivity, run internal tools, or check internal file permissions.
```bash
docker exec -it <container_name> /bin/bash
# or if bash isn't available:
docker exec -it <container_name> /bin/sh
```

### 5. Running One-Off Commands inside Containers
Execute a single command inside a container without an interactive session. This is incredibly useful for testing internal logic (e.g. running your healthcheck script manually to see where it breaks).
```bash
# Debug file permissions inside a container
docker exec <container_name> ls -la /var/lib/clickhouse

# Debug a bash script step-by-step
docker exec dash_0-db-1 bash -x /healthchecks/postgres.sh
```

### 6. Destructive Reset
Destroy all existing containers, networks, and (most importantly) named volumes from the stack configuration. Essential when you've changed volume configurations and need to recreate them fresh.
```bash
docker compose down -v
```

### 7. View Detailed Architecture Setup
Verify exactly how Docker is running on your machine (useful to identify if you're running standard Docker Engine, a Snap package, or Docker Desktop).
```bash
docker info | grep -i "Operating System"
```
