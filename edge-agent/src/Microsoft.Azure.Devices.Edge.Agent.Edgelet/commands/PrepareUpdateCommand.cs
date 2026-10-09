// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Edgelet.Commands
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Agent.Core.PlanRunner;
    using Microsoft.Azure.Devices.Edge.Agent.Edgelet.Models;
    using Microsoft.Azure.Devices.Edge.Util;

    public class PrepareUpdateCommand : ICommand
    {
        readonly IModuleManager moduleManager;
        readonly ModuleSpec module;

        readonly PulledImageCache imageCache;

        public PrepareUpdateCommand(IModuleManager moduleManager, IModule module, object settings, PulledImageCache imageCache = null)
        {
            this.moduleManager = moduleManager;
            this.module = BuildModuleSpec(module, settings);
            this.imageCache = imageCache;
        }

        public string Id => $"PrepareUpdateCommand({this.module.Name})";

        public string Show() => $"Prepare module {this.module.Name}";

        public async Task ExecuteAsync(CancellationToken token)
        {
            await this.moduleManager.PrepareUpdateAsync(this.module);
            this.imageCache?.Add(PulledImageCache.GetImage(this.module.Settings));
        }

        public Task UndoAsync(CancellationToken token) => TaskEx.Done;

        static ModuleSpec BuildModuleSpec(IModule module, object settings)
        {
            return new ModuleSpec(module.Name, module.Type, module.ImagePullPolicy, settings);
        }
    }
}
