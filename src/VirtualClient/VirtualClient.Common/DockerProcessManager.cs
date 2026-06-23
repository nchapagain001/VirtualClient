// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Common
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using VirtualClient.Common.Extensions;

    /// <summary>
    /// Wraps a ProcessManager to transparently route command execution to a Docker container
    /// when the VC_DOCKER_CONTAINER_ID environment variable is set.
    /// </summary>
    public class DockerProcessManager : ProcessManager
    {
        private readonly ProcessManager innerProcessManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="DockerProcessManager"/> class.
        /// </summary>
        /// <param name="innerProcessManager">The underlying ProcessManager to delegate to.</param>
        public DockerProcessManager(ProcessManager innerProcessManager)
        {
            this.innerProcessManager = innerProcessManager ?? throw new ArgumentNullException(nameof(innerProcessManager));
        }

        /// <inheritdoc />
        public override PlatformID Platform => this.innerProcessManager.Platform;

        /// <inheritdoc />
        public override IProcessProxy CreateProcess(string command, string arguments = null, string workingDir = null)
        {
            command.ThrowIfNullOrWhiteSpace(nameof(command));

            string containerId = Environment.GetEnvironmentVariable("VC_DOCKER_CONTAINER_ID");

            if (string.IsNullOrWhiteSpace(containerId))
            {
                return this.innerProcessManager.CreateProcess(command, arguments, workingDir);
            }

            string translatedCommand = this.TranslatePath(command);
            string translatedWorkingDir = this.TranslatePath(workingDir);
            string translatedArguments = this.TranslateArguments(arguments);

            // docker exec -w requires a valid Unix absolute path (starts with /).
            // If translated working directory is not a valid Unix path, don't use -w flag.
            bool isValidUnixPath = !string.IsNullOrEmpty(translatedWorkingDir) && translatedWorkingDir.StartsWith("/");

            string dockerExecCommand = $"docker";
            string dockerExecArgs = !isValidUnixPath
                ? $"exec {containerId} {translatedCommand}"
                : $"exec -w {translatedWorkingDir} {containerId} {translatedCommand}";

            if (!string.IsNullOrWhiteSpace(translatedArguments))
            {
                dockerExecArgs = $"{dockerExecArgs} {translatedArguments}";
            }

            return this.innerProcessManager.CreateProcess(dockerExecCommand, dockerExecArgs, workingDir);
        }

        /// <inheritdoc />
        public override IProcessProxy GetProcess(int processId)
        {
            return this.innerProcessManager.GetProcess(processId);
        }

        /// <inheritdoc />
        public override IEnumerable<IProcessProxy> GetProcesses(string processName)
        {
            return this.innerProcessManager.GetProcesses(processName);
        }

        private string TranslatePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            // Normalize separators for comparison — env vars may use backslashes while paths may use forward slashes
            string normalizedPath = path.Replace('\\', '/');

            Dictionary<string, string> translations = new Dictionary<string, string>
            {
                { Environment.GetEnvironmentVariable("VC_DOCKER_PACKAGES_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_PACKAGES_MOUNT") },
                { Environment.GetEnvironmentVariable("VC_DOCKER_LOGS_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_LOGS_MOUNT") },
                { Environment.GetEnvironmentVariable("VC_DOCKER_STATE_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_STATE_MOUNT") }
            };

            foreach (var (hostPath, containerPath) in translations)
            {
                if (!string.IsNullOrEmpty(hostPath) && !string.IsNullOrEmpty(containerPath))
                {
                    string normalizedHostPath = hostPath.Replace('\\', '/');
                    if (normalizedPath.StartsWith(normalizedHostPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return normalizedPath.Replace(normalizedHostPath, containerPath);
                    }
                }
            }

            return normalizedPath;
        }

        private string TranslateArguments(string arguments)
        {
            if (string.IsNullOrEmpty(arguments))
            {
                return arguments;
            }

            // Normalize separators for comparison — env vars may use backslashes while arguments may use forward slashes
            string result = arguments.Replace('\\', '/');

            Dictionary<string, string> translations = new Dictionary<string, string>
            {
                { Environment.GetEnvironmentVariable("VC_DOCKER_PACKAGES_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_PACKAGES_MOUNT") },
                { Environment.GetEnvironmentVariable("VC_DOCKER_LOGS_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_LOGS_MOUNT") },
                { Environment.GetEnvironmentVariable("VC_DOCKER_STATE_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_STATE_MOUNT") }
            };

            foreach (var (hostPath, containerPath) in translations)
            {
                if (!string.IsNullOrEmpty(hostPath) && !string.IsNullOrEmpty(containerPath))
                {
                    string normalizedHostPath = hostPath.Replace('\\', '/');
                    if (result.Contains(normalizedHostPath, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result.Replace(normalizedHostPath, containerPath, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            return result;
        }
    }
}