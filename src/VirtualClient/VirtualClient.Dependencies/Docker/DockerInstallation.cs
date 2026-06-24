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
    using VirtualClient.Common.Docker;
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
        private DockerContainerClient dockerClient;

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

            ILogger logger = dependencies.GetService<ILogger>();
            this.dockerClient = new DockerContainerClient(logger);
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
                await this.SetStateValueAsync(DockerAlreadyInstalledStateKey, "True", cancellationToken)
                    .ConfigureAwait(false);
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
                string alreadyInstalled = await this.GetStateValueAsync(DockerAlreadyInstalledStateKey, cancellationToken)
                    .ConfigureAwait(false);

                if (alreadyInstalled == "True")
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

        private Task<bool> IsHyperVEnabledAsync(CancellationToken cancellationToken)
        {
            return this.dockerClient.IsHyperVEnabledAsync(cancellationToken);
        }

        private Task<bool> IsDockerInstalledAsync(CancellationToken cancellationToken)
        {
            return this.dockerClient.IsDockerAvailableAsync(cancellationToken);
        }

        private async Task InstallDockerOnWindowsAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            string dockerDesktopExe = this.GetDockerDesktopExePath();
            if (dockerDesktopExe != null)
            {
                await this.StartDockerDesktopAsync(telemetryContext, cancellationToken).ConfigureAwait(false);
                return;
            }

            string restartedState = await this.GetStateValueAsync(DockerRestartedStateKey, cancellationToken)
                .ConfigureAwait(false);

            if (restartedState != "True")
            {
                bool isHyperVEnabled = await this.IsHyperVEnabledAsync(cancellationToken).ConfigureAwait(false);

                if (!isHyperVEnabled)
                {
                    await this.CallPowerShell(Path.Combine(this.DockerScriptPath, "enable-hyperv.ps1"), telemetryContext, cancellationToken).ConfigureAwait(false);
                    await this.SetStateValueAsync(DockerRestartedStateKey, "True", cancellationToken).ConfigureAwait(false);
                    this.RequestReboot();
                    return;
                }
            }

            await this.CallPowerShell(Path.Combine(this.DockerScriptPath, "install-wsl2-docker.ps1"), telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallPowerShell(Path.Combine(this.DockerScriptPath, "configure-docker-autostart.ps1"), telemetryContext, cancellationToken).ConfigureAwait(false);
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
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "install-docker-debian.sh"), this.Version, telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "start-docker-daemon.sh"), null, telemetryContext, cancellationToken).ConfigureAwait(false);
        }

        private async Task InstallDockerOnRHELBasedAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "install-docker-rhel.sh"), this.Version, telemetryContext, cancellationToken).ConfigureAwait(false);
            await this.CallBashScript(Path.Combine(this.DockerScriptPath, "start-docker-daemon.sh"), null, telemetryContext, cancellationToken).ConfigureAwait(false);
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

        private Task CallBashScript(string scriptPath, string argument = null, EventContext telemetryContext = null, CancellationToken cancellationToken = default)
        {
            string arguments = string.IsNullOrEmpty(argument) ? scriptPath : $"{scriptPath} {argument}";
            return this.ExecuteCommandAsync("bash", arguments, null, telemetryContext, cancellationToken);
        }

        private async Task VerifyDockerAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            try
            {
                bool isDockerAvailable = await this.dockerClient.IsDockerAvailableAsync(cancellationToken).ConfigureAwait(false);
                if (!isDockerAvailable)
                {
                    throw new DependencyException(
                        "Docker verification failed.",
                        ErrorReason.DependencyInstallationFailed);
                }

                this.Logger?.LogTrace("Docker verification successful");
            }
            catch (Exception ex)
            {
                throw new DependencyException(
                    $"Docker verification failed: {ex.Message}",
                    ErrorReason.DependencyInstallationFailed);
            }
        }

        private async Task<string> GetStateValueAsync(string key, CancellationToken cancellationToken)
        {
            try
            {
                State state = await this.stateManager.GetStateAsync<State>(this.TypeName, cancellationToken)
                    .ConfigureAwait(false);

                if (state?.Properties.TryGetValue(key, out IConvertible value) == true)
                {
                    return value?.ToString();
                }
            }
            catch (Exception ex)
            {
                this.Logger?.LogWarning($"Failed to read state key '{key}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Sets a state value for the Docker installation process, preserving existing keys.
        /// </summary>
        private async Task SetStateValueAsync(string key, string value, CancellationToken cancellationToken)
        {
            IDictionary<string, IConvertible> properties = new Dictionary<string, IConvertible>();

            try
            {
                State existingState = await this.stateManager.GetStateAsync<State>(this.TypeName, cancellationToken)
                    .ConfigureAwait(false);

                if (existingState?.Properties != null)
                {
                    foreach (KeyValuePair<string, IConvertible> kvp in existingState.Properties)
                    {
                        properties[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                this.Logger?.LogWarning($"Failed to read existing state before setting key '{key}': {ex.Message}");
            }

            properties[key] = value;

            State state = new State(properties);
            await this.stateManager.SaveStateAsync(this.TypeName, state, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
