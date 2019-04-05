﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoraKeysManagerFacade
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices;
    using Microsoft.Azure.WebJobs;
    using Newtonsoft.Json;
    using StackExchange.Redis;

    public sealed class LoRaDevAddrCache
    {
        private const string DeltaUpdateKey = "deltaUpdateKey";
        private const string LastDeltaUpdateKeyValue = "lastDeltaUpdateKeyValue";
        private const string FullUpdateKey = "fullUpdateKey";
        private const string GlobalDevAddrUpdateKey = "globalUpdateKey";
        private const string CacheKeyPrefix = "devAddrTable:";
        private static readonly TimeSpan LockExpiry = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan FullUpdateKeyTimeSpan = TimeSpan.FromHours(25);
        private static readonly TimeSpan DeltaUpdateKeyTimeSpan = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan GlobalDevAddrUpdateKeyTimeSpan = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan DevAddrObjectsTTL = TimeSpan.FromHours(24);

        private readonly ILoRaDeviceCacheStore cacheStore;
        private readonly string gatewayId;
        private readonly string cacheKey;
        private readonly string devAddr;
        private string devEUI;

        private static string GenerateKey(string devAddr) => CacheKeyPrefix + devAddr;

        // How do I detect for first boot??? Need to set LastFullReload
        public LoRaDevAddrCache(ILoRaDeviceCacheStore cacheStore, string devAddr, string gatewayId, RegistryManager registryManager)
        {
            if (string.IsNullOrEmpty(devAddr))
            {
                throw new ArgumentNullException("devAddr");
            }

            this.cacheStore = cacheStore;
            this.gatewayId = gatewayId;
            this.cacheKey = GenerateKey(devAddr);
            this.devAddr = devAddr;

            // perform the necessary syncs
            _ = this.PerformNeededSyncs(registryManager);
        }

        public LoRaDevAddrCache(ILoRaDeviceCacheStore cacheStore)
        {
            this.cacheStore = cacheStore;
        }

        public bool HasValue()
        {
            return this.cacheStore.StringGet(this.cacheKey) != null;
        }

        public bool TryGetInfo(out List<DevAddrCacheInfo> info)
        {
            info = new List<DevAddrCacheInfo>();

            var tmp = this.cacheStore.TryGetHashObject(this.cacheKey);
            if (tmp?.Length > 0)
            {
                foreach (var tm in tmp)
                {
                    info.Add(JsonConvert.DeserializeObject<DevAddrCacheInfo>(tm));
                }
            }

            return info.Count > 0;
        }

        // change
        public bool StoreInfo(DevAddrCacheInfo info, bool initialize = false)
        {
            this.devEUI = info.DevEUI;

            return this.cacheStore.TrySetHashObject(this.cacheKey, info.DevEUI, JsonConvert.SerializeObject(info));
        }

        internal async Task PerformNeededSyncs(RegistryManager registryManager)
        {
            if (await this.cacheStore.LockTakeAsync(FullUpdateKey, FullUpdateKey, FullUpdateKeyTimeSpan))
            {
                var ownGlobalLock = false;
                try
                {
                    // if a full update is needed I take the global lock and perform a full reload
                    if (!await this.cacheStore.LockTakeAsync(GlobalDevAddrUpdateKey, GlobalDevAddrUpdateKey, GlobalDevAddrUpdateKeyTimeSpan, true))
                    {
                        // should that really be a exception, this is somehow expected?
                        throw new Exception("Failed to lock the global dev addr update key");
                    }

                    ownGlobalLock = true;
                    await this.PerformFullReload(this.cacheStore, registryManager);
                    // if successfull i set the delta lock to 5 minutes and release the global lock
                    this.cacheStore.StringSet(FullUpdateKey, DateTime.UtcNow.ToString(), FullUpdateKeyTimeSpan);
                    this.cacheStore.StringSet(DeltaUpdateKey, DeltaUpdateKey, DeltaUpdateKeyTimeSpan);
                }
                catch (Exception)
                {
                    // there was a problem, to deal with iot hub throttling we add some time.
                    this.cacheStore.ChangeLockTTL(FullUpdateKey, timeToExpire: TimeSpan.FromMinutes(1)); // on 24
                }
                finally
                {
                    if (ownGlobalLock)
                    {
                        this.cacheStore.LockRelease(GlobalDevAddrUpdateKey, GlobalDevAddrUpdateKey);
                    }
                }
            }
            else if (await this.cacheStore.LockTakeAsync(GlobalDevAddrUpdateKey, GlobalDevAddrUpdateKey, TimeSpan.FromMinutes(5)))
            {
                try
                {
                    if (await this.cacheStore.LockTakeAsync(DeltaUpdateKey, DeltaUpdateKey, TimeSpan.FromMinutes(5)))
                    {
                        await this.PerformDeltaReload(this.cacheStore, registryManager);
                        this.cacheStore.LockRelease(FullUpdateKey, FullUpdateKey);
                    }
                }
                finally
                {
                    this.cacheStore.LockRelease(GlobalDevAddrUpdateKey, GlobalDevAddrUpdateKey);
                }
            }
        }

        private async Task PerformFullReload(ILoRaDeviceCacheStore cacheStore, RegistryManager registryManager)
        {
            var query = $"SELECT * FROM devices WHERE is_defined(properties.desired.AppKey) OR is_defined(properties.desired.AppSKey) OR is_defined(properties.desired.NwkSKey)";
            List<DevAddrCacheInfo> devAddrCacheInfos = await this.GetDeviceTwinsFromIotHub(registryManager, query);
            this.BulkSaveDevAddrCache(devAddrCacheInfos, true);
        }

        /// <summary>
        /// Methof performing a deltaReload
        /// </summary>
        private async Task PerformDeltaReload(ILoRaDeviceCacheStore cacheStore, RegistryManager registryManager)
        {
            // if the value is null (first call), we take five minutes before this call
            var lastUpdate = this.cacheStore.StringGet(LastDeltaUpdateKeyValue) ?? DateTime.UtcNow.AddMinutes(-5).ToString();
            var query = $"SELECT * FROM c where properties.desired.$metadata.$lastUpdated >= '{lastUpdate}' OR properties.reported.$metadata.DevAddr.$lastUpdated >= '{lastUpdate}'";
            List<DevAddrCacheInfo> devAddrCacheInfos = await this.GetDeviceTwinsFromIotHub(registryManager, query);
            this.BulkSaveDevAddrCache(devAddrCacheInfos, false);
        }

        private async Task<List<DevAddrCacheInfo>> GetDeviceTwinsFromIotHub(RegistryManager registryManager, string inputQuery)
        {
            var query = registryManager.CreateQuery(inputQuery);
            this.cacheStore.StringSet(LastDeltaUpdateKeyValue, DateTime.UtcNow.ToString(), TimeSpan.FromDays(1));
            List<DevAddrCacheInfo> devAddrCacheInfos = new List<DevAddrCacheInfo>();
            while (query.HasMoreResults)
            {
                var page = await query.GetNextAsTwinAsync();

                foreach (var twin in page)
                {
                    if (twin.DeviceId != null)
                    {
                        var device = await registryManager.GetDeviceAsync(twin.DeviceId);
                        string currentDevAddr;
                        if (twin.Properties.Desired.Contains("DevAddr"))
                        {
                            currentDevAddr = twin.Properties.Desired["DevAddr"].Value as string;
                        }
                        else if (twin.Properties.Reported.Contains("DevAddr"))
                        {
                            currentDevAddr = twin.Properties.Reported["DevAddr"].Value as string;
                        }
                        else
                        {
                            continue;
                        }

                        devAddrCacheInfos.Add(new DevAddrCacheInfo()
                        {
                            DevAddr = currentDevAddr,
                            DevEUI = twin.DeviceId,
                            GatewayId = twin.Properties.Desired.Contains("GatewayId") ? twin.Properties.Desired["GatewayId"].Value as string : string.Empty
                        });
                    }
                }
            }

            return devAddrCacheInfos;
        }

        /// <summary>
        /// Method to bulk save a devAddrCacheInfo list in redis in a call per devAddr
        /// </summary>
        private void BulkSaveDevAddrCache(List<DevAddrCacheInfo> devAddrCacheInfos, bool canDeleteDeviceWithDevAddr)
        {
            // elements will naturally expire we only need to add new ones
            var regrouping = devAddrCacheInfos.GroupBy(x => x.DevAddr).ToList();
            // initiate connection
            for (int i = 0; i < regrouping.Count; i++)
            {
                var cacheKey = GenerateKey(regrouping[i].Key);
                if (canDeleteDeviceWithDevAddr)
                {
                    this.cacheStore.ReplaceHashObjects(cacheKey, regrouping[i].ToList(), DevAddrObjectsTTL);
                }
                else
                {
                    foreach (var devEuiObject in regrouping[i])
                    {
                        this.cacheStore.TrySetHashObject(cacheKey, devEuiObject.DevEUI, JsonConvert.SerializeObject(devEuiObject), DevAddrObjectsTTL);
                    }
                }
            }
        }
    }
}