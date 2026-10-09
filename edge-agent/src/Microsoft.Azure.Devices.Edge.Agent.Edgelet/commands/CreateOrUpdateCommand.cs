// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Edgelet.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Agent.Edgelet.Models;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Configuration;
    using Newtonsoft.Json;

    public class CreateOrUpdateCommand : ICommand
    {
        readonly IModuleManager moduleManager;
        readonly ModuleSpec moduleSpec;
        readonly Lazy<string> id;
        readonly Operation operation;
        readonly PulledImageCache imageCache;

        CreateOrUpdateCommand(IModuleManager moduleManager, ModuleSpec moduleSpec, Operation operation, PulledImageCache imageCache)
        {
            this.moduleManager = Preconditions.CheckNotNull(moduleManager, nameof(moduleManager));
            this.moduleSpec = Preconditions.CheckNotNull(moduleSpec, nameof(moduleSpec));
            this.id = new Lazy<string>(() => JsonConvert.SerializeObject(this.moduleSpec).CreateSha256());
            this.operation = operation;
            this.imageCache = imageCache;
        }

        enum Operation
        {
            Create,
            Update,
            UpdateAndStart
        }

        public string Id => this.id.Value;

        public ModuleSpec ModuleSpec => this.moduleSpec;

        public static CreateOrUpdateCommand BuildCreate(
            IModuleManager moduleManager,
            IModule module,
            IModuleIdentity identity,
            IConfigSource configSource,
            object settings,
            string edgeDeviceHostname,
            Option<string> parentEdgeHostname,
            PulledImageCache imageCache = null) =>
            Build(moduleManager, module, identity, configSource, settings, Operation.Create, edgeDeviceHostname, parentEdgeHostname, imageCache);

        public static CreateOrUpdateCommand BuildUpdate(
            IModuleManager moduleManager,
            IModule module,
            IModuleIdentity identity,
            IConfigSource configSource,
            object settings,
            bool start,
            string edgeDeviceHostname,
            Option<string> parentEdgeHostname,
            PulledImageCache imageCache = null) =>
            Build(moduleManager, module, identity, configSource, settings, start ? Operation.UpdateAndStart : Operation.Update, edgeDeviceHostname, parentEdgeHostname, imageCache);

        public Task ExecuteAsync(CancellationToken token)
        {
            // The pull has already been attempted by the preceding prepare command. If the image is known
            // to be present (pulled now or earlier), iotedged must not pull it again: that would fail offline.
            ModuleSpec spec = this.moduleSpec;
            if (this.imageCache != null && this.imageCache.Contains(PulledImageCache.GetImage(spec.Settings)))
            {
                spec = new ModuleSpec(spec.Name, spec.Type, ImagePullPolicy.Never, spec.Settings, spec.EnvironmentVariables);
            }

            switch (this.operation)
            {
                case Operation.Update:
                    return this.moduleManager.UpdateModuleAsync(spec);

                case Operation.UpdateAndStart:
                    return this.moduleManager.UpdateAndStartModuleAsync(spec);

                default:
                    return this.moduleManager.CreateModuleAsync(spec);
            }
        }

        public string Show()
        {
            switch (this.operation)
            {
                case Operation.Update:
                    return $"Update module {this.moduleSpec.Name}";

                case Operation.UpdateAndStart:
                    return $"Update and start module {this.moduleSpec.Name}";

                default:
                    return $"Create module {this.moduleSpec.Name}";
            }
        }

        public Task UndoAsync(CancellationToken token) => TaskEx.Done;

        static ModuleSpec BuildModuleSpec(IModule module, IEnumerable<EnvVar> envVars, object settings)
        {
            return new ModuleSpec(module.Name, module.Type, module.ImagePullPolicy, settings, envVars);
        }

        static IEnumerable<EnvVar> GetEnvVars(
            IDictionary<string, EnvVal> moduleEnvVars,
            IModuleIdentity identity,
            IConfigSource configSource,
            string edgeDeviceHostname,
            Option<string> parentEdgeHostname)
        {
            List<EnvVar> envVars = moduleEnvVars.Select(m => new EnvVar(m.Key, m.Value.Value)).ToList();

            // Inject the connection details as an environment variable
            if (identity.Credentials is IdentityProviderServiceCredentials creds)
            {
                if (!string.IsNullOrWhiteSpace(creds.ProviderUri))
                {
                    envVars.Add(new EnvVar(Constants.EdgeletWorkloadUriVariableName, creds.ProviderUri));
                }

                if (!string.IsNullOrWhiteSpace(creds.AuthScheme))
                {
                    envVars.Add(new EnvVar(Constants.EdgeletAuthSchemeVariableName, creds.AuthScheme));
                }

                if (!string.IsNullOrWhiteSpace(creds.ModuleGenerationId))
                {
                    envVars.Add(new EnvVar(Constants.EdgeletModuleGenerationIdVariableName, creds.ModuleGenerationId));
                }
            }

            if (!string.IsNullOrWhiteSpace(identity.IotHubHostname))
            {
                envVars.Add(new EnvVar(Constants.IotHubHostnameVariableName, identity.IotHubHostname));
            }

            // In nested edge scenario, EdgeAgent and EdgeHub will use parent edge as upstream gateway,
            // Other modules use current edge hub as upstream gateway and have parent edge hostname in environment variable.
            if (identity.ModuleId.Equals(Constants.EdgeAgentModuleIdentityName) || identity.ModuleId.Equals(Constants.EdgeHubModuleIdentityName))
            {
                envVars.Add(new EnvVar(Constants.EdgeDeviceHostNameKey, edgeDeviceHostname));
                parentEdgeHostname.ForEach(value => envVars.Add(new EnvVar(Constants.GatewayHostnameVariableName, value)));
            }
            else
            {
                envVars.Add(new EnvVar(Constants.GatewayHostnameVariableName, edgeDeviceHostname));
                parentEdgeHostname.ForEach(value => envVars.Add(new EnvVar(Constants.ParentEdgeHostnameVariableName, value)));
            }

            if (!string.IsNullOrWhiteSpace(identity.DeviceId))
            {
                envVars.Add(new EnvVar(Constants.DeviceIdVariableName, identity.DeviceId));
            }

            if (!string.IsNullOrWhiteSpace(identity.ModuleId))
            {
                envVars.Add(new EnvVar(Constants.ModuleIdVariableName, identity.ModuleId));
            }

            if (!envVars.Exists(e => e.Key == Logger.RuntimeLogLevelEnvKey))
            {
                envVars.Add(new EnvVar(Logger.RuntimeLogLevelEnvKey, Logger.GetLogLevel().ToString()));
            }

            configSource.Configuration.GetValue<string>(Constants.UpstreamProtocolKey).ToUpstreamProtocol().ForEach(
                u =>
                {
                    if (!envVars.Any(e => e.Key.Equals(Constants.UpstreamProtocolKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        envVars.Add(new EnvVar(Constants.UpstreamProtocolKey, u.ToString()));
                    }
                });

            if (identity.ModuleId.Equals(Constants.EdgeAgentModuleIdentityName))
            {
                string managementUri = configSource.Configuration.GetValue<string>(Constants.EdgeletManagementUriVariableName);
                if (!string.IsNullOrEmpty(managementUri))
                {
                    envVars.Add(new EnvVar(Constants.EdgeletManagementUriVariableName, managementUri));
                }

                string networkId = configSource.Configuration.GetValue<string>(Constants.NetworkIdKey);
                if (!string.IsNullOrEmpty(networkId))
                {
                    envVars.Add(new EnvVar(Constants.NetworkIdKey, networkId));
                }

                string workloadListenMnt = configSource.Configuration.GetValue<string>(Constants.EdgeletWorkloadListenMntUriVariableName);
                if (!string.IsNullOrEmpty(workloadListenMnt))
                {
                    envVars.Add(new EnvVar(Constants.EdgeletWorkloadListenMntUriVariableName, workloadListenMnt));
                }

                envVars.Add(new EnvVar(Constants.ModeKey, Constants.IotedgedMode));
            }

            // Set the edgelet's api version
            string apiVersion = configSource.Configuration.GetValue<string>(Constants.EdgeletApiVersionVariableName);
            if (!string.IsNullOrEmpty(apiVersion))
            {
                envVars.Add(new EnvVar(Constants.EdgeletApiVersionVariableName, apiVersion));
            }

            return envVars;
        }

        static CreateOrUpdateCommand Build(
            IModuleManager moduleManager,
            IModule module,
            IModuleIdentity identity,
            IConfigSource configSource,
            object settings,
            Operation operation,
            string edgeDeviceHostname,
            Option<string> parentEdgeHostname,
            PulledImageCache imageCache)
        {
            Preconditions.CheckNotNull(moduleManager, nameof(moduleManager));
            Preconditions.CheckNotNull(module, nameof(module));
            Preconditions.CheckNotNull(identity, nameof(identity));
            Preconditions.CheckNotNull(configSource, nameof(configSource));
            Preconditions.CheckNotNull(settings, nameof(settings));
            Preconditions.CheckNonWhiteSpace(edgeDeviceHostname, nameof(edgeDeviceHostname));

            IEnumerable<EnvVar> envVars = GetEnvVars(module.Env, identity, configSource, edgeDeviceHostname, parentEdgeHostname);
            ModuleSpec moduleSpec = BuildModuleSpec(module, envVars, settings);
            return new CreateOrUpdateCommand(moduleManager, moduleSpec, operation, imageCache);
        }
    }
}
