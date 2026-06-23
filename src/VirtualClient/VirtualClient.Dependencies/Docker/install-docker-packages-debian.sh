#!/bin/bash
set -e

VERSION=${1:-}

if [ -z "$VERSION" ]; then
    apt-get install docker-ce docker-ce-cli containerd.io docker-compose-plugin --yes --quiet
else
    apt-get install docker-ce=$(apt-cache madison docker-ce | grep $VERSION | awk '{print $3}') \
                    docker-ce-cli=$(apt-cache madison docker-ce | grep $VERSION | awk '{print $3}') \
                    containerd.io docker-compose-plugin --yes --quiet
fi
