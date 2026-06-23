# Verify Docker is installed and ready
$maxAttempts = 24
$attempt = 0
$dockerReady = $false

while ($attempt -lt $maxAttempts -and -not $dockerReady) {
    try {
        $dockerOutput = wsl --exec docker ps 2>&1
        if ($LASTEXITCODE -eq 0) {
            $dockerReady = $true
            Write-Output "Docker daemon is ready"
            wsl --exec docker --version
            exit 0
        }
    }
    catch {
        # Continue retrying
    }

    $attempt++
    if ($attempt -lt $maxAttempts) {
        Start-Sleep -Seconds 5
    }
}

if (-not $dockerReady) {
    Write-Output "ERROR: Docker daemon did not become ready after 2 minutes"
    exit 1
}
