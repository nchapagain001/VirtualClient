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

            string dockerExecCommand = $"docker";
            string dockerExecArgs = string.IsNullOrWhiteSpace(translatedWorkingDir)
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

            Dictionary<string, string> translations = new Dictionary<string, string>
            {
                { Environment.GetEnvironmentVariable("VC_DOCKER_PACKAGES_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_PACKAGES_MOUNT") },
                { Environment.GetEnvironmentVariable("VC_DOCKER_LOGS_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_LOGS_MOUNT") },
                { Environment.GetEnvironmentVariable("VC_DOCKER_STATE_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_STATE_MOUNT") }
            };

            foreach (var (hostPath, containerPath) in translations)
            {
                if (!string.IsNullOrEmpty(hostPath) && !string.IsNullOrEmpty(containerPath) && path.StartsWith(hostPath))
                {
                    return path.Replace(hostPath, containerPath).Replace('\\', '/');
                }
            }

            return path;
        }

        private string TranslateArguments(string arguments)
        {
            if (string.IsNullOrEmpty(arguments))
            {
                return arguments;
            }

            string result = arguments;

            Dictionary<string, string> translations = new Dictionary<string, string>
            {
                { Environment.GetEnvironmentVariable("VC_DOCKER_PACKAGES_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_PACKAGES_MOUNT") },
                { Environment.GetEnvironmentVariable("VC_DOCKER_LOGS_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_LOGS_MOUNT") },
                { Environment.GetEnvironmentVariable("VC_DOCKER_STATE_HOST"), Environment.GetEnvironmentVariable("VC_DOCKER_STATE_MOUNT") }
            };

            bool translated = false;

            foreach (var (hostPath, containerPath) in translations)
            {
                if (!string.IsNullOrEmpty(hostPath) && !string.IsNullOrEmpty(containerPath) && result.Contains(hostPath))
                {
                    result = result.Replace(hostPath, containerPath);
                    translated = true;
                }
            }

            if (translated)
            {
                result = result.Replace('\\', '/');
            }

            return result;
        }
    }
}