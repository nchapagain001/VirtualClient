#!/bin/bash
set -e

apt-get update -qq
apt-get install ca-certificates curl gnupg lsb-release --yes --quiet

mkdir -p /etc/apt/keyrings

# Detect distro to use the correct Docker repository
DISTRO=$(. /etc/os-release && echo "$ID")
case "$DISTRO" in
    debian)
        REPO_URL="https://download.docker.com/linux/debian"
        ;;
    *)
        REPO_URL="https://download.docker.com/linux/ubuntu"
        ;;
esac

curl -fsSL "${REPO_URL}/gpg" | gpg --dearmor -o /etc/apt/keyrings/docker.gpg --batch --yes

echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] ${REPO_URL} $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null

apt-get update -qq
