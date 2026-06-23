// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Common.Docker
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using VirtualClient.Common.Extensions;

    /// <summary>
    /// Manages Docker container operations: creation, execution, cleanup.
    /// </summary>
    public class DockerContainerClient
    {
        private const string DockerCommand = "docker";
        private readonly ILogger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DockerContainerClient"/> class.
        /// </summary>
        public DockerContainerClient(ILogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Checks if a Docker image exists locally.
        /// </summary>
        public async Task<bool> ImageExistsAsync(string imageName, CancellationToken cancellationToken)
        {
            try
            {
                DockerCommandResult result = await this.ExecuteDockerCommandAsync($"image inspect {imageName}", null, cancellationToken).ConfigureAwait(false);
                return result.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Builds a Docker image from a Dockerfile.
        /// </summary>
        public async Task BuildImageAsync(string buildContext, string dockerfileName, string imageName, CancellationToken cancellationToken)
        {
            string dockerfilePath = Path.Combine(buildContext, dockerfileName);
            string arguments = $"build -f \"{dockerfilePath}\" -t {imageName} \"{buildContext}\"";

            DockerCommandResult result = await this.ExecuteDockerCommandAsync(arguments, buildContext, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to build Docker image '{imageName}'. Error: {result.StandardError}");
            }
        }

        /// <summary>
        /// Checks if Docker is available and running on the system.
        /// </summary>
        public async Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken)
        {
            try
            {
                DockerCommandResult result = await this.ExecuteDockerCommandAsync("version", null, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0)
                {
                    return true;
                }

                // If regular docker command fails, try with sudo (for newly added docker group members)
                return await this.ExecuteDockerWithSudoAsync("version", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning($"Docker availability check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Inspects a Docker image and returns the container platform and architecture.
        /// </summary>
        public async Task<(PlatformID Platform, Architecture Architecture)> InspectImageAsync(
            string imageName,
            CancellationToken cancellationToken)
        {
            this.logger?.LogInformation($"Inspecting Docker image platform: {imageName}");

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to inspect Docker image '{imageName}'. Error: {result.StandardError}");
            }

            return DockerContainerClient.ParsePlatformFromInspectJson(result.StandardOutput);
        }

        /// <summary>
        /// Parses the output of 'docker image inspect' JSON to determine the container platform and architecture.
        /// </summary>
        public static (PlatformID Platform, Architecture Architecture) ParsePlatformFromInspectJson(string inspectJson)
        {
            JsonArray array;

            try
            {
                array = JsonNode.Parse(inspectJson)?.AsArray()
                    ?? throw new ArgumentException("Invalid docker inspect JSON output.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ArgumentException("Invalid docker inspect JSON output.", ex);
            }

            if (array.Count == 0)
            {
                throw new ArgumentException("Docker inspect output is empty.");
            }

            JsonNode root = array[0]
                ?? throw new ArgumentException("Docker inspect output is empty.");

            string os = root["Os"]?.GetValue<string>() ?? string.Empty;
            string arch = root["Architecture"]?.GetValue<string>() ?? string.Empty;
            string variant = root["Variant"]?.GetValue<string>() ?? string.Empty;

            PlatformID platform = os.ToLowerInvariant() switch
            {
                "linux" => PlatformID.Unix,
                "windows" => PlatformID.Win32NT,
                _ => throw new NotSupportedException($"Unsupported container OS: '{os}'")
            };

            Architecture architecture = arch.ToLowerInvariant() switch
            {
                "amd64" or "x86_64" => Architecture.X64,
                "arm64" or "aarch64" => Architecture.Arm64,
                "arm" when variant.ToLowerInvariant() == "v8" => Architecture.Arm64,
                "arm" => Architecture.Arm,
                "386" or "i386" => Architecture.X86,
                _ => throw new NotSupportedException($"Unsupported architecture: '{arch}'")
            };

            return (platform, architecture);
        }

        /// <summary>
        /// Creates a Docker container from the specified image with volume mounts.
        /// </summary>
        public async Task<string> CreateContainerAsync(
            string image,
            IDictionary<string, string> volumeMounts,
            IDictionary<string, string> environmentVariables,
            CancellationToken cancellationToken)
        {
            // Build docker run command with volume mounts and environment variables
            List<string> arguments = new List<string> { "run", "-d" };

            // Add volume mounts
            if (volumeMounts != null && volumeMounts.Count > 0)
            {
                foreach (KeyValuePair<string, string> mount in volumeMounts)
                {
                    arguments.Add("-v");
                    arguments.Add($"{mount.Key}:{mount.Value}");
                }
            }

            // Add environment variables
            if (environmentVariables != null && environmentVariables.Count > 0)
            {
                foreach (KeyValuePair<string, string> env in environmentVariables)
                {
                    arguments.Add("-e");
                    arguments.Add($"{env.Key}={env.Value}");
                }
            }

            // Keep container running by default (tail -f /dev/null)
            arguments.Add(image);
            arguments.Add("tail");
            arguments.Add("-f");
            arguments.Add("/dev/null");

            string argumentsString = string.Join(" ", arguments);
            this.logger?.LogInformation($"Creating Docker container: docker {argumentsString}");

            var result = await this.ExecuteDockerCommandAsync(argumentsString, null, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to create Docker container from image '{image}'. Error: {result.StandardError}");
            }

            string containerId = result.StandardOutput?.Trim();
            if (string.IsNullOrWhiteSpace(containerId))
            {
                throw new InvalidOperationException("Docker container creation succeeded but no container ID was returned.");
            }

            this.logger?.LogInformation($"Docker container created successfully. Container ID: {containerId}");
            return containerId;
        }

        /// <summary>
        /// Executes a command inside a running Docker container.
        /// </summary>
        public async Task<DockerExecResult> ExecuteInContainerAsync(
            string containerId,
            string command,
            CancellationToken cancellationToken)
        {
            string arguments = $"exec {containerId} {command}";

            this.logger?.LogInformation($"Executing command in container {containerId}: {command}");

            return new DockerExecResult
            {
                ExitCode = result.ExitCode,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError,
                Success = result.ExitCode == 0
            };
        }

        /// <summary>
        /// Stops a running Docker container.
        /// </summary>
        public async Task<bool> StopContainerAsync(string containerId, CancellationToken cancellationToken)
        {
            try
            {
                this.logger?.LogInformation($"Stopping Docker container: {containerId}");
                var result = await this.ExecuteDockerCommandAsync($"stop {containerId}", null, cancellationToken).ConfigureAwait(false);
                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {
                this.logger?.LogError($"Failed to stop Docker container {containerId}", ex);
                return false;
            }
        }

        /// <summary>
        /// Removes a Docker container.
        /// </summary>
        public async Task<bool> RemoveContainerAsync(string containerId, CancellationToken cancellationToken)
        {
            try
            {
                this.logger?.LogInformation($"Removing Docker container: {containerId}");
                var result = await this.ExecuteDockerCommandAsync($"rm {containerId}", null, cancellationToken).ConfigureAwait(false);
                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {
                this.logger?.LogError($"Failed to remove Docker container {containerId}", ex);
                return false;
            }
        }

        /// <summary>
        /// Executes a docker command and returns the result.
        /// </summary>
        private async Task<DockerCommandResult> ExecuteDockerCommandAsync(
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = DockerCommand,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            Process process = new Process { StartInfo = processInfo };

            try
            {
                process.Start();

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                // Wait for process exit asynchronously to satisfy AsyncFixer
                while (!process.HasExited)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                return new DockerCommandResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = stdoutTask.Result,
                    StandardError = stderrTask.Result
                };
            }
            finally
            {
                process?.Dispose();
            }
        }

        /// <summary>
        /// Executes a docker command with sudo and returns true if successful.
        /// </summary>
        private async Task<bool> ExecuteDockerWithSudoAsync(string arguments, CancellationToken cancellationToken)
        {
            try
            {
                ProcessStartInfo processInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"sudo docker {arguments}\"",
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

                    return process.ExitCode == 0;
                }
                finally
                {
                    process?.Dispose();
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Result of a Docker command execution.
        /// </summary>
        private class DockerCommandResult
        {
            /// <summary>
            /// Gets or sets the exit code from the command execution.
            /// </summary>
            public int ExitCode { get; set; }

            /// <summary>
            /// Gets or sets the standard output from the command.
            /// </summary>
            public string StandardOutput { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the standard error from the command.
            /// </summary>
            public string StandardError { get; set; } = string.Empty;
        }
    }

    /// <summary>
    /// Result of executing a command inside a Docker container.
    /// </summary>
    public class DockerExecResult
    {
        /// <summary>
        /// Gets or sets the exit code from the command execution.
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// Gets or sets the standard output from the command.
        /// </summary>
        public string StandardOutput { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the standard error from the command.
        /// </summary>
        public string StandardError { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the command succeeded (exit code 0).
        /// </summary>
        public bool Success { get; set; }
    }
}
