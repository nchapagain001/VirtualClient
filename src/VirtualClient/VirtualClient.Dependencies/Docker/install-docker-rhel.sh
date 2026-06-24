#!/bin/bash
set -e

VERSION=${1:-}

echo "Installing Docker prerequisites..."
yum install -y -q yum-utils device-mapper-persistent-data lvm2

echo "Adding Docker repository..."
yum-config-manager --add-repo https://download.docker.com/linux/centos/docker-ce.repo

echo "Updating package cache..."
yum check-update -q || true

echo "Installing Docker packages..."
if [ -z "$VERSION" ]; then
    yum install docker-ce docker-ce-cli containerd.io docker-compose-plugin -y -q
else
    yum install docker-ce-$VERSION docker-ce-cli-$VERSION containerd.io docker-compose-plugin -y -q
fi

echo "Docker installation completed"
