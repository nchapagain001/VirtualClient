#!/bin/bash
set -e

systemctl enable docker
systemctl start docker

# Wait for Docker daemon to be ready (up to 30 seconds)
for i in {1..30}; do
    if docker ps > /dev/null 2>&1; then
        echo "Docker daemon is ready"
        exit 0
    fi
    sleep 1
done

echo "Docker daemon failed to start"
exit 1
