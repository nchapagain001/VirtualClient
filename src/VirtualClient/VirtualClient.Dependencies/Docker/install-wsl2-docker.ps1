# Install WSL2 Ubuntu 24.04 and Docker daemon
Write-Output "Installing WSL2 Ubuntu 24.04..."
$wslUrl = "https://aka.ms/wslubuntu2404"
$distroPath = "$env:TEMP\ubuntu-2404.appx"
New-Item -ItemType Directory -Path $env:TEMP -Force -ErrorAction SilentlyContinue | Out-Null
Invoke-WebRequest -Uri $wslUrl -OutFile $distroPath -UseBasicParsing
Add-AppxPackage -Path $distroPath
wsl --set-default-version 2
wsl --set-default Ubuntu-24.04
Write-Output "WSL2 with Ubuntu 24.04 installed successfully"

# Install Docker daemon in WSL2 Ubuntu
Write-Output "Installing Docker daemon in WSL2..."
$installScript = @'
#!/bin/bash
set -e

echo "Updating package cache..."
sudo apt-get update -qq

echo "Installing Docker dependencies..."
sudo apt-get install -y -qq apt-transport-https ca-certificates curl gnupg lsb-release

echo "Adding Docker GPG key..."
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /usr/share/keyrings/docker-archive-keyring.gpg

echo "Adding Docker repository..."
echo "deb [arch=amd64 signed-by=/usr/share/keyrings/docker-archive-keyring.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

echo "Installing Docker packages..."
sudo apt-get update -qq
sudo apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin

echo "Enabling Docker to start on boot..."
sudo systemctl enable docker

echo "Starting Docker daemon..."
sudo systemctl start docker

echo "Docker installation completed"
'@

wsl --exec bash -c $installScript
Write-Output "Docker daemon installed successfully"
