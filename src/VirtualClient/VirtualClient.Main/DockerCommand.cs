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
                throw new ArgumentException("Docker image (--image) is required for docker command execution.");
            }

            // Validate that at least one profile is specified
            if (this.Profiles == null || !this.Profiles.Any())
            {
                throw new ArgumentException("At least one profile (--profile) is required for docker command execution.");
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

                // Step 2: Auto-build image if it is a vc- certified image and not found locally
                await this.EnsureImageExistsAsync(logger, cancellationToken).ConfigureAwait(false);

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

            try
            {
                await base.ExecuteAsync(Array.Empty<string>(), dependencies, cancellationTokenSource).ConfigureAwait(false);
                this.LogDockerInfo(logger, "Docker installation completed.");
            }
            finally
            {
                // Restore original profile
                if (originalProfile != null)
                {
                    this.Profiles = new List<DependencyProfileReference>
                    {
                        originalProfile
                    };
                }
                this.InstallDependencies = false;
            }

            // Start Docker daemon
            this.LogDockerInfo(logger, "Starting Docker daemon...");
            await this.StartDockerDaemonAsync(logger, cancellationTokenSource.Token).ConfigureAwait(false);

            // Verify Docker is now available
            await Task.Delay(2000, cancellationTokenSource.Token).ConfigureAwait(false);
            dockerAvailable = await this.dockerClient.IsDockerAvailableAsync(cancellationTokenSource.Token).ConfigureAwait(false);

            if (!dockerAvailable)
            {
                throw new InvalidOperationException(
                    "Docker installation completed but daemon is not running. " +
                    "Try running: sudo systemctl start docker");
            }
        }

        /// <summary>
        /// Starts the Docker daemon using systemctl.
        /// </summary>
        private async Task StartDockerDaemonAsync(ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                this.LogDockerInfo(logger, "Attempting to start Docker daemon...");

                ProcessStartInfo processInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "-c \"sudo -n systemctl start docker 2>&1 || sudo systemctl start docker 2>&1\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = new Process { StartInfo = processInfo })
                {
                    process.Start();
                    await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);

                    string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        this.LogDockerInfo(logger, $"Docker daemon output: {output}");
                    }

                    if (!string.IsNullOrWhiteSpace(error) && process.ExitCode != 0)
                    {
                        this.LogDockerInfo(logger, $"Warning: Docker daemon startup output: {error}");
                    }

                    if (process.ExitCode != 0)
                    {
                        this.LogDockerInfo(logger, $"Warning: Failed to start Docker daemon (exit code {process.ExitCode}). Docker may already be running.");
                    }
                    else
                    {
                        this.LogDockerInfo(logger, "Docker daemon started successfully.");
                    }
                }

                // Wait longer for docker socket to be available
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Exception starting Docker daemon: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures the Docker image exists locally. Auto-builds vc- certified images if not found.
        /// </summary>
        private async Task EnsureImageExistsAsync(ILogger logger, CancellationToken cancellationToken)
        {
            if (!this.DockerImage.StartsWith("vc-", StringComparison.OrdinalIgnoreCase))
                return;

            bool exists = await this.dockerClient.ImageExistsAsync(this.DockerImage, cancellationToken).ConfigureAwait(false);

            if (exists)
            {
                this.LogDockerInfo(logger, $"Image found locally: {this.DockerImage}");
                return;
            }

            this.LogDockerInfo(logger, $"Image '{this.DockerImage}' not found locally. Building...");

            string imagesDirectory = this.GetImagesDirectory();
            string dockerfileName = this.GetDockerfileForImage(this.DockerImage);

            this.LogDockerInfo(logger, $"Building from: {Path.Combine(imagesDirectory, dockerfileName)}");

            await this.dockerClient.BuildImageAsync(imagesDirectory, dockerfileName, this.DockerImage, cancellationToken).ConfigureAwait(false);

            this.LogDockerInfo(logger, $"Image '{this.DockerImage}' built successfully.");
        }

        /// <summary>
        /// Maps a vc- image name to its Dockerfile filename by discovering Dockerfiles in the images directory.
        /// Naming convention: Dockerfile.{suffix} → vc-{suffix}:latest (or vc-{os}:{version} if hyphen-separated).
        /// </summary>
        private string GetDockerfileForImage(string imageName)
        {
            string imagesDirectory = this.GetImagesDirectory();
            Dictionary<string, string> imageToDockerfile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Discover all Dockerfiles and map them to image names
            string[] dockerfiles = Directory.GetFiles(imagesDirectory, "Dockerfile.*");

            foreach (string dockerfilePath in dockerfiles)
            {
                string dockerfileName = Path.GetFileName(dockerfilePath);
                string suffix = dockerfileName.Substring("Dockerfile.".Length);

                // Generate image name from suffix
                // Pattern: Dockerfile.ubuntu-noble → vc-ubuntu:noble, Dockerfile.alpine → vc-alpine:latest
                string generatedImageName = this.GenerateImageNameFromDockerfileSuffix(suffix);
                imageToDockerfile[generatedImageName] = dockerfileName;
            }

            if (!imageToDockerfile.TryGetValue(imageName, out string dockerfileFileName))
            {
                string supportedImages = string.Join(", ", imageToDockerfile.Keys);
                throw new NotSupportedException(
                    $"No Dockerfile found for image '{imageName}'. Supported images: {supportedImages}");
            }

            return dockerfileFileName;
        }

        /// <summary>
        /// Generates an image name from a Dockerfile suffix using the naming convention.
        /// Examples: "ubuntu-noble" → "vc-ubuntu:noble", "alpine" → "vc-alpine:latest"
        /// </summary>
        private string GenerateImageNameFromDockerfileSuffix(string suffix)
        {
            // If the suffix contains a hyphen, split at the first hyphen to separate OS and version
            int hyphenIndex = suffix.IndexOf('-');
            if (hyphenIndex > 0)
            {
                string os = suffix.Substring(0, hyphenIndex);
                string version = suffix.Substring(hyphenIndex + 1);
                return $"vc-{os}:{version}";
            }

            // No hyphen: use "latest" as the tag
            return $"vc-{suffix}:latest";
        }

        /// <summary>
        /// Returns the path to the images directory alongside the binary.
        /// </summary>
        private string GetImagesDirectory()
        {
            string imagesDir = Path.Combine(AppContext.BaseDirectory, "images");

            if (!Directory.Exists(imagesDir))
            {
                throw new DirectoryNotFoundException(
                    $"Images directory not found: {imagesDir}. " +
                    $"Ensure the images/ folder is present alongside the VirtualClient binary.");
            }

            return imagesDir;
        }

        /// <summary>
        /// Validates that none of the profile actions use DockerExecution, which conflicts with the docker subcommand.
        /// </summary>
        private async Task ValidateProfileForDockerSubcommandAsync(IServiceCollection dependencies, CancellationToken cancellationToken)
        {
            IEnumerable<string> profilePaths = await this.EvaluateProfilesAsync(dependencies);

            foreach (string profilePath in profilePaths)
            {
                if (!File.Exists(profilePath))
                    continue;

                string json = await File.ReadAllTextAsync(profilePath, cancellationToken).ConfigureAwait(false);
                JsonNode root = JsonNode.Parse(json);
                JsonArray actions = root?["Actions"]?.AsArray();

                if (actions == null)
                    continue;

                foreach (JsonNode action in actions)
                {
                    string type = action?["Type"]?.GetValue<string>() ?? string.Empty;
                    if (type.Equals("DockerExecution", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Profile '{Path.GetFileName(profilePath)}' contains a 'DockerExecution' component " +
                            $"which conflicts with the 'docker' subcommand (double Docker). " +
                            $"Remove DockerExecution from the profile when using the docker subcommand.");
                    }
                }
            }
        }


        /// <summary>
        /// Prepares volume mounts for container using platform-specific directories.
        /// </summary>
        private Dictionary<string, string> PrepareVolumeMounts(ILogger logger, PlatformSpecifics platformSpecifics)
        {
            Dictionary<string, string> volumeMounts = new Dictionary<string, string>();
            string currentDirectory = platformSpecifics.CurrentDirectory;

            // Standard mount points - use PlatformSpecifics for consistent paths
            volumeMounts[Path.Combine(currentDirectory, "packages")] = "/mnt/packages";
            volumeMounts[Path.Combine(currentDirectory, "logs")] = "/mnt/logs";
            volumeMounts[platformSpecifics.StateDirectory] = "/mnt/state";

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
        /// Logs docker-related information with docker color (blue).
        /// </summary>
        private void LogDockerInfo(ILogger logger, string message)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            logger?.LogInformation(message);
            Console.ResetColor();
        }
    }
}
