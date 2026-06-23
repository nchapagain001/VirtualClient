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

            // Wrap profile actions with DockerExecution component, leaving dependencies unchanged.
            this.WrapProfileActionsWithDockerExecution(platformSpecifics);

            try
            {
                // Execute profile normally. DockerExecution manages container with DeferContainerCleanup=true.
                // All iterations reuse the same container.
                return await base.ExecuteAsync(args, dependencies, cancellationTokenSource).ConfigureAwait(false);
            }
            finally
            {
                if (!this.KeepContainerAlive)
                {
                    // Clean up Docker container after all iterations complete (unless user requested to keep it alive).
                    await this.CleanupDockerContainerAsync(logger, cancellationTokenSource).ConfigureAwait(false);
                }
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

            // Create a new Actions array with DockerExecution wrapping all dependencies and actions.
            // All dependencies and actions run inside the container for complete encapsulation.
            JsonArray newActions = new JsonArray();
            JsonArray dockerComponents = new JsonArray();

            // Move all dependencies into the container components (run first, before actions).
            if (profileNode["Dependencies"] is JsonArray dependenciesArray)
            {
                foreach (JsonNode dependency in dependenciesArray)
                {
                    dockerComponents.Add(dependency.DeepClone());
                }

                // Clear dependencies array after moving to container
                profileNode["Dependencies"] = new JsonArray();
            }

            // Move all actions into the container components (run after dependencies).
            foreach (JsonNode action in actionsArray)
            {
                dockerComponents.Add(action.DeepClone());
            }

            JsonObject dockerExecutionAction = new JsonObject
            {
                ["Type"] = "DockerExecution",
                ["Parameters"] = new JsonObject
                {
                    ["Image"] = this.DockerImage,
                    ["DeferContainerCleanup"] = true
                },
                ["Components"] = dockerComponents
            };

            newActions.Add(dockerExecutionAction);

            // Replace the profile's Actions with the wrapped version
            profileNode["Actions"] = newActions;

            // Write the modified profile to a temporary location
            string tempProfileName = $"{Path.GetFileNameWithoutExtension(profileRef.ProfileName)}_DockerExecution_wrapped.json";
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
            DockerContainerClient.LogDockerInformation("Checking Docker availability...");
            bool dockerAvailable = await this.dockerClient.IsDockerAvailableAsync(cancellationTokenSource.Token).ConfigureAwait(false);

            if (!dockerAvailable)
            {
                throw new WorkloadException(
                    "Docker is not available on this system. Please install Docker and ensure it is running before using the docker subcommand.",
                    ErrorReason.InvalidProfileDefinition);
            }

            DockerContainerClient.LogDockerInformation("Docker is available and running.");
        }

        /// <summary>
        /// Cleans up the Docker container created for this execution.
        /// </summary>
        private async Task CleanupDockerContainerAsync(ILogger logger, CancellationTokenSource cancellationTokenSource)
        {
            try
            {
                string containerId = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID);

                if (string.IsNullOrWhiteSpace(containerId))
                {
                    return;
                }

                DockerContainerClient.LogDockerInformation($"Cleaning up Docker container: {containerId}");
                await this.dockerClient.StopContainerAsync(containerId, cancellationTokenSource.Token).ConfigureAwait(false);
                await this.dockerClient.RemoveContainerAsync(containerId, cancellationTokenSource.Token).ConfigureAwait(false);
                DockerContainerClient.LogDockerInformation($"Docker container cleanup completed.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Failed to cleanup Docker container: {ex.Message}");
            }
            finally
            {
                // Clear Docker environment variables
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PLATFORM, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_ARCH, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PACKAGES_HOST, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PACKAGES_MOUNT, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_LOGS_HOST, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_LOGS_MOUNT, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_STATE_HOST, null);
                Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_STATE_MOUNT, null);
            }
        }
    }
}
