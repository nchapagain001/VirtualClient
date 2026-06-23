// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
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
    /// Re-instantiates components with platform-specific service implementations based on container platform.
    /// </summary>
    [SupportedPlatforms("linux-x64,linux-arm64,win-x64,win-arm64")]
    internal class DockerExecution : VirtualClientComponentCollection
    {
        private DockerContainerClient dockerClient;
        private string createdContainerId;
        private PlatformID containerPlatform;
        private Architecture containerArchitecture;

        /// <summary>
        /// Initializes a new instance of the <see cref="DockerExecution"/> class.
        /// </summary>
        public DockerExecution(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters = null)
            : base(dependencies, parameters)
        {
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
        /// Initializes Docker container execution requirements.
        /// </summary>
        protected override Task InitializeAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(this.Image))
            {
                throw new WorkloadException(
                    "Docker image (Image parameter) is required for DockerExecution.",
                    ErrorReason.InvalidProfileDefinition);
            }

            if (this.Count == 0)
            {
                throw new WorkloadException(
                    "DockerExecution must contain at least one child component.",
                    ErrorReason.InvalidProfileDefinition);
            }

            return base.InitializeAsync(telemetryContext, cancellationToken);
        }

        /// <summary>
        /// Executes child components with re-instantiation for the detected container platform.
        /// </summary>
        protected override async Task ExecuteAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            IServiceProvider serviceProvider = this.Dependencies.BuildServiceProvider();
            ILogger logger = serviceProvider.GetService<ILogger>();
            this.dockerClient = new DockerContainerClient(logger);

            // Check if container already exists (set by DockerCommand)
            string containerId = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID);

            if (string.IsNullOrWhiteSpace(containerId))
            {
                // Create a new container for this execution
                containerId = await this.CreateAndSetupContainerAsync(logger, cancellationToken).ConfigureAwait(false);
                this.createdContainerId = containerId;
            }
            else
            {
                // Container was created by DockerCommand, get its platform info
                await this.DetectContainerPlatformAsync(cancellationToken).ConfigureAwait(false);
            }

            // Re-instantiate and execute child components with platform-appropriate services
            foreach (VirtualClientComponent component in this)
            {
                if (!VirtualClientComponent.IsSupported(component))
                {
                    logger?.LogInformation(
                        $"DockerExecution: Skipping component '{component.TypeName}' - not supported for container platform {this.containerPlatform}-{this.containerArchitecture}");
                    continue;
                }

                // Re-instantiate the component with container platform services
                VirtualClientComponent reInstantiatedComponent = await this.ReInstantiateComponentAsync(
                    component, logger, cancellationToken).ConfigureAwait(false);

                await reInstantiatedComponent.ExecuteAsync(cancellationToken).ConfigureAwait(false);

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
            if (!string.IsNullOrWhiteSpace(this.createdContainerId))
            {
                try
                {
                    await this.dockerClient.StopContainerAsync(this.createdContainerId, cancellationToken).ConfigureAwait(false);
                    await this.dockerClient.RemoveContainerAsync(this.createdContainerId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.Logger?.LogWarning($"Failed to cleanup Docker container {this.createdContainerId}: {ex.Message}");
                }
            }

            await base.CleanupAsync(telemetryContext, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a Docker container and detects its platform.
        /// </summary>
        private async Task<string> CreateAndSetupContainerAsync(ILogger logger, CancellationToken cancellationToken)
        {
            // Check if image exists, attempt to pull if missing
            bool imageExists = await this.dockerClient.ImageExistsAsync(this.Image, cancellationToken).ConfigureAwait(false);
            if (!imageExists)
            {
                logger?.LogInformation($"Docker image '{this.Image}' not found locally. Attempting to pull from Docker Hub...");
                try
                {
                    await this.PullDockerImageAsync(this.Image, cancellationToken).ConfigureAwait(false);
                    imageExists = await this.dockerClient.ImageExistsAsync(this.Image, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"Failed to pull Docker image: {ex.Message}");
                }
            }

            if (!imageExists)
            {
                throw new InvalidOperationException(
                    $"Docker image '{this.Image}' not found. Please pull the image manually: docker pull {this.Image}");
            }

            // Detect container platform
            await this.DetectContainerPlatformAsync(cancellationToken).ConfigureAwait(false);

            // Prepare volume mounts mapping host directories to container paths
            PlatformSpecifics platformSpecifics = this.PlatformSpecifics;
            Dictionary<string, string> volumeMounts = new Dictionary<string, string>
            {
                { platformSpecifics.PackagesDirectory, "/mnt/packages" },
                { platformSpecifics.LogsDirectory, "/mnt/logs" },
                { platformSpecifics.StateDirectory, "/mnt/state" }
            };

            // Create container
            string containerId = await this.dockerClient.CreateContainerAsync(
                this.Image,
                volumeMounts,
                null,
                cancellationToken).ConfigureAwait(false);

            // Set environment variables for process routing and platform detection
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_CONTAINER_ID, containerId);
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PLATFORM, this.containerPlatform.ToString());
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_ARCH, this.containerArchitecture.ToString());
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_IMAGE, this.Image);

            // Set path translation variables for DockerProcessManager
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PACKAGES_HOST, platformSpecifics.PackagesDirectory);
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PACKAGES_MOUNT, "/mnt/packages");
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_LOGS_HOST, platformSpecifics.LogsDirectory);
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_LOGS_MOUNT, "/mnt/logs");
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_STATE_HOST, platformSpecifics.StateDirectory);
            Environment.SetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_STATE_MOUNT, "/mnt/state");

            logger?.LogInformation($"Docker container created: {containerId} ({this.containerPlatform}-{this.containerArchitecture})");
            return containerId;
        }

        /// <summary>
        /// Detects the container platform and architecture.
        /// </summary>
        private async Task DetectContainerPlatformAsync(CancellationToken cancellationToken)
        {
            (PlatformID platform, Architecture arch) = await this.dockerClient.InspectImageAsync(
                this.Image, cancellationToken).ConfigureAwait(false);

            this.containerPlatform = platform;
            this.containerArchitecture = arch;
        }

        /// <summary>
        /// Re-instantiates a component with container platform-appropriate services.
        /// </summary>
        private Task<VirtualClientComponent> ReInstantiateComponentAsync(
            VirtualClientComponent originalComponent, ILogger logger, CancellationToken cancellationToken)
        {
            // Get the original SystemManagement from parent dependencies before we create the new collection
            IServiceProvider originalProvider = this.Dependencies.BuildServiceProvider();
            ISystemManagement originalSystemManagement = originalProvider.GetService<ISystemManagement>();

            // Create a new ServiceCollection that copies services from parent with ProcessManager override
            IServiceCollection containerServices = new ServiceCollection();

            // Copy all registrations from parent EXCEPT ProcessManager (we'll create a new one)
            foreach (ServiceDescriptor descriptor in this.Dependencies)
            {
                // Skip only ProcessManager - we'll create a container-specific version
                // ISystemManagement will be re-registered with new ProcessManager below
                if (descriptor.ServiceType == typeof(ProcessManager))
                {
                    continue;
                }

                containerServices.Add(descriptor);
            }

            // Register new ProcessManager wrapped with DockerProcessManager
            ProcessManager hostProcessManager = ProcessManager.Create(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? PlatformID.Win32NT : PlatformID.Unix);
            ProcessManager dockerAwareProcessManager = new DockerProcessManager(hostProcessManager);
            containerServices.AddSingleton<ProcessManager>(dockerAwareProcessManager);

            // Override SystemManagement in the new collection to use the new DockerProcessManager
            // Get the descriptor and remove it, then add a new one with updated ProcessManager
            foreach (ServiceDescriptor descriptor in this.Dependencies)
            {
                if (descriptor.ServiceType == typeof(ISystemManagement))
                {
                    // Remove the old ISystemManagement descriptor from the new collection
                    int indexToRemove = containerServices.ToList().FindIndex(d => d.ServiceType == typeof(ISystemManagement));
                    if (indexToRemove >= 0)
                    {
                        // Create a modified copy of the original SystemManagement with new ProcessManager
                        if (originalSystemManagement is SystemManagement originalSysMgmt)
                        {
                            // Set the ProcessManager on the original instance to the docker-aware version
                            originalSysMgmt.ProcessManager = dockerAwareProcessManager;
                            // Re-register it
                            containerServices.AddSingleton<ISystemManagement>(originalSystemManagement);
                        }
                    }

                    break;
                }
            }

            // Re-instantiate the component with new services
            VirtualClientComponent newComponent = (VirtualClientComponent)Activator.CreateInstance(
                originalComponent.GetType(),
                containerServices,
                originalComponent.Parameters);

            return Task.FromResult(newComponent);
        }

        /// <summary>
        /// Pulls a Docker image from Docker Hub.
        /// </summary>
        private async Task PullDockerImageAsync(string imageName, CancellationToken cancellationToken)
        {
            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"pull {imageName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process process = new Process { StartInfo = processInfo };

            try
            {
                process.Start();

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                while (!process.HasExited)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Failed to pull Docker image '{imageName}'. Error: {stderrTask.Result}");
                }
            }
            finally
            {
                process?.Dispose();
            }
        }
    }
}
