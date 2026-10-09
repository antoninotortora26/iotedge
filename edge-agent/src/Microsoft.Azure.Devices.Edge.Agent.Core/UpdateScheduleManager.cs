// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Core
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Represents the state of a module update.
    /// </summary>
    public enum ModuleUpdateState
    {
        /// <summary>No update in progress</summary>
        Idle,

        /// <summary>Image downloaded but not yet applied</summary>
        Downloaded,

        /// <summary>Update applied, module restarted with new image</summary>
        Applied
    }

    /// <summary>
    /// Status information for a module update.
    /// </summary>
    public class ModuleUpdateStatus
    {
        public ModuleUpdateState State { get; set; }

        public string UpdateMode { get; set; }
    }

    /// <summary>
    /// Manages the timing and scheduling of image updates for modules.
    /// Supports four modes:
    /// - immediate: Update image as soon as available (default)
    /// - on_restart: Update only when module restarts
    /// - scheduled: Update at specified times
    /// - on_request: Update only when explicitly requested via message
    /// NOTE: Image pull (prepare) and container update (apply) are now separated.
    /// </summary>
    public interface IUpdateScheduleManager
    {
        /// <summary>
        /// Determines if a module image should be prepared (pulled) immediately.
        /// </summary>
        /// <param name="module">The desired module configuration.</param>
        /// <param name="runtimeModule">The runtime module, if any.</param>
        /// <returns>True if the image should be pulled, false to skip.</returns>
        Task<bool> ShouldPrepareUpdateAsync(IModule module, IRuntimeModule runtimeModule);

        /// <summary>
        /// Determines if a module update should be applied (replace container).
        /// </summary>
        /// <param name="current">The current module configuration, if any.</param>
        /// <param name="next">The desired module configuration.</param>
        /// <param name="runtimeModule">The runtime module, if any.</param>
        /// <returns>True if the update should be applied, false to defer.</returns>
        Task<bool> ShouldApplyUpdateAsync(IModule current, IModule next, IRuntimeModule runtimeModule);

        /// <summary>
        /// Marks that an update request was received for a module (for on_request mode).
        /// </summary>
        /// <param name="moduleName">The name of the module.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task SetUpdateRequestAsync(string moduleName);

        /// <summary>
        /// Clears the update request flag for a module.
        /// </summary>
        /// <param name="moduleName">The name of the module.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ClearUpdateRequestAsync(string moduleName);

        /// <summary>
        /// Sets the update state for a module.
        /// </summary>
        /// <param name="moduleName">The name of the module.</param>
        /// <param name="state">The update state.</param>
        /// <param name="updateMode">The update mode.</param>
        void SetModuleUpdateState(string moduleName, ModuleUpdateState state, string updateMode);

        /// <summary>
        /// Gets all module update statuses.
        /// </summary>
        /// <returns>Dictionary of module update statuses.</returns>
        IDictionary<string, ModuleUpdateStatus> GetModuleUpdateStatuses();

        /// <summary>
        /// Sets the default update configuration from edgeAgent environment variables.
        /// </summary>
        /// <param name="defaultMode">The default update mode (e.g., on_restart, immediate).</param>
        /// <param name="defaultSchedule">The default schedule for scheduled mode (e.g., "23:00").</param>
        void SetDefaultConfiguration(string defaultMode, string defaultSchedule);
    }

    public class UpdateScheduleManager : IUpdateScheduleManager
    {
        static readonly ILogger Log = Logger.Factory.CreateLogger<UpdateScheduleManager>();
        private readonly Dictionary<string, bool> updateRequestedModules = new Dictionary<string, bool>();
        private static readonly string[] ScheduleFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm'Z'"
        };

        private readonly Dictionary<string, ModuleUpdateStatus> moduleUpdateStatuses = new Dictionary<string, ModuleUpdateStatus>();
        private readonly object stateLock = new object();
        private string defaultUpdateMode = null;
        private string defaultUpdateSchedule = null;

        public Task<bool> ShouldPrepareUpdateAsync(IModule module, IRuntimeModule runtimeModule)
        {
            try
            {
                // Always pull new images immediately when detected
                // This ensures images are ready when apply is triggered
                string updateMode = this.GetUpdateMode(module);
                string desiredImage = this.GetModuleImage(module) ?? "<unknown>";

                Log.LogInformation(
                    "[ImageUpdate] Module '{name}': will pull image '{image}' immediately (mode={mode})",
                    module.Name,
                    desiredImage,
                    updateMode);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "[ImageUpdate] Error determining if should prepare update for module '{name}'", module.Name);
                return Task.FromResult(true); // Default to pull on error
            }
        }

        public Task<bool> ShouldApplyUpdateAsync(IModule current, IModule next, IRuntimeModule runtimeModule)
        {
            try
            {
                string updateMode = this.GetUpdateMode(next);
                string desiredImage = this.GetModuleImage(next) ?? "<unknown>";

                switch (updateMode)
                {
                    case Constants.ImageUpdateModeImmediate:
                        Log.LogInformation(
                            "[ImageUpdate] Module '{name}': applying image '{image}' immediately (mode=immediate)",
                            next.Name,
                            desiredImage);
                        return Task.FromResult(true);

                    case Constants.ImageUpdateModeOnRestart:
                        // Apply only when module is NOT running or has just restarted
                        bool isRestarting = this.IsModuleRestarting(next, runtimeModule);
                        bool isNotRunning = runtimeModule == null || runtimeModule.RuntimeStatus != ModuleStatus.Running;
                        bool shouldApply = isNotRunning || isRestarting;
                        if (shouldApply)
                        {
                            string reason = isRestarting ? "module just restarted" : $"module status={runtimeModule?.RuntimeStatus.ToString() ?? "null"}";
                            Log.LogInformation(
                                "[ImageUpdate] Module '{name}': applying image '{image}' on restart (mode=on_restart, reason={reason})",
                                next.Name,
                                desiredImage,
                                reason);
                        }
                        else
                        {
                            Log.LogDebug(
                                "[ImageUpdate] Module '{name}': deferring apply until restart (mode=on_restart, status={status})",
                                next.Name,
                                runtimeModule?.RuntimeStatus.ToString() ?? "null");
                        }

                        return Task.FromResult(shouldApply);

                    case Constants.ImageUpdateModeScheduled:
                        Log.LogDebug(
                            "[ImageUpdate] Module '{name}': checking scheduled window for apply (mode=scheduled)",
                            next.Name);
                        return this.ShouldUpdateAtScheduledTimeAsync(next);

                    case Constants.ImageUpdateModeOnRequest:
                        bool hasRequest = this.updateRequestedModules.ContainsKey(next.Name) &&
                                         this.updateRequestedModules[next.Name];
                        if (hasRequest)
                        {
                            Log.LogInformation(
                                "[ImageUpdate] Module '{name}': applying image '{image}' on explicit request (mode=on_request)",
                                next.Name,
                                desiredImage);
                        }
                        else
                        {
                            Log.LogDebug(
                                "[ImageUpdate] Module '{name}': waiting for explicit request to apply (mode=on_request)",
                                next.Name);
                        }

                        return Task.FromResult(hasRequest);

                    default:
                        Log.LogWarning(
                            "[ImageUpdate] Module '{name}': unknown IMAGE_UPDATE_MODE '{mode}', defaulting to immediate",
                            next.Name,
                            updateMode);
                        return Task.FromResult(true);
                }
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "[ImageUpdate] Error determining if should apply update for module '{name}'", next.Name);
                return Task.FromResult(true); // Default to immediate on error
            }
        }

        public Task SetUpdateRequestAsync(string moduleName)
        {
            lock (this.updateRequestedModules)
            {
                this.updateRequestedModules[moduleName] = true;
                Log.LogInformation("Update request set for module '{name}'", moduleName);
            }

            return TaskEx.Done;
        }

        public Task ClearUpdateRequestAsync(string moduleName)
        {
            lock (this.updateRequestedModules)
            {
                this.updateRequestedModules[moduleName] = false;
            }

            return TaskEx.Done;
        }

        public void SetModuleUpdateState(string moduleName, ModuleUpdateState state, string updateMode)
        {
            lock (this.stateLock)
            {
                this.moduleUpdateStatuses[moduleName] = new ModuleUpdateStatus
                {
                    State = state,
                    UpdateMode = updateMode
                };

                Log.LogInformation(
                    "[ImageUpdate] Module '{name}' state updated: {state}, mode: {mode}",
                    moduleName,
                    state,
                    updateMode);
            }
        }

        public IDictionary<string, ModuleUpdateStatus> GetModuleUpdateStatuses()
        {
            lock (this.stateLock)
            {
                // Return a copy to avoid concurrency issues
                return new Dictionary<string, ModuleUpdateStatus>(this.moduleUpdateStatuses);
            }
        }

        public void SetDefaultConfiguration(string defaultMode, string defaultSchedule)
        {
            this.defaultUpdateMode = defaultMode;
            this.defaultUpdateSchedule = defaultSchedule;
            Log.LogInformation(
                "[ImageUpdate] Default configuration set: mode={mode}, schedule={schedule}",
                defaultMode ?? "<not set>",
                defaultSchedule ?? "<not set>");
        }

        private string GetUpdateMode(IModule module)
        {
            // Priority 1: Check module-specific environment variable
            if (module.Env?.ContainsKey(Constants.ImageUpdateModeVariableName) ?? false)
            {
                string modeValue = module.Env[Constants.ImageUpdateModeVariableName]?.Value;
                if (!string.IsNullOrWhiteSpace(modeValue))
                {
                    Log.LogDebug("[ImageUpdate] Module '{name}': using mode '{mode}' from module environment variable", module.Name, modeValue);
                    return modeValue;
                }
            }

            // Priority 2: Check default configuration from edgeAgent environment variables
            if (!string.IsNullOrWhiteSpace(this.defaultUpdateMode))
            {
                Log.LogDebug("[ImageUpdate] Module '{name}': using mode '{mode}' from edgeAgent default configuration", module.Name, this.defaultUpdateMode);
                return this.defaultUpdateMode;
            }

            // Priority 3: Hardcoded default
            Log.LogDebug("[ImageUpdate] Module '{name}': using hardcoded default mode 'immediate'", module.Name);
            return Constants.ImageUpdateModeImmediate;
        }

        private Task<bool> ShouldUpdateAtScheduledTimeAsync(IModule module)
        {
            try
            {
                // Get schedule with priority: module env var > default config > none
                string scheduleValue = null;

                // Priority 1: Module-specific environment variable
                if (module.Env?.ContainsKey(Constants.ImageUpdateScheduleVariableName) ?? false)
                {
                    scheduleValue = module.Env[Constants.ImageUpdateScheduleVariableName]?.Value;
                }

                // Priority 2: Default configuration from edgeAgent
                if (string.IsNullOrWhiteSpace(scheduleValue))
                {
                    scheduleValue = this.defaultUpdateSchedule;
                }

                if (string.IsNullOrWhiteSpace(scheduleValue))
                {
                    Log.LogWarning("[ImageUpdate] Module '{name}': scheduled mode but no schedule configured, skipping apply", module.Name);
                    return Task.FromResult(false);
                }

                // Schedule is a one-time absolute UTC timestamp in ISO 8601 format, e.g. "2026-10-08T15:53:00Z".
                // The apply is not limited to a narrow window so that it still happens if the device was offline or busy at that instant.
                if (!DateTimeOffset.TryParseExact(
                        scheduleValue.Trim(),
                        ScheduleFormats,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset scheduledInstant))
                {
                    Log.LogWarning(
                        "[ImageUpdate] Module '{name}': invalid IMAGE_UPDATE_SCHEDULE format '{schedule}'. Expected an ISO 8601 UTC date/time, e.g. '2026-10-08T15:53:00Z'",
                        module.Name,
                        scheduleValue);
                    return Task.FromResult(false);
                }

                DateTimeOffset utcNow = DateTimeOffset.UtcNow;
                if (utcNow < scheduledInstant)
                {
                    Log.LogDebug(
                        "[ImageUpdate] Module '{name}': scheduled date/time not yet reached, deferring apply (scheduled={scheduled:o}, current={current:o})",
                        module.Name,
                        scheduledInstant,
                        utcNow);
                    return Task.FromResult(false);
                }

                Log.LogInformation(
                    "[ImageUpdate] Module '{name}': applying image — scheduled date/time reached (scheduled={scheduled:o}, current={current:o})",
                    module.Name,
                    scheduledInstant,
                    utcNow);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "[ImageUpdate] Error evaluating scheduled update time for module '{name}'", module.Name);
                return Task.FromResult(false);
            }
        }

        private bool IsModuleRestarting(IModule module, IRuntimeModule runtimeModule)
        {
            // If runtimeModule is null, the module doesn't exist yet, so it's not restarting
            if (runtimeModule == null)
            {
                return false;
            }

            // Simple heuristic: if the module's last start time is very recent (within last minute),
            // it probably just restarted
            if (runtimeModule.LastStartTimeUtc != DateTime.MinValue)
            {
                TimeSpan timeSinceStart = DateTime.UtcNow - runtimeModule.LastStartTimeUtc;
                return timeSinceStart < TimeSpan.FromMinutes(1);
            }

            return false;
        }

        private string GetModuleImage(IModule module)
        {
            try
            {
                // IModule does not expose image directly; use reflection to read Config.Image
                // This works for DockerModule and any module type that has a Config with an Image property.
                var configProperty = module.GetType().GetProperty("Config");
                if (configProperty != null)
                {
                    var config = configProperty.GetValue(module);
                    if (config != null)
                    {
                        var imageProperty = config.GetType().GetProperty("Image");
                        if (imageProperty != null)
                        {
                            string image = imageProperty.GetValue(config) as string;

                            // Strip surrounding quotes if present (some configs include them)
                            if (!string.IsNullOrEmpty(image))
                            {
                                image = image.Trim('"', '\'');
                            }

                            Log.LogInformation(
                                "[ImageUpdate] Module '{name}': extracted image '{image}' via reflection",
                                module.Name,
                                image ?? "<null>");
                            return image;
                        }
                    }
                }

                Log.LogWarning(
                    "[ImageUpdate] Module '{name}': could not extract image via reflection (type={type})",
                    module.Name,
                    module.GetType().Name);
                return null;
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "[ImageUpdate] Error extracting image from module '{name}'", module.Name);
                return null;
            }
        }
    }
}
