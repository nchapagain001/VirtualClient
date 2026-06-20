// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Contracts
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Wraps PlatformSpecifics to transparently return Docker container platform/architecture
    /// when VC_DOCKER_PLATFORM and VC_DOCKER_ARCH environment variables are set.
    /// </summary>
    public class DockerAwarePlatformSpecifics : PlatformSpecifics
    {
        private readonly PlatformSpecifics innerPlatformSpecifics;

        /// <summary>
        /// Initializes a new instance of the <see cref="DockerAwarePlatformSpecifics"/> class.
        /// </summary>
        /// <param name="innerPlatformSpecifics">The underlying PlatformSpecifics to delegate to.</param>
        public DockerAwarePlatformSpecifics(PlatformSpecifics innerPlatformSpecifics)
            : base(innerPlatformSpecifics.Platform, innerPlatformSpecifics.CpuArchitecture, innerPlatformSpecifics.CurrentDirectory)
        {
            this.innerPlatformSpecifics = innerPlatformSpecifics ?? throw new ArgumentNullException(nameof(innerPlatformSpecifics));
        }

        /// <inheritdoc />
        public override PlatformID Platform
        {
            get
            {
                string containerPlatform = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PLATFORM);
                if (!string.IsNullOrWhiteSpace(containerPlatform) && Enum.TryParse<PlatformID>(containerPlatform, out var platform))
                {
                    return platform;
                }

                return this.innerPlatformSpecifics.Platform;
            }
        }

        /// <inheritdoc />
        public override Architecture CpuArchitecture
        {
            get
            {
                string containerArch = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_ARCH);
                if (!string.IsNullOrWhiteSpace(containerArch) && Enum.TryParse<Architecture>(containerArch, out var arch))
                {
                    return arch;
                }

                return this.innerPlatformSpecifics.CpuArchitecture;
            }
        }

        /// <inheritdoc />
        public override string PlatformArchitectureName
        {
            get
            {
                string containerPlatform = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_PLATFORM);
                string containerArch = Environment.GetEnvironmentVariable(EnvironmentVariable.VC_DOCKER_ARCH);

                if (!string.IsNullOrWhiteSpace(containerPlatform) && !string.IsNullOrWhiteSpace(containerArch))
                {
                    if (Enum.TryParse<PlatformID>(containerPlatform, out var platform) &&
                        Enum.TryParse<Architecture>(containerArch, out var arch))
                    {
                        return PlatformSpecifics.GetPlatformArchitectureName(platform, arch);
                    }
                }

                return this.innerPlatformSpecifics.PlatformArchitectureName;
            }
        }
    }
}
