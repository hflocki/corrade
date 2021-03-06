///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CorradeConfigurationSharp;
using OpenMetaverse;
using wasOpenMetaverse;
using wasSharp;
using wasSharp.Timers;
using Reflection = wasSharp.Reflection;

namespace Corrade
{
    public partial class Corrade
    {
        public partial class CorradeCommands
        {
            public static readonly Action<Command.CorradeCommandParameters, Dictionary<string, string>>
                getgroupmemberdata =
                    (corradeCommandParameters, result) =>
                    {
                        if (
                            !HasCorradePermission(corradeCommandParameters.Group.UUID,
                                (int) Configuration.Permissions.Group))
                            throw new Command.ScriptException(Enumerations.ScriptError.NO_CORRADE_PERMISSIONS);
                        UUID groupUUID;
                        var target = wasInput(
                            KeyValue.Get(
                                wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.TARGET)),
                                corradeCommandParameters.Message));
                        switch (string.IsNullOrEmpty(target))
                        {
                            case false:
                                if (!UUID.TryParse(target, out groupUUID) &&
                                    !Resolvers.GroupNameToUUID(Client, target, corradeConfiguration.ServicesTimeout,
                                        corradeConfiguration.DataTimeout,
                                        new DecayingAlarm(corradeConfiguration.DataDecayType), ref groupUUID))
                                    throw new Command.ScriptException(Enumerations.ScriptError.GROUP_NOT_FOUND);
                                break;

                            default:
                                groupUUID = corradeCommandParameters.Group.UUID;
                                break;
                        }
                        UUID agentUUID;
                        if (
                            !UUID.TryParse(
                                wasInput(KeyValue.Get(
                                    wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.AGENT)),
                                    corradeCommandParameters.Message)),
                                out agentUUID) && !Resolvers.AgentNameToUUID(Client,
                                wasInput(
                                    KeyValue.Get(
                                        wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.FIRSTNAME)),
                                        corradeCommandParameters.Message)),
                                wasInput(
                                    KeyValue.Get(
                                        wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.LASTNAME)),
                                        corradeCommandParameters.Message)),
                                corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                                new DecayingAlarm(corradeConfiguration.DataDecayType),
                                ref agentUUID))
                            throw new Command.ScriptException(Enumerations.ScriptError.AGENT_NOT_FOUND);
                        Dictionary<UUID, GroupMember> groupMembers = null;
                        var groupMembersReceivedEvent = new ManualResetEventSlim(false);
                        var groupMembersRequestUUID = UUID.Zero;
                        EventHandler<GroupMembersReplyEventArgs> GroupMembersReplyEventHandler = (sender, args) =>
                        {
                            if (!groupMembersRequestUUID.Equals(args.RequestID)) return;
                            groupMembers = args.Members;
                            groupMembersReceivedEvent.Set();
                        };
                        Client.Groups.GroupMembersReply += GroupMembersReplyEventHandler;
                        groupMembersRequestUUID = Client.Groups.RequestGroupMembers(groupUUID);
                        if (
                            !groupMembersReceivedEvent.Wait(
                                (int) corradeConfiguration.ServicesTimeout))
                        {
                            Client.Groups.GroupMembersReply -= GroupMembersReplyEventHandler;
                            throw new Command.ScriptException(Enumerations.ScriptError.TIMEOUT_GETTING_GROUP_MEMBERS);
                        }
                        Client.Groups.GroupMembersReply -= GroupMembersReplyEventHandler;

                        GroupMember groupMember;
                        if (!groupMembers.TryGetValue(agentUUID, out groupMember))
                            throw new Command.ScriptException(Enumerations.ScriptError.AGENT_NOT_FOUND);
                        var data =
                            groupMember.GetStructuredData(
                                wasInput(
                                    KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(Command.ScriptKeys.DATA)),
                                        corradeCommandParameters.Message))).ToList();
                        if (data.Any())
                            result.Add(Reflection.GetNameFromEnumValue(Command.ResultKeys.DATA),
                                CSV.FromEnumerable(data));
                    };
        }
    }
}