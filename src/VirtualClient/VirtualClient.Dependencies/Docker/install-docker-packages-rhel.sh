#!/bin/bash
set -e

VERSION=${1:-}

if [ -z "$VERSION" ]; then
    yum install docker-ce docker-ce-cli containerd.io docker-compose-plugin -y -q
else
    yum install docker-ce-$VERSION docker-ce-cli-$VERSION containerd.io docker-compose-plugin -y -q
fi
