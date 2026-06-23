# Enable Hyper-V and Virtual Machine Platform features
Enable-WindowsOptionalFeature -Online -FeatureName Hyper-V -All -NoRestart
Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -NoRestart
Write-Output "Hyper-V and VirtualMachinePlatform enabled successfully"
