///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Linq;
using System.Reflection;
using CorradeConfiguration;
using OpenMetaverse;
using Inventory = wasOpenMetaverse.Inventory;

namespace Corrade
{
    public partial class Corrade
    {
        public partial class RLVBehaviours
        {
            public static Action<string, RLVRule, UUID> remoutfit = (message, rule, senderUUID) =>
            {
                if (!rule.Param.Equals(RLV_CONSTANTS.FORCE))
                {
                    return;
                }
                switch (!string.IsNullOrEmpty(rule.Option))
                {
                    case true: // A single wearable
                        var wearTypeInfo = typeof (WearableType).GetFields(BindingFlags.Public |
                                                                           BindingFlags.Static)
                            .AsParallel().FirstOrDefault(
                                p => string.Equals(rule.Option, p.Name, StringComparison.InvariantCultureIgnoreCase));
                        if (wearTypeInfo == null)
                        {
                            break;
                        }
                        var wearable =
                            Inventory.GetWearables(Client, CurrentOutfitFolder)
                                .ToArray()
                                .AsParallel()
                                .FirstOrDefault(
                                    o =>
                                        !Inventory.IsBodyPart(Client, o) &&
                                        (o as InventoryWearable).WearableType.Equals(
                                            (WearableType) wearTypeInfo.GetValue(null)));
                        if (wearable != null)
                        {
                            CorradeThreadPool[CorradeThreadType.NOTIFICATION].Spawn(
                                () => SendNotification(
                                    Configuration.Notifications.OutfitChanged,
                                    new OutfitEventArgs
                                    {
                                        Action = Action.UNWEAR,
                                        Name = wearable.Name,
                                        Description = wearable.Description,
                                        Item = wearable.UUID,
                                        Asset = wearable.AssetUUID,
                                        Entity = wearable.AssetType,
                                        Creator = wearable.CreatorID,
                                        Permissions =
                                            Inventory.wasPermissionsToString(
                                                wearable.Permissions),
                                        Inventory = wearable.InventoryType,
                                        Slot = (wearable as InventoryWearable).WearableType.ToString()
                                    }),
                                corradeConfiguration.MaximumNotificationThreads);
                            Inventory.UnWear(Client, CurrentOutfitFolder, wearable, corradeConfiguration.ServicesTimeout);
                        }
                        break;
                    default:
                        Inventory.GetWearables(Client, CurrentOutfitFolder)
                            .AsParallel()
                            .Where(o => !Inventory.IsBodyPart(Client, o as InventoryWearable))
                            .ForAll(
                                o =>
                                {
                                    CorradeThreadPool[CorradeThreadType.NOTIFICATION].Spawn(
                                        () => SendNotification(
                                            Configuration.Notifications.OutfitChanged,
                                            new OutfitEventArgs
                                            {
                                                Action = Action.UNWEAR,
                                                Name = o.Name,
                                                Description = o.Description,
                                                Item = o.UUID,
                                                Asset = o.AssetUUID,
                                                Entity = o.AssetType,
                                                Creator = o.CreatorID,
                                                Permissions =
                                                    Inventory.wasPermissionsToString(
                                                        o.Permissions),
                                                Inventory = o.InventoryType,
                                                Slot = (o as InventoryWearable).WearableType.ToString()
                                            }),
                                        corradeConfiguration.MaximumNotificationThreads);
                                    Inventory.UnWear(Client, CurrentOutfitFolder, o,
                                        corradeConfiguration.ServicesTimeout);
                                });
                        break;
                }
                RebakeTimer.Change(corradeConfiguration.RebakeDelay, 0);
            };
        }
    }
}