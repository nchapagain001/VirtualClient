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
            // DockerExecution will manage container creation, platform detection, and cleanup.
            this.WrapProfileActionsWithDockerExecution(platformSpecifics);

            // Execute profile normally. Base class handles:
            // 1. Dependency installation on host
            // 2. DockerExecution initialization (creates container, detects platform)
            // 3. Child action re-instantiation and execution within container
            // 4. DockerExecution cleanup
            int exitCode = await base.ExecuteAsync(args, dependencies, cancellationTokenSource).ConfigureAwait(false);

            return exitCode;
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

            // Create a new Actions array with DockerExecution wrapping the original actions
            JsonArray newActions = new JsonArray();

            JsonObject dockerExecutionAction = new JsonObject
            {
                ["Type"] = "DockerExecution",
                ["Parameters"] = new JsonObject
                {
                    ["Image"] = this.DockerImage
                },
                ["Components"] = actionsArray.DeepClone() as JsonArray
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
    }
}
