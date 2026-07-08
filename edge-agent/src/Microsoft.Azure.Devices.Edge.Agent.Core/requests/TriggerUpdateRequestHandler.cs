// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Agent.Core.Requests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;

    /// <summary>
    /// Request handler for triggering module updates in on_request mode.
    /// </summary>
    public class TriggerUpdateRequestHandler : RequestHandlerBase<TriggerUpdateRequest, TriggerUpdateResponse>
    {
        static readonly ILogger Log = Logger.Factory.CreateLogger<TriggerUpdateRequestHandler>();
        readonly IUpdateScheduleManager updateScheduleManager;

        public TriggerUpdateRequestHandler(IUpdateScheduleManager updateScheduleManager)
        {
            this.updateScheduleManager = Preconditions.CheckNotNull(updateScheduleManager, nameof(updateScheduleManager));
        }

        public override string RequestName => "TriggerModuleUpdate";

        protected override async Task<Option<TriggerUpdateResponse>> HandleRequestInternal(
            Option<TriggerUpdateRequest> payload,
            CancellationToken cancellationToken)
        {
            TriggerUpdateRequest request = payload.Expect(() => new System.ArgumentException("Request payload is required"));

            if (string.IsNullOrWhiteSpace(request.ModuleName))
            {
                throw new System.ArgumentException("ModuleName is required in request payload");
            }

            Log.LogInformation("Received trigger update request for module '{moduleName}'", request.ModuleName);

            await this.updateScheduleManager.SetUpdateRequestAsync(request.ModuleName);

            var response = new TriggerUpdateResponse
            {
                ModuleName = request.ModuleName,
                Message = "Update request registered. Module will be updated on next reconciliation cycle."
            };

            return Option.Some(response);
        }
    }

    public class TriggerUpdateRequest
    {
        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }
    }

    public class TriggerUpdateResponse
    {
        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
