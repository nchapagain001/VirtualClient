// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Actions
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using VirtualClient.Common;
    using VirtualClient.Common.Docker;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;

    /// <summary>
    /// Executes child components within a Docker container environment.
    /// Component code runs on host, but all processes route to the container via DockerProcessManager.
    /// </summary>
    [SupportedPlatforms("linux-x64,linux-arm64")]
    internal class DockerExecution : VirtualClientComponentCollection
    {
        private ISystemManagement systemManager;
        private DockerContainerClient dockerClient;
        private string createdContainerId;

        /// <summary>
        /// Initializes a new instance of the <see cref="DockerExecution"/> class.
        /// </summary>
        /// <param name="dependencies">Provides all of the required dependencies to the Virtual Client component.</param>
        /// <param name="parameters">Parameters defined in the execution profile or supplied to the Virtual Client on the command line.</param>
        public DockerExecution(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters = null)
            : base(dependencies, parameters)
        {
            this.systemManager = dependencies.GetService<ISystemManagement>();
            this.dockerClient = new DockerContainerClient(this.Logger);
            this.createdContainerId = null;
        }

        /// <summary>
        /// The Docker image to use for container execution.
        /// </summary>
        public string Image
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(this.Image));
            }
        }

        /// <summary>
        /// Optional volume mount paths. Format: "/host/path:/container/path".
        /// </summary>
        public string VolumeMounts
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(this.VolumeMounts), string.Empty);
            }
        }

        /// <summary>
        /// Optional environment variables for the container. Format: "VAR1=value1,VAR2=value2".
        /// </summary>
        public string EnvironmentVariables
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(this.EnvironmentVariables), string.Empty);
            }
        }

        /// <summary>
        /// Initializes Docker container execution requirements.
        /// </summary>
        protected override Task InitializeAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            // Validate Docker image is specified
            if (string.IsNullOrWhiteSpace(this.Image))
            {
                throw new WorkloadException(
                    "Docker image (Image parameter) is required for DockerExecution.",
                    ErrorReason.InvalidProfileDefinition);
            }

            // Validate that at least one child component is defined
            if (this.Count == 0)
            {
                throw new WorkloadException(
                    "DockerExecution must contain at least one child component.",
                    ErrorReason.InvalidProfileDefinition);
            }

            return base.InitializeAsync(telemetryContext, cancellationToken);
        }

        /// <summary>
        /// Executes child components with their processes routed to the Docker container.
        /// </summary>
        protected override async Task ExecuteAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            // Check if container already exists (set by DockerCommand)
            string containerId = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID);

            if (string.IsNullOrWhiteSpace(containerId))
            {
                // Create a new container for this execution
                containerId = await this.CreateAndSetupContainerAsync(cancellationToken).ConfigureAwait(false);
                this.createdContainerId = containerId;
            }

            // Wrap ProcessManager with DockerProcessManager so child components' processes route to container
            ProcessManager currentProcessManager = this.Dependencies.GetService<ProcessManager>();
            ProcessManager wrappedProcessManager = new DockerProcessManager(currentProcessManager);

            // Replace ProcessManager in dependencies with the wrapped version
            this.Dependencies.AddSingleton<ProcessManager>(wrappedProcessManager);

            // Execute child components (their process calls will route to container via DockerProcessManager)
            foreach (VirtualClientComponent component in this)
            {
                await component.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Cleans up Docker container resources.
        /// </summary>
        protected override async Task CleanupAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            // Stop and remove container if we created it (don't cleanup if it was created by DockerCommand)
            if (!string.IsNullOrWhiteSpace(this.createdContainerId))
            {
                try
                {
                    // Execute the component's profile action inside the container via docker exec.
                    // The VirtualClient binary is expected to be volume-mounted at /app.
                    string command = $"/app/VirtualClient --profile=/app/profiles/{componentName}.json";

                    this.Logger?.LogInformation($"DockerExecution: Running docker exec: {command}");

                    DockerExecResult execResult = await this.dockerClient.ExecuteInContainerAsync(
                        containerId,
                        command,
                        cancellationToken).ConfigureAwait(false);

                    // Capture output for logging and telemetry
                    if (!execResult.Success)
                    {
                        this.Logger?.LogError(
                            $"DockerExecution: Component execution failed in container. " +
                            $"Component={componentName}, ExitCode={execResult.ExitCode}, " +
                            $"Error={execResult.StandardError}");

                        throw new WorkloadException(
                            $"Component {componentName} failed inside container. Exit code: {execResult.ExitCode}. " +
                            $"Error: {execResult.StandardError}",
                            ErrorReason.WorkloadFailed);
                    }

            // Parse volume mounts if provided
            Dictionary<string, string> volumeMounts = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(this.VolumeMounts))
            {
                foreach (string mount in this.VolumeMounts.Split(','))
                {
                    string[] parts = mount.Trim().Split(':');
                    if (parts.Length == 2)
                    {
                        volumeMounts[parts[0]] = parts[1];
                    }
                }
                catch (WorkloadException)
                {
                    throw;
                }
            }

            // Parse environment variables if provided
            Dictionary<string, string> envVars = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(this.EnvironmentVariables))
            {
                foreach (string envPair in this.EnvironmentVariables.Split(','))
                {
                    string[] parts = envPair.Trim().Split('=');
                    if (parts.Length == 2)
                    {
                        envVars[parts[0]] = parts[1];
                    }
                }
            }

            // Create container
            string containerId = await this.dockerClient.CreateContainerAsync(
                this.Image,
                volumeMounts,
                envVars,
                cancellationToken).ConfigureAwait(false);

            // Set environment variable for child components to route processes to container
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID, containerId);

            return containerId;
        }
    }
}
