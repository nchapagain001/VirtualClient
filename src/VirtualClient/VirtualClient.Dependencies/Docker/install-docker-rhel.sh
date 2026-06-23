#!/bin/bash
set -e

yum install -y -q yum-utils device-mapper-persistent-data lvm2

yum-config-manager --add-repo https://download.docker.com/linux/centos/docker-ce.repo

yum check-update -q || true
