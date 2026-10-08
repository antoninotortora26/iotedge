// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Edgelet
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Agent.Edgelet.Models;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;

    public class ModuleIdentityLifecycleManager : IModuleIdentityLifecycleManager
    {
        readonly IIdentityManager identityManager;
        readonly ModuleIdentityProviderServiceBuilder identityProviderServiceBuilder;
        readonly Uri workloadUri;
        readonly bool enableOrphanedIdentityCleanup;
        readonly string identityBackupFilePath;

        protected virtual bool ShouldAlwaysReturnIdentities => false;

        public ModuleIdentityLifecycleManager(IIdentityManager identityManager, ModuleIdentityProviderServiceBuilder identityProviderServiceBuilder, Uri workloadUri, bool enableOrphanedIdentityCleanup, string identityBackupFilePath = null)
        {
            this.identityManager = Preconditions.CheckNotNull(identityManager, nameof(identityManager));
            this.identityProviderServiceBuilder = Preconditions.CheckNotNull(identityProviderServiceBuilder, nameof(identityProviderServiceBuilder));
            this.workloadUri = Preconditions.CheckNotNull(workloadUri, nameof(workloadUri));
            this.enableOrphanedIdentityCleanup = enableOrphanedIdentityCleanup;
            this.identityBackupFilePath = identityBackupFilePath;
        }

        public async Task<IImmutableDictionary<string, IModuleIdentity>> GetModuleIdentitiesAsync(ModuleSet desired, ModuleSet current)
        {
            Diff diff = desired.Diff(current);
            if (diff.IsEmpty && !this.ShouldAlwaysReturnIdentities)
            {
                return ImmutableDictionary<string, IModuleIdentity>.Empty;
            }

            try
            {
                IImmutableDictionary<string, Identity> identities = (await this.identityManager.GetIdentities()).ToImmutableDictionary(i => i.ModuleId);

                if (this.enableOrphanedIdentityCleanup)
                {
                    identities = await this.RemoveStaleIdentities(desired, current, identities);
                }

                IImmutableDictionary<string, IModuleIdentity> moduleIdentities = await this.GetModuleIdentitiesAsync(diff, identities);
                this.lastKnownIdentities = moduleIdentities;
                this.BackupIdentities(moduleIdentities);
                return moduleIdentities;
            }
            catch (Exception ex)
            {
                Events.ErrorGettingModuleIdentities(ex);

                // Offline: reuse the last identities obtained (in memory, or from the on-disk backup
                // after a restart) so deferred (e.g. scheduled) updates can still be planned.
                if (this.lastKnownIdentities.Count == 0)
                {
                    this.lastKnownIdentities = this.RestoreIdentities();
                }

                return this.lastKnownIdentities;
            }
        }

        volatile IImmutableDictionary<string, IModuleIdentity> lastKnownIdentities = ImmutableDictionary<string, IModuleIdentity>.Empty;

        void BackupIdentities(IImmutableDictionary<string, IModuleIdentity> moduleIdentities)
        {
            if (string.IsNullOrWhiteSpace(this.identityBackupFilePath) || moduleIdentities.Count == 0)
            {
                return;
            }

            try
            {
                var entries = moduleIdentities.Values
                    .Select(m => new { Id = m.ModuleId, Creds = m.Credentials as IdentityProviderServiceCredentials })
                    .Where(x => x.Creds != null)
                    .Select(x => new BackedUpIdentity { ModuleId = x.Id, GenerationId = x.Creds.ModuleGenerationId })
                    .ToList();

                string dir = System.IO.Path.GetDirectoryName(this.identityBackupFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                string tmpPath = this.identityBackupFilePath + ".tmp";
                System.IO.File.WriteAllText(tmpPath, Newtonsoft.Json.JsonConvert.SerializeObject(entries));
                System.IO.File.Copy(tmpPath, this.identityBackupFilePath, true);
                System.IO.File.Delete(tmpPath);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                Events.ErrorGettingModuleIdentities(ex);
            }
        }

        IImmutableDictionary<string, IModuleIdentity> RestoreIdentities()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(this.identityBackupFilePath) && System.IO.File.Exists(this.identityBackupFilePath))
                {
                    var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BackedUpIdentity>>(System.IO.File.ReadAllText(this.identityBackupFilePath));
                    return entries
                        .Select(e => this.identityProviderServiceBuilder.Create(e.ModuleId, e.GenerationId, this.workloadUri.ToString()))
                        .ToImmutableDictionary(m => ModuleIdentityHelper.GetModuleName(m.ModuleId));
                }
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                Events.ErrorGettingModuleIdentities(ex);
            }

            return ImmutableDictionary<string, IModuleIdentity>.Empty;
        }

        class BackedUpIdentity
        {
            public string ModuleId { get; set; }

            public string GenerationId { get; set; }
        }

        async Task<IImmutableDictionary<string, IModuleIdentity>> GetModuleIdentitiesAsync(Diff diff, IImmutableDictionary<string, Identity> identities)
        {
            IList<string> addedOrUpdatedModuleNames = diff.AddedOrUpdated.Select(m => ModuleIdentityHelper.GetModuleIdentityName(m.Name)).ToList();
            List<string> removedModuleNames = diff.Removed.Select(ModuleIdentityHelper.GetModuleIdentityName).ToList();

            // Create identities for all modules that are in the deployment but aren't in iotedged.
            IEnumerable<string> createIdentities = addedOrUpdatedModuleNames.Where(m => !identities.ContainsKey(m));

            // Update identities for all modules that are in the deployment and are in iotedged (except for Edge Agent which gets special
            // treatment in iotedged).
            //
            // NOTE: This update can potentially be made more efficient by checking that an update is actually needed, i.e. if auth type
            // is not SAS and/or if the credentials are not what iotedged expects it to be.
            IEnumerable<Identity> updateIdentities = addedOrUpdatedModuleNames
                .Where(m => identities.ContainsKey(m) && m != Constants.EdgeAgentModuleIdentityName)
                .Select(m => identities[m]);

            // Remove identities which exist in iotedged but don't exist in the deployment anymore. We exclude however, identities that
            // aren't managed by Edge since these have been created by some out-of-band process and Edge doesn't "own" the identity.
            IEnumerable<string> removeIdentities = removedModuleNames.Where(
                m => identities.ContainsKey(m) &&
                     Constants.ModuleIdentityEdgeManagedByValue.Equals(identities[m].ManagedBy, StringComparison.OrdinalIgnoreCase));

            // First remove identities (so that we don't go over the IoTHub limit).
            await Task.WhenAll(removeIdentities.Select(i => this.identityManager.DeleteIdentityAsync(i)));

            // Create/update identities.
            IEnumerable<Task<Identity>> createTasks = createIdentities.Select(i => this.identityManager.CreateIdentityAsync(i, Constants.ModuleIdentityEdgeManagedByValue));
            IEnumerable<Task<Identity>> updateTasks = updateIdentities.Select(i => this.identityManager.UpdateIdentityAsync(i.ModuleId, i.GenerationId, i.ManagedBy));
            Identity[] upsertedIdentities = await Task.WhenAll(createTasks.Concat(updateTasks));

            List<IModuleIdentity> moduleIdentities = upsertedIdentities.Select(this.GetModuleIdentity).ToList();

            // Add back the unchanged identities (including Edge Agent).
            var upsertedIdentityList = moduleIdentities.Select(i => i.ModuleId).ToList();
            var unchangedIdentities = identities.Where(i => !removedModuleNames.Contains(i.Key) && !upsertedIdentityList.Contains(i.Key));
            moduleIdentities.AddRange(unchangedIdentities.Select(i => this.GetModuleIdentity(i.Value)));

            return moduleIdentities.ToImmutableDictionary(m => ModuleIdentityHelper.GetModuleName(m.ModuleId));
        }

        IModuleIdentity GetModuleIdentity(Identity identity) =>
            this.identityProviderServiceBuilder.Create(identity.ModuleId, identity.GenerationId, this.workloadUri.ToString());

        async Task<IImmutableDictionary<string, Identity>> RemoveStaleIdentities(ModuleSet desired, ModuleSet current, IImmutableDictionary<string, Identity> identities)
        {
            // Need to remove any identities (except EA/EH and those in desired) that are managed by EA but don't have a tracked module in the ModuleSet.
            IEnumerable<string> removeOrphanedIdentities = identities.Where(
                i => !(
                        Constants.EdgeAgentModuleIdentityName.Equals(i.Key, StringComparison.Ordinal) ||
                        Constants.EdgeHubModuleIdentityName.Equals(i.Key, StringComparison.Ordinal)
                     ) &&
                     Constants.ModuleIdentityEdgeManagedByValue.Equals(i.Value.ManagedBy, StringComparison.OrdinalIgnoreCase) &&
                     !current.Modules.Any(m => ModuleIdentityHelper.GetModuleIdentityName(m.Key) == i.Key) &&
                     !desired.Modules.Any(m => ModuleIdentityHelper.GetModuleIdentityName(m.Key) == i.Key))
                .Select(i => i.Key);

            Events.RemoveOrphanedIdentities(removeOrphanedIdentities);

            // First any identities that don't have running modules currently.
            await Task.WhenAll(removeOrphanedIdentities.Select(i => this.identityManager.DeleteIdentityAsync(i)));

            Events.FinishedRemovingOrphanedIdentities(removeOrphanedIdentities);

            // Remove any identities from map that were in removeOrphanedIdentities
            return identities.RemoveRange(removeOrphanedIdentities);
        }

        static class Events
        {
            const int IdStart = AgentEventIds.ModuleIdentityLifecycleManager;
            static readonly ILogger Log = Logger.Factory.CreateLogger<ModuleIdentityLifecycleManager>();

            enum EventIds
            {
                ErrorGettingModuleIdentities = IdStart,
                RemoveOrphanedIdentities,
                FinishedRemovingOrphanedIdentities
            }

            public static void ErrorGettingModuleIdentities(Exception ex)
            {
                Log.LogDebug((int)EventIds.ErrorGettingModuleIdentities, ex, "Error getting module identities.");
            }

            public static void RemoveOrphanedIdentities(IEnumerable<string> removeOrphanedIdentities)
            {
                Log.LogInformation((int)EventIds.RemoveOrphanedIdentities, $"Removing orphaned identities {string.Join(", ", removeOrphanedIdentities.Select(s => s.ToString()))}");
            }

            public static void FinishedRemovingOrphanedIdentities(IEnumerable<string> removeOrphanedIdentities)
            {
                Log.LogInformation((int)EventIds.FinishedRemovingOrphanedIdentities, $"Finished removing orphaned identities {string.Join(", ", removeOrphanedIdentities.Select(s => s.ToString()))}");
            }
        }
    }
}
