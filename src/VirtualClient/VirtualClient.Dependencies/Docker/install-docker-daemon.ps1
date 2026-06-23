# Install Docker daemon in WSL2 Ubuntu
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
