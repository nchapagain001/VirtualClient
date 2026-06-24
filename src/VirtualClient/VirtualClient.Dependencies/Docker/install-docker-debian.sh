#!/bin/bash
set -e

VERSION=${1:-}

echo "Updating package cache..."
apt-get update -qq

echo "Installing Docker dependencies..."
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

echo "Adding Docker GPG key..."
curl -fsSL "${REPO_URL}/gpg" | gpg --dearmor -o /etc/apt/keyrings/docker.gpg --batch --yes

echo "Adding Docker repository..."
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] ${REPO_URL} $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null

echo "Updating package cache after adding repository..."
apt-get update -qq

echo "Installing Docker packages..."
if [ -z "$VERSION" ]; then
    apt-get install docker-ce docker-ce-cli containerd.io docker-compose-plugin --yes --quiet
else
    apt-get install docker-ce=$(apt-cache madison docker-ce | grep $VERSION | awk '{print $3}') \
                    docker-ce-cli=$(apt-cache madison docker-ce | grep $VERSION | awk '{print $3}') \
                    containerd.io docker-compose-plugin --yes --quiet
fi

echo "Docker installation completed"
