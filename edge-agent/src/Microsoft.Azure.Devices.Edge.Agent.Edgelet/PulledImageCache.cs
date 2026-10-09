// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Edgelet
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;

    // Remembers the images that were successfully pulled, in memory and in a file next to the
    // deployment backup, so that they are neither pulled again nor required from the registry
    // when the device is offline (including after an edgeAgent restart).
    public class PulledImageCache
    {
        static readonly ILogger Log = Logger.Factory.CreateLogger<PulledImageCache>();
        readonly string filePath;
        readonly object sync = new object();
        HashSet<string> images;

        public PulledImageCache(string filePath)
        {
            this.filePath = filePath;
        }

        public static string GetImage(object settings) =>
            settings?.GetType().GetProperty("Image")?.GetValue(settings) as string;

        // Untagged images and the "latest" tag can change in the registry, so they are always pulled.
        public static bool IsMutableTag(string image)
        {
            string name = image.Substring(image.LastIndexOf('/') + 1);
            int tagSeparator = name.LastIndexOf(':');
            return tagSeparator < 0 || name.Substring(tagSeparator + 1) == "latest";
        }

        public bool Contains(string image)
        {
            if (string.IsNullOrWhiteSpace(image) || IsMutableTag(image))
            {
                return false;
            }

            lock (this.sync)
            {
                return this.Load().Contains(image);
            }
        }

        public void Add(string image)
        {
            if (string.IsNullOrWhiteSpace(image) || IsMutableTag(image))
            {
                return;
            }

            lock (this.sync)
            {
                if (this.Load().Add(image))
                {
                    this.Save();
                }
            }
        }

        HashSet<string> Load()
        {
            if (this.images != null)
            {
                return this.images;
            }

            this.images = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!string.IsNullOrWhiteSpace(this.filePath) && File.Exists(this.filePath))
                {
                    var stored = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(this.filePath));
                    if (stored != null)
                    {
                        this.images.UnionWith(stored);
                    }
                }
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                Log.LogWarning(ex, "Could not read the pulled images file {path}.", this.filePath);
            }

            return this.images;
        }

        void Save()
        {
            if (string.IsNullOrWhiteSpace(this.filePath))
            {
                return;
            }

            try
            {
                string dir = Path.GetDirectoryName(this.filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmpPath = this.filePath + ".tmp";
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(this.images));
                File.Copy(tmpPath, this.filePath, true);
                File.Delete(tmpPath);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                Log.LogWarning(ex, "Could not write the pulled images file {path}.", this.filePath);
            }
        }
    }
}
