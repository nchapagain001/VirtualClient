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
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using VirtualClient.Common.Docker;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;
    using VirtualClient.Logging;

    /// <summary>
    /// Command executes a workload profile inside a Docker container.
    /// </summary>
    internal class DockerCommand : ExecuteProfileCommand
    {
        private DockerContainerClient dockerClient;

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
        /// Executes the docker command by wrapping profile actions with DockerExecution,
        /// then executing the modified profile normally. Dependencies run on the host;
        /// actions execute inside the container through DockerExecution orchestration.
        /// </summary>
        protected override async Task<int> ExecuteAsync(string[] args, IServiceCollection dependencies, CancellationTokenSource cancellationTokenSource)
        {
            ILogger logger = dependencies.GetService<ILogger>();
            PlatformSpecifics platformSpecifics = dependencies.GetService<PlatformSpecifics>();

            // Verify Docker is available before proceeding
            this.dockerClient = new DockerContainerClient(logger);
            await this.EnsureDockerInstalledAndRunningAsync(logger, cancellationTokenSource).ConfigureAwait(false);

            logger?.LogInformation($"Docker execution mode enabled with image: {this.DockerImage}");

            // Wrap profile actions with DockerExecution component, leaving dependencies unchanged.
            // DockerExecution will manage container creation, but not cleanup (we handle cleanup here).
            this.WrapProfileActionsWithDockerExecution(platformSpecifics);

            try
            {
                // Execute profile normally. Base class handles:
                // 1. Dependency installation on host
                // 2. DockerExecution initialization (creates container, detects platform)
                // 3. Child action re-instantiation and execution within container
                // 4. Multiple iterations reuse the same container
                int exitCode = await base.ExecuteAsync(args, dependencies, cancellationTokenSource).ConfigureAwait(false);
                return exitCode;
            }
            finally
            {
                // Clean up Docker container after all iterations complete (unless user requested to keep it alive).
                await this.CleanupDockerContainerAsync(logger, cancellationTokenSource).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Wraps the loaded profile's actions with a DockerExecution component.
        /// </summary>
        private void WrapProfileActionsWithDockerExecution(PlatformSpecifics platformSpecifics)
        {
            if (this.Profiles == null || !this.Profiles.Any())
            {
                return;
            }

            DependencyProfileReference profileRef = this.Profiles.First();
            string profilePath = Path.Combine(
                platformSpecifics.ProfilesDirectory,
                profileRef.ProfileName);

            if (!File.Exists(profilePath))
            {
                throw new WorkloadException(
                    $"Profile not found at path: {profilePath}",
                    ErrorReason.InvalidProfileDefinition);
            }

            string profileJson = File.ReadAllText(profilePath);
            JsonNode profileNode = JsonNode.Parse(profileJson);

            if (profileNode?["Actions"] is not JsonArray actionsArray)
            {
                throw new WorkloadException(
                    $"Profile '{profileRef.ProfileName}' does not contain an Actions array.",
                    ErrorReason.InvalidProfileDefinition);
            }

            // Detect nested DockerExecution: if the profile already contains a DockerExecution action,
            // running the 'docker' subcommand would create a double-docker scenario.
            bool hasDockerExecution = actionsArray
                .OfType<JsonObject>()
                .Any(action => string.Equals(
                    action["Type"]?.GetValue<string>(),
                    "DockerExecution",
                    StringComparison.OrdinalIgnoreCase));

            if (hasDockerExecution)
            {
                string errorMessage =
                    $"Profile '{profileRef.ProfileName}' already contains a 'DockerExecution' action. " +
                    $"The 'docker' subcommand wraps profile actions in DockerExecution automatically. " +
                    $"Either use the 'docker' subcommand with a standard profile (e.g. GET-STARTED-OPENSSL.json), " +
                    $"or run the profile directly without the 'docker' subcommand (e.g. VirtualClient.exe --profile=GET-STARTED-OPENSSL-DOCKER.json).";

                ConsoleLogger.Default.LogMessage(errorMessage, EventContext.Persisted());

                throw new WorkloadException(errorMessage, ErrorReason.InvalidProfileDefinition);
            }

            // Create a new Actions array with DockerExecution wrapping the original actions.
            // Any LinuxPackageInstallation dependencies are moved into the container so packages
            // are installed inside the container rather than on the host.
            JsonArray newActions = new JsonArray();

            JsonArray dockerComponents = new JsonArray();

            // Move LinuxPackageInstallation dependencies into the container components (run first, before actions).
            if (profileNode["Dependencies"] is JsonArray dependenciesArray)
            {
                List<int> indicesToRemove = new List<int>();
                for (int i = 0; i < dependenciesArray.Count; i++)
                {
                    if (dependenciesArray[i] is JsonObject depObj &&
                        string.Equals(depObj["Type"]?.GetValue<string>(), "LinuxPackageInstallation", StringComparison.OrdinalIgnoreCase))
                    {
                        dockerComponents.Add(depObj.DeepClone());
                        indicesToRemove.Add(i);
                    }
                }

                for (int i = indicesToRemove.Count - 1; i >= 0; i--)
                {
                    dependenciesArray.RemoveAt(indicesToRemove[i]);
                }
            }

            foreach (JsonNode action in actionsArray)
            {
                dockerComponents.Add(action.DeepClone());
            }

            JsonObject dockerExecutionAction = new JsonObject
            {
                ["Type"] = "DockerExecution",
                ["Parameters"] = new JsonObject
                {
                    ["Image"] = this.DockerImage
                },
                ["Components"] = dockerComponents
            };

            newActions.Add(dockerExecutionAction);

            // Replace the profile's Actions with the wrapped version
            profileNode["Actions"] = newActions;

            // Write the modified profile to a temporary location
            string tempProfileName = $"{Path.GetFileNameWithoutExtension(profileRef.ProfileName)}_docker_wrapped.json";
            string tempProfilePath = Path.Combine(
                platformSpecifics.ProfilesDirectory,
                tempProfileName);

            File.WriteAllText(tempProfilePath, profileNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // Update this.Profiles to use the wrapped profile
            this.Profiles = new List<DependencyProfileReference>
            {
                new DependencyProfileReference(tempProfileName)
            };
        }

        /// <summary>
        /// Ensures Docker is installed and running.
        /// </summary>
        private async Task EnsureDockerInstalledAndRunningAsync(ILogger logger, CancellationTokenSource cancellationTokenSource)
        {
            logger?.LogInformation("Checking Docker availability...");
            bool dockerAvailable = await this.dockerClient.IsDockerAvailableAsync(cancellationTokenSource.Token).ConfigureAwait(false);

            if (!dockerAvailable)
            {
                throw new WorkloadException(
                    "Docker is not available on this system. Please install Docker and ensure it is running before using the docker subcommand.",
                    ErrorReason.InvalidProfileDefinition);
            }

            logger?.LogInformation("Docker is available and running.");
        }

        /// <summary>
        /// Cleans up the Docker container created for this execution.
        /// </summary>
        private async Task CleanupDockerContainerAsync(ILogger logger, CancellationTokenSource cancellationTokenSource)
        {
            if (this.KeepContainerAlive)
            {
                logger?.LogInformation("Container cleanup skipped (--keep-container-alive flag is set).");
                return;
            }

            string containerId = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID);
            if (string.IsNullOrWhiteSpace(containerId))
            {
                return;
            }

            try
            {
                string containerImage = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_IMAGE);
                string containerPlatform = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PLATFORM);
                string containerArch = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_ARCH);

                logger?.LogInformation($"Cleaning up Docker container: {containerId}");
                await this.dockerClient.StopContainerAsync(containerId, cancellationTokenSource.Token).ConfigureAwait(false);
                await this.dockerClient.RemoveContainerAsync(containerId, cancellationTokenSource.Token).ConfigureAwait(false);
                logger?.LogInformation($"Docker container cleanup successful - Image: {containerImage}, Platform: {containerPlatform}-{containerArch}");
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Failed to cleanup Docker container {containerId}: {ex.Message}");
            }
            finally
            {
                // Clear the container ID env vars
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PLATFORM, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_ARCH, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_IMAGE, null);
            }
        }
    }
}
