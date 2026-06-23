# Install WSL2 Ubuntu 24.04
$wslUrl = "https://aka.ms/wslubuntu2404"
$distroPath = "$env:TEMP\ubuntu-2404.appx"
New-Item -ItemType Directory -Path $env:TEMP -Force -ErrorAction SilentlyContinue | Out-Null
Invoke-WebRequest -Uri $wslUrl -OutFile $distroPath -UseBasicParsing
Add-AppxPackage -Path $distroPath
wsl --set-default-version 2
wsl --set-default Ubuntu-24.04
Write-Output "WSL2 with Ubuntu 24.04 installed successfully"
