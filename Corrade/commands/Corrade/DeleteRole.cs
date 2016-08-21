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

namespace Corrade
{
    public partial class Corrade
    {
        public partial class CorradeCommands
        {
            public static Action<CorradeCommandParameters, Dictionary<string, string>> deleterole =
                (corradeCommandParameters, result) =>
                {
                    if (
                        !HasCorradePermission(corradeCommandParameters.Group.UUID, (int) Configuration.Permissions.Group))
                    {
                        throw new ScriptException(ScriptError.NO_CORRADE_PERMISSIONS);
                    }
                    var currentGroups = Enumerable.Empty<UUID>();
                    if (
                        !Services.GetCurrentGroups(Client, corradeConfiguration.ServicesTimeout,
                            ref currentGroups))
                    {
                        throw new ScriptException(ScriptError.COULD_NOT_GET_CURRENT_GROUPS);
                    }
                    if (!new HashSet<UUID>(currentGroups).Contains(corradeCommandParameters.Group.UUID))
                    {
                        throw new ScriptException(ScriptError.NOT_IN_GROUP);
                    }
                    if (
                        !Services.HasGroupPowers(Client, Client.Self.AgentID, corradeCommandParameters.Group.UUID,
                            GroupPowers.DeleteRole,
                            corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                            new Time.DecayingAlarm(corradeConfiguration.DataDecayType)) ||
                        !Services.HasGroupPowers(Client, Client.Self.AgentID, corradeCommandParameters.Group.UUID,
                            GroupPowers.RemoveMember,
                            corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                            new Time.DecayingAlarm(corradeConfiguration.DataDecayType)))
                    {
                        throw new ScriptException(ScriptError.NO_GROUP_POWER_FOR_COMMAND);
                    }
                    var role =
                        wasInput(KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.ROLE)),
                            corradeCommandParameters.Message));
                    UUID roleUUID;
                    if (!UUID.TryParse(role, out roleUUID) &&
                        !Resolvers.RoleNameToUUID(Client, role, corradeCommandParameters.Group.UUID,
                            corradeConfiguration.ServicesTimeout,
                            ref roleUUID))
                    {
                        throw new ScriptException(ScriptError.ROLE_NOT_FOUND);
                    }
                    if (roleUUID.Equals(UUID.Zero))
                    {
                        throw new ScriptException(ScriptError.CANNOT_DELETE_THE_EVERYONE_ROLE);
                    }
                    var targetGroup = new Group();
                    if (
                        !Services.RequestGroup(Client, corradeCommandParameters.Group.UUID,
                            corradeConfiguration.ServicesTimeout,
                            ref targetGroup))
                    {
                        throw new ScriptException(ScriptError.GROUP_NOT_FOUND);
                    }
                    if (targetGroup.OwnerRole.Equals(roleUUID))
                    {
                        throw new ScriptException(ScriptError.CANNOT_REMOVE_OWNER_ROLE);
                    }
                    // remove members from role
                    var GroupRoleMembersReplyEvent = new ManualResetEvent(false);
                    var groupRolesMembersRequestUUID = UUID.Zero;
                    EventHandler<GroupRolesMembersReplyEventArgs> GroupRolesMembersEventHandler = (sender, args) =>
                    {
                        if (!groupRolesMembersRequestUUID.Equals(args.RequestID)) return;
                        args.RolesMembers
                            .AsParallel()
                            .Where(o => o.Key.Equals(roleUUID))
                            .ForAll(
                                o =>
                                    Client.Groups.RemoveFromRole(corradeCommandParameters.Group.UUID, roleUUID, o.Value));
                        GroupRoleMembersReplyEvent.Set();
                    };
                    Client.Groups.GroupRoleMembersReply += GroupRolesMembersEventHandler;
                    groupRolesMembersRequestUUID =
                        Client.Groups.RequestGroupRolesMembers(corradeCommandParameters.Group.UUID);
                    if (!GroupRoleMembersReplyEvent.WaitOne((int) corradeConfiguration.ServicesTimeout, false))
                    {
                        Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                        throw new ScriptException(ScriptError.TIMEOUT_EJECTING_AGENT);
                    }
                    Client.Groups.GroupRoleMembersReply -= GroupRolesMembersEventHandler;
                    lock (Locks.ClientInstanceGroupsLock)
                    {
                        Client.Groups.DeleteRole(corradeCommandParameters.Group.UUID, roleUUID);
                    }
                };
        }
    }
}