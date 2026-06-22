// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Dependencies
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Polly;
    using VirtualClient.Common;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;

    /// <summary>
    /// Provides functionality for installing Docker on Windows and Linux systems.
    /// Supports multiple distros (Ubuntu, Debian, CentOS, RHEL) and handles
    /// multi-step installations that require restarts.
    /// </summary>
    public class DockerInstallation : VirtualClientComponent
    {
        private const string DockerAlreadyInstalledStateKey = "DockerAlreadyInstalled";
        private const string DockerRestartedStateKey = "DockerRestarted";

        private ISystemManagement systemManager;
        private IStateManager stateManager;
        private string dockerScriptPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="DockerInstallation"/> class.
        /// </summary>
        /// <param name="dependencies">An enumeration of dependencies that can be used for dependency injection.</param>
        /// <param name="parameters">A series of key value pairs that dictate runtime execution.</param>
        public DockerInstallation(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters)
            : base(dependencies, parameters)
        {
            this.RetryPolicy = Policy.Handle<Exception>().WaitAndRetryAsync(5, (retries) => TimeSpan.FromSeconds(retries + 1));
            this.systemManager = dependencies.GetService<ISystemManagement>();
            this.stateManager = dependencies.GetService<IStateManager>();
        }

        /// <summary>
        /// The version of docker to install from the feed.
        /// </summary>
        public string Version
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(DockerInstallation.Version), string.Empty);
            }

            set
            {
                this.Parameters[nameof(DockerInstallation.Version)] = value;
            }
        }

        /// <summary>
        /// A policy that defines how the component will retry when
        /// it experiences transient issues.
        /// </summary>
        public IAsyncPolicy RetryPolicy { get; set; }

        /// <summary>
        /// Gets the Docker scripts directory path.
        /// </summary>
        private string DockerScriptPath
        {
            get
            {
                if (this.dockerScriptPath == null)
                {
                    this.dockerScriptPath = Path.Combine(this.PlatformSpecifics.ScriptsDirectory, "docker");

                    if (!Directory.Exists(this.dockerScriptPath))
                    {
                        throw new WorkloadException(
                            $"Docker installation scripts not found at {this.dockerScriptPath}",
                            ErrorReason.DependencyNotFound);
                    }
                }

                return this.dockerScriptPath;
            }
        }

        /// <summary>
        /// Initializes docker installation requirements.
        /// </summary>
        /// <param name="telemetryContext">Provides context information that will be captured with telemetry events.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <returns></returns>
        protected override async Task InitializeAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            bool isDockerInstalled = await this.IsDockerInstalledAsync(cancellationToken)
                .ConfigureAwait(false);

            if (isDockerInstalled)
            {
                this.SetStateValue(DockerAlreadyInstalledStateKey, "True");
                return;
            }
        }

        /// <summary>
        /// Executes the Docker installation process based on the platform and configuration.
        /// </summary>
        /// <param name="telemetryContext">Provides context information for telemetry events.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            try
            {
                if (this.GetStateValue(DockerAlreadyInstalledStateKey) == "True")
                {
                    return;
                }

                if (this.Platform == PlatformID.Unix)
                {
                    LinuxDistributionInfo distroInfo = await this.systemManager.GetLinuxDistributionAsync(cancellationToken)
                        .ConfigureAwait(false);

                    switch (distroInfo.LinuxDistribution)
                    {
                        case LinuxDistribution.Ubuntu:
                        case LinuxDistribution.Debian:
                            await this.InstallDockerOnDebianBasedAsync(telemetryContext, cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case LinuxDistribution.CentOS7:
                        case LinuxDistribution.CentOS8:
                        case LinuxDistribution.RHEL7:
                        case LinuxDistribution.RHEL8:
                            await this.InstallDockerOnRHELBasedAsync(telemetryContext, cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        default:
                            throw new WorkloadException(
                                $"Docker installation is not supported on the current Linux distro: {distroInfo.LinuxDistribution}. " +
                                $"Supported distros: Ubuntu, Debian, CentOS7, CentOS8, RHEL7, RHEL8.",
                                ErrorReason.LinuxDistributionNotSupported);
                    }
                }
                else if (this.Platform == PlatformID.Win32NT)
                {
                    await this.InstallDockerOnWindowsAsync(telemetryContext, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await this.VerifyDockerAsync(telemetryContext, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<bool> IsHyperVEnabledAsync(CancellationToken cancellationToken)
        {
            string scriptPath = Path.Combine(this.DockerScriptPath, "check-hyperv-enabled.ps1");
            int exitCode = await this.CallPowerShellAndGetExitCode(scriptPath, cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }

        private async Task<bool> IsDockerInstalledAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Check if Docker daemon is functional with retry logic
                int maxRetries = 3;
                for (int i = 0; i < maxRetries; i++)
                {
                    using (IProcessProxy process = this.systemManager.ProcessManager.CreateProcess("docker", "ps"))
                    {
                        await process.StartAndWaitAsync(cancellationToken).ConfigureAwait(false);
                        if (process.ExitCode == 0)
                        {
                            return true;
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task InstallDockerOnWindowsAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            string dockerDesktopExe = this.GetDockerDesktopExePath();
            if (dockerDesktopExe != null)
            {
                await this.StartDockerDesktopAsync(telemetryContext, cancellationToken).ConfigureAwait(false);
                return;
            }

            string restartedState = this.GetStateValue(DockerRestartedStateKey);

            if (restartedState != "True")
            {
                bool isHyperVEnabled = await this.IsHyperVEnabledAsync(cancellationToken).ConfigureAwait(false);

                if (!isHyperVEnabled)
                {
                    await this.CallPowerShell(Path.Combine(this.DockerScriptPath, "enable-hyperv.ps1"), telemetryContext, cancellationToken).ConfigureAwait(false);
                    this.SetStateValue(DockerRestartedStateKey, "True");
                    this.RequestReboot();
                    return;
                }
            }

            await this.CallPowerShell(Path.Combine(this.DockerScriptPath, "install-wsl2.ps1"), telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallPowerShell(Path.Combine(this.DockerScriptPath, "install-docker-daemon.ps1"), telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallPowerShell(Path.Combine(this.DockerScriptPath, "configure-docker-autostart.ps1"), telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallPowerShell(Path.Combine(this.DockerScriptPath, "verify-docker.ps1"), telemetryContext, cancellationToken).ConfigureAwait(false);
        }

        private string GetDockerDesktopExePath()
        {
            string exePath = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
            return File.Exists(exePath) ? exePath : null;
        }

        private async Task StartDockerDesktopAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            string dockerDesktopExe = this.GetDockerDesktopExePath();
            IProcessProxy dockerDesktopProcess = this.systemManager.ProcessManager.CreateProcess(dockerDesktopExe, null);
            dockerDesktopProcess.Start();

            int maxAttempts = 12;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                using (IProcessProxy process = this.systemManager.ProcessManager.CreateProcess("docker", "ps"))
                {
                    await process.StartAndWaitAsync(cancellationToken).ConfigureAwait(false);
                    if (process.ExitCode == 0)
                    {
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }

            throw new WorkloadException(
                "Docker Desktop did not start within 2 minutes.",
                ErrorReason.DependencyInstallationFailed);
        }

        private async Task InstallDockerOnDebianBasedAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "install-docker-debian.sh"), null, telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "install-docker-packages-debian.sh"), this.Version, telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "start-docker-daemon.sh"), null, telemetryContext, cancellationToken).ConfigureAwait(false);
        }

        private async Task InstallDockerOnRHELBasedAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "install-docker-rhel.sh"), null, telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "install-docker-packages-rhel.sh"), this.Version, telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "start-docker-daemon.sh"), null, telemetryContext, cancellationToken).ConfigureAwait(false);
        }

        private Task ExecuteCommandAsync(
            string command,
            string arguments,
            EventContext telemetryContext,
            CancellationToken cancellationToken)
        {
            return this.ExecuteCommandAsync(command, arguments, null, telemetryContext, cancellationToken);
        }

        private Task ExecuteCommandAsync(
            string command,
            string arguments,
            string workingDirectory,
            EventContext telemetryContext,
            CancellationToken cancellationToken)
        {
            return this.RetryPolicy.ExecuteAsync(async () =>
            {
                if (workingDirectory == null)
                {
                    workingDirectory = Environment.CurrentDirectory;
                }

                using (IProcessProxy process = this.systemManager.ProcessManager.CreateElevatedProcess(
                    this.Platform,
                    command,
                    arguments,
                    workingDirectory))
                {
                    this.CleanupTasks.Add(() => process.SafeKill(this.Logger));
                    this.LogProcessTrace(process);

                    await process.StartAndWaitAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await this.LogProcessDetailsAsync(process, telemetryContext)
                            .ConfigureAwait(false);

                        process.ThrowIfErrored<DependencyException>(errorReason: ErrorReason.DependencyInstallationFailed);
                    }
                }
            });
        }

        private Task CallPowerShell(string scriptPath, EventContext telemetryContext, CancellationToken cancellationToken)
        {
            return this.ExecuteCommandAsync("powershell", $"-ExecutionPolicy Bypass -File \"{scriptPath}\"", null, telemetryContext, cancellationToken);
        }

        private async Task<int> CallPowerShellAndGetExitCode(string scriptPath, CancellationToken cancellationToken)
        {
            try
            {
                string arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"";
                using (IProcessProxy process = this.systemManager.ProcessManager.CreateProcess("powershell", arguments))
                {
                    await process.StartAndWaitAsync(cancellationToken).ConfigureAwait(false);
                    return process.ExitCode;
                }
            }
            catch
            {
                return -1;
            }
        }

        private Task CallBashScript(string scriptPath, string argument = null, EventContext telemetryContext = null, CancellationToken cancellationToken = default)
        {
            string arguments = string.IsNullOrEmpty(argument) ? scriptPath : $"{scriptPath} {argument}";
            return this.ExecuteCommandAsync("bash", arguments, null, telemetryContext, cancellationToken);
        }

        private async Task VerifyDockerAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            try
            {
                using (IProcessProxy process = this.systemManager.ProcessManager.CreateElevatedProcess(
                    this.Platform,
                    "docker",
                    "version",
                    null))
                {
                    await process.StartAndWaitAsync(cancellationToken).ConfigureAwait(false);

                    if (process.ExitCode != 0)
                    {
                        throw new DependencyException(
                            "Docker verification failed.",
                            ErrorReason.DependencyInstallationFailed);
                    }

                    this.Logger?.LogTrace(process.StandardOutput.ToString());
                }
            }
            catch (Exception ex)
            {
                throw new DependencyException(
                    $"Docker verification failed: {ex.Message}",
                    ErrorReason.DependencyInstallationFailed);
            }
        }

        private string GetStateValue(string key)
        {
            try
            {
                State state = this.stateManager?.GetStateAsync<State>(this.TypeName, CancellationToken.None)
                    .GetAwaiter().GetResult();

                if (state?.Properties.TryGetValue(key, out IConvertible value) == true)
                {
                    return value?.ToString();
                }
            }
            catch
            {
                // State may not exist yet
            }

            return null;
        }

        /// <summary>
        /// Sets a state value for the Docker installation process.
        /// </summary>
        private void SetStateValue(string key, string value)
        {
            State state = new State(new Dictionary<string, IConvertible>
            {
                { key, value }
            });

            this.stateManager?.SaveStateAsync(
                this.TypeName,
                state,
                CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}