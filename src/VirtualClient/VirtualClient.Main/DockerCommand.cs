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
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
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

            // Load and wrap profile before execution
            DependencyProfileReference profileRef = this.Profiles.First();
            string profilePath = Path.Combine(platformSpecifics.ProfilesDirectory, profileRef.ProfileName);

            if (!File.Exists(profilePath))
            {
                throw new WorkloadException(
                    $"Profile not found at path: {profilePath}",
                    ErrorReason.InvalidProfileDefinition);
            }

            ExecutionProfile profile = await ExecutionProfile.ReadProfileAsync(profilePath).ConfigureAwait(false);
            ExecutionProfile wrappedProfile = this.WrapProfileActionsWithDockerExecution(profile);

            // Save wrapped profile
            string tempProfileName = $"{Path.GetFileNameWithoutExtension(profileRef.ProfileName)}_DockerExecution_wrapped.json";
            string tempProfilePath = Path.Combine(platformSpecifics.ProfilesDirectory, tempProfileName);

            string wrappedJson = JsonConvert.SerializeObject(wrappedProfile, Formatting.Indented);
            File.WriteAllText(tempProfilePath, wrappedJson);

            // Update this.Profiles to use the wrapped profile
            this.Profiles = new List<DependencyProfileReference>
            {
                new DependencyProfileReference(tempProfileName)
            };

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
        /// Wraps the loaded profile's dependencies and actions in separate DockerExecution components.
        /// Dependencies run in the first DockerExecution (creating the container).
        /// Actions run in the second DockerExecution (reusing the same container).
        /// </summary>
        protected ExecutionProfile WrapProfileActionsWithDockerExecution(ExecutionProfile profile)
        {
            if (profile == null || (profile.Dependencies == null && profile.Actions == null))
            {
                return profile;
            }

            this.ValidateProfileForDocker(profile);

            // Create Docker parameters shared by both wrappers
            IDictionary<string, IConvertible> dockerParameters = new Dictionary<string, IConvertible>
            {
                ["Image"] = this.DockerImage,
                ["DeferContainerCleanup"] = true
            };

            // Wrap dependencies and actions separately
            List<ExecutionProfileElement> wrappedDependencies = new List<ExecutionProfileElement>();
            List<ExecutionProfileElement> wrappedActions = new List<ExecutionProfileElement>();

            if (profile.Dependencies?.Any() == true)
            {
                ExecutionProfileElement dependencyWrapper = new ExecutionProfileElement(
                    type: "DockerExecution",
                    parameters: dockerParameters,
                    components: profile.Dependencies);

                wrappedDependencies.Add(dependencyWrapper);
            }

            if (profile.Actions?.Any() == true)
            {
                ExecutionProfileElement actionWrapper = new ExecutionProfileElement(
                    type: "DockerExecution",
                    parameters: dockerParameters,
                    components: profile.Actions);

                wrappedActions.Add(actionWrapper);
            }

            // Create new profile with wrapped components
            ExecutionProfile wrappedProfile = new ExecutionProfile(
                description: profile.Description,
                minimumExecutionInterval: profile.MinimumExecutionInterval,
                actions: wrappedActions,
                dependencies: wrappedDependencies,
                monitors: profile.Monitors,
                metadata: profile.Metadata,
                parameters: profile.Parameters,
                parametersOn: profile.ParametersOn);

            return wrappedProfile;
        }

        /// <summary>
        /// Validates that the profile does not already contain DockerExecution components.
        /// Throws an exception if DockerExecution is found in dependencies, actions, or monitors.
        /// </summary>
        protected void ValidateProfileForDocker(ExecutionProfile profile)
        {
            bool hasDockerExecution = (profile.Dependencies?.Any(d =>
                    string.Equals(d.Type, "DockerExecution", StringComparison.OrdinalIgnoreCase)) == true) ||
                   (profile.Actions?.Any(a =>
                    string.Equals(a.Type, "DockerExecution", StringComparison.OrdinalIgnoreCase)) == true) ||
                   (profile.Monitors?.Any(m =>
                    string.Equals(m.Type, "DockerExecution", StringComparison.OrdinalIgnoreCase)) == true);

            if (hasDockerExecution)
            {
                string errorMessage =
                    $"The provided profile already contains a 'DockerExecution' component. " +
                    $"The 'docker' subcommand wraps profile components in DockerExecution automatically. " +
                    $"Either use the 'docker' subcommand with a standard profile (e.g. GET-STARTED-OPENSSL.json), " +
                    $"or run the profile directly without the 'docker' subcommand (e.g. VirtualClient.exe --profile=GET-STARTED-OPENSSL-DOCKER.json).";

                ConsoleLogger.Default.LogMessage(errorMessage, EventContext.Persisted());

                throw new WorkloadException(errorMessage, ErrorReason.InvalidProfileDefinition);
            }
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
