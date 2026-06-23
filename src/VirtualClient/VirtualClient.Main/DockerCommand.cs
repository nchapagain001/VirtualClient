// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using VirtualClient.Common.Docker;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;

    /// <summary>
    /// Command executes a workload profile inside a Docker container.
    /// </summary>
    internal class DockerCommand : ExecuteProfileCommand
    {
        private DockerContainerClient dockerClient;
        private string containerId;

        /// <summary>
        /// The Docker image to use for container execution (e.g. ubuntu:noble, redis:7.0-alpine).
        /// </summary>
        public string DockerImage { get; set; }

        /// <summary>
        /// Whether to keep the container alive after execution for debugging.
        /// </summary>
        public bool KeepContainerAlive { get; set; }

        /// <summary>
        /// Initializes the command runtime before dependency initialization and execution.
        /// </summary>
        protected override void Initialize(string[] args, PlatformSpecifics platformSpecifics)
        {
            // Validate docker image is provided
            if (string.IsNullOrWhiteSpace(this.DockerImage))
            {
                throw new WorkloadException(
                    "Docker image (--image) is required for docker command execution.",
                    ErrorReason.InvalidProfileDefinition);
            }

            // Validate that at least one profile is specified
            if (this.Profiles == null || !this.Profiles.Any())
            {
                throw new WorkloadException(
                    "At least one profile (--profile) is required for docker command execution.",
                    ErrorReason.InvalidProfileDefinition);
            }

            // Call parent initialization
            base.Initialize(args, platformSpecifics);
        }

        /// <summary>
        /// Executes the docker command: creates container, runs profile, cleans up, then flushes telemetry.
        /// </summary>
        protected override async Task<int> ExecuteAsync(string[] args, IServiceCollection dependencies, CancellationTokenSource cancellationTokenSource)
        {
            ILogger logger = dependencies.GetService<ILogger>();
            EventContext telemetryContext = EventContext.Persisted();
            this.dockerClient = new DockerContainerClient(logger);
            PlatformSpecifics platformSpecifics = dependencies.GetService<PlatformSpecifics>();
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            int exitCode = 0;

            try
            {
                // Step 1: Ensure Docker is installed and running (auto-install if missing)
                await this.EnsureDockerInstalledAndRunningAsync(dependencies, logger, cancellationTokenSource).ConfigureAwait(false);

                if (!dockerAvailable)
                {
                    throw new WorkloadException(
                        "Docker is not available. Please ensure Docker is installed and the daemon is running. " +
                        "Run 'docker version' to verify your Docker installation.",
                        ErrorReason.DependencyNotFound);
                }

                // Step 3: Validate profile — fail immediately if DockerExecution is in any action (double Docker)
                await this.ValidateProfileForDockerSubcommandAsync(dependencies, cancellationToken).ConfigureAwait(false);

                // Step 3.5: Create Docker container with volume mounts
                Dictionary<string, string> volumeMappings = this.PrepareVolumeMounts(logger, platformSpecifics);

                this.containerId = await this.dockerClient.CreateContainerAsync(
                    this.DockerImage,
                    volumeMappings,
                    null,
                    cancellationToken).ConfigureAwait(false);

                // Generate short container name alias for manual reference
                string containerNameAlias = Guid.NewGuid().ToString().Substring(0, 8).ToLowerInvariant();
                telemetryContext.AddContext(nameof(containerNameAlias), containerNameAlias)
                                .AddContext("containerCreated", true)
                                .AddContext(nameof(this.containerId), this.containerId)
                                .AddContext(nameof(volumeMappings),  volumeMappings);

                // Step 4: Set container ID in environment for child components
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID, this.containerId);

                // Step 5: Inspect image to detect container platform and set env vars for package resolution
                (PlatformID containerPlatform, Architecture containerArch) = await this.dockerClient.InspectImageAsync(
                    this.DockerImage, cancellationToken).ConfigureAwait(false);

                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PLATFORM, containerPlatform.ToString());
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_ARCH, containerArch.ToString());

                telemetryContext.AddContext(nameof(containerPlatform), containerPlatform.ToString())
                                .AddContext(nameof(containerArch), containerArch.ToString());

                // Step 5b: Set up path translation for container-aware ProcessManager
                string currentDirectory = platformSpecifics.CurrentDirectory;

                Dictionary<string, string> pathMappings = new Dictionary<string, string>
                {
                    { EnvironmentVariable.VC_DOCKER_PACKAGES_HOST, platformSpecifics.PackagesDirectory },
                    { EnvironmentVariable.VC_DOCKER_LOGS_HOST, platformSpecifics.LogsDirectory },
                    { EnvironmentVariable.VC_DOCKER_STATE_HOST, platformSpecifics.StateDirectory },
                    { EnvironmentVariable.VC_DOCKER_PACKAGES_MOUNT, "/mnt/packages" },                    
                    { EnvironmentVariable.VC_DOCKER_LOGS_MOUNT, "/mnt/logs" },
                    { EnvironmentVariable.VC_DOCKER_STATE_MOUNT, "/mnt/state" }
                };

                foreach(var mapping in pathMappings)
                {
                    Environment.SetEnvironmentVariable(mapping.Key, mapping.Value);
                }

                telemetryContext.AddContext(nameof(pathMappings), pathMappings);

                // Step 6: Install profile dependencies on host (packages volume-mounted into container)
                this.InstallDependencies = true;
                CancellationTokenSource cts1 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await base.ExecuteAsync(args, dependencies, cts1).ConfigureAwait(false);
                this.InstallDependencies = false;

                // Step 7: Execute profile actions — ProcessManager routing handled by Phase 5 wrapper
                CancellationTokenSource cts2 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitCode = await base.ExecuteAsync(args, dependencies, cts2).ConfigureAwait(false);

                telemetryContext.AddContext(nameof(exitCode), exitCode);
                telemetryContext.AddContext("executionSuccess", exitCode == 0);
            }
            finally
            {
                // Step 8: Cleanup container
                if (!string.IsNullOrWhiteSpace(this.containerId))
                {
                    await this.CleanupContainerAsync(logger, cancellationToken).ConfigureAwait(false);
                }
            }

            return exitCode;
        }

        /// <summary>
        /// Ensures Docker is installed and running. Auto-installs if not available.
        /// </summary>
        private async Task EnsureDockerInstalledAndRunningAsync(IServiceCollection dependencies, ILogger logger, CancellationTokenSource cancellationTokenSource)
        {
            this.LogDockerInfo(logger, "Checking Docker availability...");
            bool dockerAvailable = await this.dockerClient.IsDockerAvailableAsync(cancellationTokenSource.Token).ConfigureAwait(false);

            if (dockerAvailable)
            {
                return;
            }

            this.LogDockerInfo(logger, "Docker not found. Installing Docker using INSTALL-DOCKER profile...");

            // Run INSTALL-DOCKER profile to install Docker
            DependencyProfileReference originalProfile = this.Profiles?.FirstOrDefault();
            this.Profiles = new List<DependencyProfileReference>
            {
                new DependencyProfileReference("INSTALL-DOCKER.json")
            };
            this.InstallDependencies = true;

                foreach (JsonNode action in actions)
                {
                    string type = action?["Type"]?.GetValue<string>() ?? string.Empty;
                    if (type.Equals("DockerExecution", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new WorkloadException(
                            $"Profile '{Path.GetFileName(profilePath)}' contains a 'DockerExecution' component " +
                            $"which conflicts with the 'docker' subcommand (double Docker). " +
                            $"Remove DockerExecution from the profile when using the docker subcommand.",
                            ErrorReason.InvalidProfileDefinition);
                    }
                }
            }

            return imagesDir;
        }

        /// <summary>
        /// Executes the profile inside the container via docker exec using the mounted VirtualClient binary.
        /// </summary>
        private async Task ValidateProfileForDockerSubcommandAsync(IServiceCollection dependencies, CancellationToken cancellationToken)
        {
            IEnumerable<string> profilePaths = await this.EvaluateProfilesAsync(dependencies);

            foreach (string profilePath in profilePaths)
            {
                if (!File.Exists(profilePath))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return 1;
                }

                string profileFileName = Path.GetFileName(profilePath);
                this.LogDockerInfo(logger, $"Executing profile in container: {profileFileName}");

                // Execute the profile inside the container using the volume-mounted VC binary
                string command = $"/app/VirtualClient --profile=/app/profiles/{profileFileName}";

                DockerExecResult result = await this.dockerClient.ExecuteInContainerAsync(
                    this.containerId, command, cancellationToken).ConfigureAwait(false);

                if (!result.Success)
                {
                    throw new WorkloadException(
                        $"Profile '{profileFileName}' failed in container. " +
                        $"Exit code: {result.ExitCode}. Error: {result.StandardError}",
                        ErrorReason.WorkloadFailed);
                }

                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    this.LogDockerInfo(logger, $"Container output: {result.StandardOutput}");
                }
            }


        /// <summary>
        /// Prepares volume mounts for container using platform-specific directories.
        /// </summary>
        private Dictionary<string, string> PrepareVolumeMounts(ILogger logger, PlatformSpecifics platformSpecifics)
        {
            Dictionary<string, string> volumeMounts = new Dictionary<string, string>();
            string currentDirectory = Environment.CurrentDirectory;

            // Mount the VirtualClient binary directory so it can be executed inside the container
            volumeMounts[currentDirectory] = "/app";

            // Standard mount points
            volumeMounts[Path.Combine(currentDirectory, "packages")] = "/app/packages";
            volumeMounts[Path.Combine(currentDirectory, "logs")] = "/app/logs";
            volumeMounts[Path.Combine(currentDirectory, "state")] = "/app/state";

            this.LogDockerInfo(logger,
                $"Volume mounts configured: VirtualClient binary, packages, logs, state directories mounted.");

            return volumeMounts;
        }

        /// <summary>
        /// Cleans up Docker container after execution.
        /// </summary>
        private async Task CleanupContainerAsync(ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                if (this.KeepContainerAlive)
                {
                    this.LogDockerInfo(logger,
                        $"Container is being kept alive for debugging. Container ID: {this.containerId}. " +
                        $"To manually inspect: docker exec -it {this.containerId} bash. " +
                        $"To cleanup: docker stop {this.containerId} && docker rm {this.containerId}");
                }
                else
                {
                    this.LogDockerInfo(logger, $"Stopping container: {this.containerId}");
                    await this.dockerClient.StopContainerAsync(this.containerId, cancellationToken).ConfigureAwait(false);

                    this.LogDockerInfo(logger, $"Removing container: {this.containerId}");
                    await this.dockerClient.RemoveContainerAsync(this.containerId, cancellationToken).ConfigureAwait(false);

                    this.LogDockerInfo(logger, "Container cleaned up successfully.");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Container cleanup encountered an error: {ex.Message}");
                if (!this.KeepContainerAlive)
                {
                    logger?.LogWarning(
                        $"Manual cleanup may be needed. Container ID: {this.containerId}. " +
                        $"Run: docker stop {this.containerId} && docker rm {this.containerId}");
                }
            }
        }

        /// <summary>
        /// Logs docker-related information.
        /// </summary>
        private void LogDockerInfo(ILogger logger, string message)
        {
            logger?.LogInformation(message);
        }
    }
}
