// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Edgelet.Commands
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;

    // Runs the inner command but does not fail the plan if it throws.
    // Used for image pulls: when the registry is unreachable (e.g. offline) the image
    // may already be present locally, so container creation must still be attempted.
    // If the image is really missing, the create/update request itself will fail.
    public class BestEffortCommand : ICommand
    {
        static readonly ILogger Log = Logger.Factory.CreateLogger<BestEffortCommand>();
        readonly ICommand inner;

        public BestEffortCommand(ICommand inner)
        {
            this.inner = Preconditions.CheckNotNull(inner, nameof(inner));
        }

        public string Id => this.inner.Id;

        public string Show() => this.inner.Show();

        public async Task ExecuteAsync(CancellationToken token)
        {
            try
            {
                await this.inner.ExecuteAsync(token);
            }
            catch (Exception ex) when (!ex.IsFatal() && !token.IsCancellationRequested)
            {
                Log.LogWarning(ex, "Command '{command}' failed, continuing (image may already be present locally).", this.inner.Show());
            }
        }

        public Task UndoAsync(CancellationToken token) => this.inner.UndoAsync(token);
    }
}
