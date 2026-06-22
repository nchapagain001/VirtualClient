$feature = Get-WindowsOptionalFeature -Online -FeatureName Hyper-V
if ($feature.State -eq 'Enabled') {
    exit 0
} else {
    exit 1
}
