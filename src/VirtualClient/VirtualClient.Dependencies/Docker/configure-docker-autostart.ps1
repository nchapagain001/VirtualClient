# Configure Docker daemon to auto-start on system boot
$dockerStartScript = @'
#!/bin/bash
echo "Docker auto-start script running at $(date)" >> /tmp/docker-autostart.log
sudo systemctl start docker
echo "Docker daemon started at $(date)" >> /tmp/docker-autostart.log
'@

$wslScriptPath = "\\wsl$\Ubuntu-24.04\tmp\docker-start.sh"
New-Item -ItemType Directory -Path "\\wsl$\Ubuntu-24.04\tmp" -Force -ErrorAction SilentlyContinue | Out-Null
Set-Content -Path $wslScriptPath -Value $dockerStartScript -Force

$taskAction = New-ScheduledTaskAction -Execute "wsl.exe" -Argument "--exec bash -c '/tmp/docker-start.sh'"
$taskTrigger = New-ScheduledTaskTrigger -AtStartup
$taskPrincipal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName "DockerDaemonAutoStart" -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Description "Automatically start Docker daemon on system boot" -Force

Write-Output "Docker auto-start configured successfully"
