// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Moq;
    using Newtonsoft.Json;
    using NUnit.Framework;
    using VirtualClient.Common.Docker;
    using VirtualClient.Contracts;

    [TestFixture]
    [Category("Unit")]
    public class DockerCommandTests
    {
        private MockFixture mockFixture;
        private TestDockerCommand command;

        [SetUp]
        public void SetupDefaults()
        {
            this.mockFixture = new MockFixture();

            this.command = new TestDockerCommand
            {
                ClientId = "AnyAgent",
                Verbose = false,
                Timeout = ProfileTiming.OneIteration(),
                ExecutionSystem = "AnySystem",
                ExperimentId = Guid.NewGuid().ToString(),
                Profiles = new List<DependencyProfileReference>(),
                InstallDependencies = false,
                DockerImage = "ubuntu:noble",
                KeepContainerAlive = false
            };
        }

        #region Command-Line Argument Tests

        [Test]
        [TestCase("ubuntu:noble")]
        [TestCase("redis:7.0-alpine")]
        [TestCase("debian:bookworm")]
        [TestCase("mcr.microsoft.com/windows/servercore:ltsc2022")]
        public void DockerCommandAcceptsValidDockerImageArguments(string dockerImage)
        {
            this.command.DockerImage = dockerImage;
            this.command.Profiles = new List<DependencyProfileReference>
            {
                new DependencyProfileReference("PROFILE.json")
            };

            // Should not throw
            this.command.CallInitialize(
                new string[] { },
                this.mockFixture.PlatformSpecifics);

            Assert.AreEqual(dockerImage, this.command.DockerImage);
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void DockerCommandRequiresValidDockerImageArgument(string invalidImage)
        {
            this.command.DockerImage = invalidImage;
            this.command.Profiles = new List<DependencyProfileReference>
            {
                new DependencyProfileReference("PROFILE.json")
            };

            WorkloadException exception = Assert.Throws<WorkloadException>(
                () => this.command.CallInitialize(
                    new string[] { },
                    this.mockFixture.PlatformSpecifics));

            Assert.IsNotEmpty(exception.Message);
            Assert.That(exception.Message.Contains("Docker image", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void DockerCommandRequiresAtLeastOneProfileArgument()
        {
            this.command.DockerImage = "ubuntu:noble";
            this.command.Profiles = new List<DependencyProfileReference>();

            WorkloadException exception = Assert.Throws<WorkloadException>(
                () => this.command.CallInitialize(
                    new string[] { },
                    this.mockFixture.PlatformSpecifics));

            Assert.IsNotEmpty(exception.Message);
            Assert.That(exception.Message.Contains("profile", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void DockerCommandRequiresAtLeastOneProfileArgumentWhenNull()
        {
            this.command.DockerImage = "ubuntu:noble";
            this.command.Profiles = null;

            WorkloadException exception = Assert.Throws<WorkloadException>(
                () => this.command.CallInitialize(
                    new string[] { },
                    this.mockFixture.PlatformSpecifics));

            Assert.IsNotEmpty(exception.Message);
            Assert.That(exception.Message.Contains("profile", StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region Docker Image Parameter Variations

        [Test]
        [TestCase("ubuntu:noble", "ubuntu", "noble")]
        [TestCase("redis:7.0-alpine", "redis", "7.0-alpine")]
        [TestCase("python:3.11-slim-bullseye", "python", "3.11-slim-bullseye")]
        public void DockerImageParameterIsPreservedInWrappedProfile(string dockerImage, string imageName, string tag)
        {
            this.command.DockerImage = dockerImage;

            ExecutionProfileElement dependency = new ExecutionProfileElement(
                type: "ComponentInstallation",
                parameters: new Dictionary<string, IConvertible> { ["Scenario"] = "Install" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: new List<ExecutionProfileElement> { dependency },
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            ExecutionProfileElement wrappedDependency = wrappedProfile.Dependencies.First();
            Assert.AreEqual(dockerImage, wrappedDependency.Parameters["Image"]);
        }

        #endregion

        #region Profile Wrapping Tests

        [Test]
        public void WrapProfileActionsWithDockerExecutionHandlesEmptyButNonNullDependencies()
        {
            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: new List<ExecutionProfileElement>(),
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            Assert.IsNotNull(wrappedProfile);
            Assert.That(wrappedProfile.Dependencies?.Count() ?? 0, Is.EqualTo(0));
        }

        [Test]
        public void WrapProfileActionsWithDockerExecutionHandlesEmptyButNonNullActions()
        {
            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: new List<ExecutionProfileElement>(),
                dependencies: null,
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            Assert.IsNotNull(wrappedProfile);
            Assert.That(wrappedProfile.Actions?.Count() ?? 0, Is.EqualTo(0));
        }

        [Test]
        public void WrapProfileActionsWithDockerExecutionHandlesEmptyProfile()
        {
            ExecutionProfile profile = new ExecutionProfile(
                description: "Empty Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: null,
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            Assert.IsNotNull(wrappedProfile);
            Assert.AreEqual("Empty Profile", wrappedProfile.Description);
        }

        [Test]
        public void WrapProfileActionsWithDockerExecutionReturnsNullForNullProfile()
        {
            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(null);

            Assert.IsNull(wrappedProfile);
        }

        [Test]
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(5)]
        public void WrapProfileActionsWithDockerExecutionWrapsMultipleDependencies(int dependencyCount)
        {
            List<ExecutionProfileElement> dependencies = new List<ExecutionProfileElement>();
            for (int i = 0; i < dependencyCount; i++)
            {
                dependencies.Add(new ExecutionProfileElement(
                    type: $"Component{i}",
                    parameters: new Dictionary<string, IConvertible> { ["Index"] = i.ToString() },
                    components: null));
            }

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: dependencies,
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            Assert.That(wrappedProfile.Dependencies.Count(), Is.EqualTo(1));
            ExecutionProfileElement wrapper = wrappedProfile.Dependencies.First();
            Assert.That(wrapper.Components.Count(), Is.EqualTo(dependencyCount));
        }

        [Test]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        public void WrapProfileActionsWithDockerExecutionWrapsMultipleActions(int actionCount)
        {
            List<ExecutionProfileElement> actions = new List<ExecutionProfileElement>();
            for (int i = 0; i < actionCount; i++)
            {
                actions.Add(new ExecutionProfileElement(
                    type: $"Action{i}",
                    parameters: new Dictionary<string, IConvertible> { ["Index"] = i.ToString() },
                    components: null));
            }

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: actions,
                dependencies: null,
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            Assert.That(wrappedProfile.Actions.Count(), Is.EqualTo(1));
            ExecutionProfileElement wrapper = wrappedProfile.Actions.First();
            Assert.That(wrapper.Components.Count(), Is.EqualTo(actionCount));
        }

        #endregion

        #region Profile Validation Tests - Double Docker Detection

        [Test]
        public void ValidateProfileForDockerThrowsWhenDockerExecutionInDependencies()
        {
            ExecutionProfileElement dockerExecDependency = new ExecutionProfileElement(
                type: "DockerExecution",
                parameters: new Dictionary<string, IConvertible> { ["Image"] = "ubuntu:20.04" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: new List<ExecutionProfileElement> { dockerExecDependency },
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            WorkloadException exception = Assert.Throws<WorkloadException>(
                () => this.command.CallValidateProfileForDocker(profile));

            Assert.IsNotEmpty(exception.Message);
            Assert.That(exception.Message.Contains("DockerExecution", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void ValidateProfileForDockerThrowsWhenDockerExecutionInActions()
        {
            ExecutionProfileElement dockerExecAction = new ExecutionProfileElement(
                type: "DockerExecution",
                parameters: new Dictionary<string, IConvertible> { ["Image"] = "ubuntu:20.04" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: new List<ExecutionProfileElement> { dockerExecAction },
                dependencies: null,
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            WorkloadException exception = Assert.Throws<WorkloadException>(
                () => this.command.CallValidateProfileForDocker(profile));

            Assert.IsNotEmpty(exception.Message);
            Assert.That(exception.Message.Contains("DockerExecution", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void ValidateProfileForDockerThrowsWhenDockerExecutionInMonitors()
        {
            ExecutionProfileElement dependency = new ExecutionProfileElement(
                type: "ComponentInstallation",
                parameters: new Dictionary<string, IConvertible> { ["Scenario"] = "Install" },
                components: null);

            ExecutionProfileElement dockerExecMonitor = new ExecutionProfileElement(
                type: "DockerExecution",
                parameters: new Dictionary<string, IConvertible> { ["Image"] = "ubuntu:20.04" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: new List<ExecutionProfileElement> { dependency },
                monitors: new List<ExecutionProfileElement> { dockerExecMonitor },
                metadata: null,
                parameters: null,
                parametersOn: null);

            WorkloadException exception = Assert.Throws<WorkloadException>(
                () => this.command.CallValidateProfileForDocker(profile));

            Assert.IsNotEmpty(exception.Message);
            Assert.That(exception.Message.Contains("DockerExecution", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        [TestCase("DOCKEREXECUTION")]
        [TestCase("DockerExecution")]
        [TestCase("dockerexecution")]
        [TestCase("DockerEXECUTION")]
        public void ValidateProfileForDockerDetectsDockerExecutionCaseInsensitively(string dockerExecutionType)
        {
            ExecutionProfileElement component = new ExecutionProfileElement(
                type: dockerExecutionType,
                parameters: new Dictionary<string, IConvertible> { ["Image"] = "ubuntu:20.04" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: new List<ExecutionProfileElement> { component },
                dependencies: null,
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            WorkloadException exception = Assert.Throws<WorkloadException>(
                () => this.command.CallValidateProfileForDocker(profile));

            Assert.IsNotEmpty(exception.Message);
        }

        [Test]
        public void ValidateProfileForDockerSucceedsForValidProfile()
        {
            ExecutionProfileElement dependency = new ExecutionProfileElement(
                type: "ComponentInstallation",
                parameters: new Dictionary<string, IConvertible> { ["Scenario"] = "Install" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: new List<ExecutionProfileElement> { dependency },
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            // Should not throw
            this.command.CallValidateProfileForDocker(profile);
        }

        #endregion

        #region Metadata and Parameters Preservation

        [Test]
        [TestCase(null)]
        [TestCase("00:30:00")]
        [TestCase("02:00:00")]
        public void WrapProfileActionsWithDockerExecutionPreservesMinimumExecutionInterval(string intervalString)
        {
            TimeSpan? interval = string.IsNullOrEmpty(intervalString) ? (TimeSpan?)null : TimeSpan.Parse(intervalString);

            ExecutionProfileElement action = new ExecutionProfileElement(
                type: "Workload",
                parameters: new Dictionary<string, IConvertible> { ["Scenario"] = "Execute" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: interval,
                actions: new List<ExecutionProfileElement> { action },
                dependencies: null,
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            Assert.AreEqual(interval, wrappedProfile.MinimumExecutionInterval);
        }

        [Test]
        public void WrapProfileActionsWithDockerExecutionPreservesMonitors()
        {
            ExecutionProfileElement dependency = new ExecutionProfileElement(
                type: "ComponentInstallation",
                parameters: new Dictionary<string, IConvertible> { ["Scenario"] = "Install" },
                components: null);

            List<ExecutionProfileElement> monitors = new List<ExecutionProfileElement>
            {
                new ExecutionProfileElement(type: "Monitor1", parameters: null, components: null),
                new ExecutionProfileElement(type: "Monitor2", parameters: null, components: null)
            };

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: new List<ExecutionProfileElement> { dependency },
                monitors: monitors,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            Assert.AreEqual(monitors, wrappedProfile.Monitors);
        }

        [Test]
        public void WrapProfileActionsWithDockerExecutionPreservesProfileParameters()
        {
            IDictionary<string, IConvertible> parameters = new Dictionary<string, IConvertible>
            {
                ["Param1"] = "Value1",
                ["Param2"] = "Value2",
                ["Param3"] = "Value3"
            };

            ExecutionProfileElement action = new ExecutionProfileElement(
                type: "Workload",
                parameters: new Dictionary<string, IConvertible> { ["Scenario"] = "Execute" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: new List<ExecutionProfileElement> { action },
                dependencies: null,
                monitors: null,
                metadata: null,
                parameters: parameters,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            Assert.AreEqual(parameters, wrappedProfile.Parameters);
        }
        #endregion

        #region Wrapped Component Properties
        [Test]
        public void WrapProfileActionsWithDockerExecutionSetsDeferContainerCleanupTrue()
        {
            ExecutionProfileElement dependency = new ExecutionProfileElement(
                type: "ComponentInstallation",
                parameters: new Dictionary<string, IConvertible> { ["Scenario"] = "Install" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: null,
                dependencies: new List<ExecutionProfileElement> { dependency },
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            ExecutionProfileElement wrappedDependency = wrappedProfile.Dependencies.First();
            Assert.That(wrappedDependency.Parameters["DeferContainerCleanup"], Is.EqualTo(true));
        }

        [Test]
        public void WrapProfileActionsWithDockerExecutionUsesDockerExecutionType()
        {
            ExecutionProfileElement action = new ExecutionProfileElement(
                type: "Workload",
                parameters: new Dictionary<string, IConvertible> { ["Scenario"] = "Execute" },
                components: null);

            ExecutionProfile profile = new ExecutionProfile(
                description: "Test Profile",
                minimumExecutionInterval: null,
                actions: new List<ExecutionProfileElement> { action },
                dependencies: null,
                monitors: null,
                metadata: null,
                parameters: null,
                parametersOn: null);

            ExecutionProfile wrappedProfile = this.command.CallWrapProfileActionsWithDockerExecution(profile);

            ExecutionProfileElement wrappedAction = wrappedProfile.Actions.First();
            Assert.AreEqual("DockerExecution", wrappedAction.Type);
        }
        #endregion

        /// <summary>
        /// Test subclass of DockerCommand to access protected methods for testing.
        /// </summary>
        private class TestDockerCommand : DockerCommand
        {
            /// <summary>
            /// Calls the protected Initialize method for testing.
            /// </summary>
            public void CallInitialize(string[] args, PlatformSpecifics platformSpecifics)
            {
                this.Initialize(args, platformSpecifics);
            }

            /// <summary>
            /// Calls the protected WrapProfileActionsWithDockerExecution method for testing.
            /// </summary>
            public ExecutionProfile CallWrapProfileActionsWithDockerExecution(ExecutionProfile profile)
            {
                return this.WrapProfileActionsWithDockerExecution(profile);
            }

            /// <summary>
            /// Calls the protected ValidateProfileForDocker method for testing.
            /// </summary>
            public void CallValidateProfileForDocker(ExecutionProfile profile)
            {
                this.ValidateProfileForDocker(profile);
            }
        }
    }
}
