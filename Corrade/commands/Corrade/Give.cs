///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CorradeConfiguration;
using OpenMetaverse;
using wasOpenMetaverse;
using wasSharp;
using Inventory = wasOpenMetaverse.Inventory;
using Reflection = wasSharp.Reflection;

namespace Corrade
{
    public partial class Corrade
    {
        public partial class CorradeCommands
        {
            public static Action<Command.CorradeCommandParameters, Dictionary<string, string>> give =
                (corradeCommandParameters, result) =>
                {
                    if (
                        !HasCorradePermission(corradeCommandParameters.Group.UUID,
                            (int) Configuration.Permissions.Inventory))
                    {
                        throw new Command.ScriptException(Enumerations.ScriptError.NO_CORRADE_PERMISSIONS);
                    }
                    var item = wasInput(
                        KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.ITEM)),
                            corradeCommandParameters.Message));
                    if (string.IsNullOrEmpty(item))
                    {
                        throw new Command.ScriptException(Enumerations.ScriptError.NO_ITEM_SPECIFIED);
                    }
                    InventoryBase inventoryBase;
                    UUID itemUUID;
                    switch (UUID.TryParse(item, out itemUUID))
                    {
                        case true:
                            inventoryBase =
                                Inventory.FindInventory<InventoryBase>(Client, Client.Inventory.Store.RootNode, itemUUID,
                                    corradeConfiguration.ServicesTimeout
                                    ).FirstOrDefault();
                            break;
                        default:
                            inventoryBase =
                                Inventory.FindInventory<InventoryBase>(Client, Client.Inventory.Store.RootNode, item,
                                    corradeConfiguration.ServicesTimeout)
                                    .FirstOrDefault();
                            break;
                    }
                    if (inventoryBase == null)
                    {
                        throw new Command.ScriptException(Enumerations.ScriptError.INVENTORY_ITEM_NOT_FOUND);
                    }
                    // If the requested item is an inventory item.
                    if (inventoryBase is InventoryItem)
                    {
                        // Sending an item requires transfer permission.
                        if (!(inventoryBase as InventoryItem).Permissions.OwnerMask.HasFlag(PermissionMask.Transfer))
                        {
                            throw new Command.ScriptException(Enumerations.ScriptError.NO_PERMISSIONS_FOR_ITEM);
                        }
                        // Set requested permissions if any on the item.
                        var permissions = wasInput(
                            KeyValue.Get(
                                wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.PERMISSIONS)),
                                corradeCommandParameters.Message));
                        if (!string.IsNullOrEmpty(permissions))
                        {
                            if (
                                !Inventory.wasSetInventoryItemPermissions(Client, inventoryBase as InventoryItem,
                                    permissions,
                                    corradeConfiguration.ServicesTimeout))
                            {
                                throw new Command.ScriptException(Enumerations.ScriptError.SETTING_PERMISSIONS_FAILED);
                            }
                        }
                    }
                    // If the requested item is a folder.
                    else if (inventoryBase is InventoryFolder)
                    {
                        // Keep track of all inventory items found.
                        var folderContents = new HashSet<InventoryBase>();
                        // Create the queue of folders.
                        var inventoryFolders = new BlockingQueue<InventoryFolder>();
                        // Enqueue the first folder (root).
                        inventoryFolders.Enqueue(inventoryBase as InventoryFolder);

                        var FolderUpdatedEvent = new ManualResetEvent(false);
                        EventHandler<FolderUpdatedEventArgs> FolderUpdatedEventHandler = (p, q) =>
                        {
                            folderContents.UnionWith(Client.Inventory.Store.GetContents(q.FolderID));
                            FolderUpdatedEvent.Set();
                        };

                        do
                        {
                            // Dequeue folder.
                            var folder = inventoryFolders.Dequeue();
                            lock (Locks.ClientInstanceInventoryLock)
                            {
                                Client.Inventory.FolderUpdated += FolderUpdatedEventHandler;
                                FolderUpdatedEvent.Reset();
                                Client.Inventory.RequestFolderContents(folder.UUID, Client.Self.AgentID, true, true,
                                    InventorySortOrder.ByDate);
                                if (!FolderUpdatedEvent.WaitOne((int) corradeConfiguration.ServicesTimeout, false))
                                {
                                    Client.Inventory.FolderUpdated -= FolderUpdatedEventHandler;
                                    throw new Command.ScriptException(
                                        Enumerations.ScriptError.TIMEOUT_GETTING_FOLDER_CONTENTS);
                                }
                                Client.Inventory.FolderUpdated -= FolderUpdatedEventHandler;
                            }
                        } while (inventoryFolders.Any());

                        // Check that if we are in SecondLife we would not transfer more items than SecondLife allows.
                        if (wasOpenMetaverse.Helpers.IsSecondLife(Client) &&
                            folderContents.Count >
                            wasOpenMetaverse.Constants.INVENTORY.MAXIMUM_FOLDER_TRANSFER_ITEM_COUNT)
                            throw new Command.ScriptException(
                                Enumerations.ScriptError.TRANSFER_WOULD_EXCEED_MAXIMUM_COUNT);

                        // Check that all the items to be transferred have transfer permission.
                        if (folderContents.AsParallel()
                            .Where(o => o is InventoryItem)
                            .Any(o => !(o as InventoryItem).Permissions.OwnerMask.HasFlag(PermissionMask.Transfer)))
                            throw new Command.ScriptException(Enumerations.ScriptError.NO_PERMISSIONS_FOR_ITEM);
                    }
                    switch (
                        Reflection.GetEnumValueFromName<Enumerations.Entity>(
                            wasInput(
                                KeyValue.Get(
                                    wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.ENTITY)),
                                    corradeCommandParameters.Message)).ToLowerInvariant()))
                    {
                        case Enumerations.Entity.AVATAR:
                            UUID agentUUID;
                            if (
                                !UUID.TryParse(
                                    wasInput(
                                        KeyValue.Get(
                                            wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.AGENT)),
                                            corradeCommandParameters.Message)), out agentUUID) &&
                                !Resolvers.AgentNameToUUID(Client,
                                    wasInput(
                                        KeyValue.Get(
                                            wasOutput(
                                                Reflection.GetNameFromEnumValue(Command.ScriptKeys.FIRSTNAME)),
                                            corradeCommandParameters.Message)),
                                    wasInput(
                                        KeyValue.Get(
                                            wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.LASTNAME)),
                                            corradeCommandParameters.Message)),
                                    corradeConfiguration.ServicesTimeout,
                                    corradeConfiguration.DataTimeout,
                                    new Time.DecayingAlarm(corradeConfiguration.DataDecayType),
                                    ref agentUUID))
                            {
                                throw new Command.ScriptException(Enumerations.ScriptError.AGENT_NOT_FOUND);
                            }
                            lock (Locks.ClientInstanceInventoryLock)
                            {
                                if (inventoryBase is InventoryItem)
                                {
                                    Client.Inventory.GiveItem(inventoryBase.UUID, inventoryBase.Name,
                                        (inventoryBase as InventoryItem).AssetType, agentUUID, true);
                                    break;
                                }
                                if (inventoryBase is InventoryFolder)
                                {
                                    Client.Inventory.GiveFolder(inventoryBase.UUID, inventoryBase.Name,
                                        AssetType.Folder, agentUUID, true);
                                }
                            }
                            break;
                        case Enumerations.Entity.OBJECT:
                            // Cannot transfer folders to objects.
                            if (inventoryBase is InventoryFolder)
                                throw new Command.ScriptException(Enumerations.ScriptError.INVALID_ITEM_TYPE);
                            float range;
                            if (
                                !float.TryParse(
                                    wasInput(
                                        KeyValue.Get(
                                            wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.RANGE)),
                                            corradeCommandParameters.Message)),
                                    out range))
                            {
                                range = corradeConfiguration.Range;
                            }
                            Primitive primitive = null;
                            var target = wasInput(KeyValue.Get(
                                wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.TARGET)),
                                corradeCommandParameters.Message));
                            if (string.IsNullOrEmpty(target))
                            {
                                throw new Command.ScriptException(Enumerations.ScriptError.NO_TARGET_SPECIFIED);
                            }
                            UUID targetUUID;
                            if (UUID.TryParse(target, out targetUUID))
                            {
                                if (
                                    !Services.FindPrimitive(Client,
                                        targetUUID,
                                        range,
                                        ref primitive,
                                        corradeConfiguration.DataTimeout))
                                {
                                    throw new Command.ScriptException(Enumerations.ScriptError.PRIMITIVE_NOT_FOUND);
                                }
                            }
                            else
                            {
                                if (
                                    !Services.FindPrimitive(Client,
                                        target,
                                        range,
                                        ref primitive,
                                        corradeConfiguration.DataTimeout))
                                {
                                    throw new Command.ScriptException(Enumerations.ScriptError.PRIMITIVE_NOT_FOUND);
                                }
                            }
                            lock (Locks.ClientInstanceInventoryLock)
                            {
                                Client.Inventory.UpdateTaskInventory(primitive.LocalID, inventoryBase as InventoryItem);
                            }
                            break;
                        default:
                            throw new Command.ScriptException(Enumerations.ScriptError.UNKNOWN_ENTITY);
                    }
                };
        }
    }
}