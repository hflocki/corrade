///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2016 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using wasSharp;
using wasSharp.Collections.Utilities;
using wasSharp.Timers;

namespace wasOpenMetaverse
{
    public static class Resolvers
    {
        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves a group name to an UUID by using the directory search.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="GroupName">the name of the group to resolve</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="dataTimeout">timeout for receiving answers from services</param>
        /// <param name="alarm">a decaying alarm for retrieving data</param>
        /// <param name="GroupUUID">an object in which to store the UUID of the group</param>
        /// <returns>true if the group name could be resolved to an UUID</returns>
        private static bool directGroupNameToUUID(GridClient Client, string GroupName,
            uint millisecondsTimeout, uint dataTimeout, DecayingAlarm alarm,
            ref UUID GroupUUID)
        {
            if (string.IsNullOrEmpty(GroupName))
                return false;
            var groupUUID = UUID.Zero;
            var requestUUID = UUID.Zero;
            EventHandler<DirGroupsReplyEventArgs> DirGroupsReplyDelegate = (sender, args) =>
            {
                if (!args.QueryID.Equals(requestUUID))
                    return;

                alarm.Alarm(dataTimeout);
                var groupSearchData =
                    args.MatchedGroups.AsParallel()
                        .FirstOrDefault(o => o.GroupName.Equals(GroupName, StringComparison.OrdinalIgnoreCase));
                if (!groupSearchData.Equals(default(DirectoryManager.GroupSearchData)))
                {
                    groupUUID = groupSearchData.GroupID;
                    alarm.Signal.Set();
                }
            };
            Client.Directory.DirGroupsReply += DirGroupsReplyDelegate;
            requestUUID = Client.Directory.StartGroupSearch(GroupName, 0, DirectoryManager.DirFindFlags.Groups);
            if (!alarm.Signal.WaitOne((int)millisecondsTimeout, true))
            {
                Client.Directory.DirGroupsReply -= DirGroupsReplyDelegate;
                return false;
            }
            Client.Directory.DirGroupsReply -= DirGroupsReplyDelegate;
            if (!groupUUID.Equals(UUID.Zero))
            {
                GroupUUID = groupUUID;
                return true;
            }
            return false;
        }

        /// <summary>
        ///     A wrapper for resolving group names to UUIDs by using Corrade's internal cache.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="GroupName">the name of the group to resolve</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="dataTimeout">timeout for receiving answers from services</param>
        /// <param name="alarm">a decaying alarm for retrieving data</param>
        /// <param name="GroupUUID">an object in which to store the UUID of the group</param>
        /// <returns>true if the group name could be resolved to an UUID</returns>
        public static bool GroupNameToUUID(GridClient Client, string GroupName, uint millisecondsTimeout,
            uint dataTimeout, DecayingAlarm alarm,
            ref UUID GroupUUID)
        {
            var group = Cache.GetGroup(GroupName);
            if (!group.Equals(default(Cache.Group)))
            {
                GroupUUID = group.UUID;
                return true;
            }
            var succeeded = directGroupNameToUUID(Client, GroupName, millisecondsTimeout, dataTimeout, alarm,
                ref GroupUUID);
            if (succeeded)
            {
                Cache.AddGroup(GroupName, GroupUUID);
            }
            return succeeded;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves an agent name to an agent UUID by searching the directory
        ///     services.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="FirstName">the first name of the agent</param>
        /// <param name="LastName">the last name of the agent</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="dataTimeout">timeout for receiving answers from services</param>
        /// <param name="alarm">a decaying alarm for retrieving data</param>
        /// <param name="AgentUUID">an object to store the agent UUID</param>
        /// <returns>true if the agent name could be resolved to an UUID</returns>
        private static bool directAgentNameToUUID(GridClient Client, string FirstName, string LastName,
            uint millisecondsTimeout,
            uint dataTimeout,
            DecayingAlarm alarm,
            ref UUID AgentUUID)
        {
            if (string.IsNullOrEmpty(FirstName) || string.IsNullOrEmpty(LastName))
                return false;
            var agentUUID = UUID.Zero;
            var requestUUID = UUID.Zero;
            EventHandler<DirPeopleReplyEventArgs> DirPeopleReplyDelegate = (sender, args) =>
            {
                if (!args.QueryID.Equals(requestUUID))
                    return;

                alarm.Alarm(dataTimeout);
                var agentSearchData =
                    args.MatchedPeople.AsParallel().FirstOrDefault(
                        o =>
                            o.FirstName.Equals(FirstName, StringComparison.OrdinalIgnoreCase) &&
                            o.LastName.Equals(LastName, StringComparison.OrdinalIgnoreCase));
                if (!agentSearchData.Equals(default(DirectoryManager.AgentSearchData)))
                {
                    agentUUID = agentSearchData.AgentID;
                    alarm.Signal.Set();
                }
            };
            Client.Directory.DirPeopleReply += DirPeopleReplyDelegate;
            requestUUID = Client.Directory.StartPeopleSearch(
                string.Format(Utils.EnUsCulture, "{0} {1}", FirstName, LastName), 0);
            if (!alarm.Signal.WaitOne((int)millisecondsTimeout, true))
            {
                Client.Directory.DirPeopleReply -= DirPeopleReplyDelegate;
                return false;
            }
            Client.Directory.DirPeopleReply -= DirPeopleReplyDelegate;
            if (!agentUUID.Equals(UUID.Zero))
            {
                AgentUUID = agentUUID;
                return true;
            }
            return false;
        }

        /// <summary>
        ///     A wrapper for looking up an agent name using Corrade's internal wasOpenMetaverse.Cache.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="FirstName">the first name of the agent</param>
        /// <param name="LastName">the last name of the agent</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="dataTimeout">timeout for receiving answers from services</param>
        /// <param name="alarm">a decaying alarm for retrieving data</param>
        /// <param name="AgentUUID">an object to store the agent UUID</param>
        /// <returns>true if the agent name could be resolved to an UUID</returns>
        public static bool AgentNameToUUID(GridClient Client, string FirstName, string LastName,
            uint millisecondsTimeout,
            uint dataTimeout,
            DecayingAlarm alarm,
            ref UUID AgentUUID)
        {
            var agent = Cache.GetAgent(FirstName, LastName);
            if (!agent.Equals(default(Cache.Agent)))
            {
                AgentUUID = agent.UUID;
                return true;
            }
            var succeeded = directAgentNameToUUID(Client, FirstName, LastName, millisecondsTimeout, dataTimeout, alarm,
                    ref AgentUUID);
            if (succeeded)
            {
                Cache.AddAgent(FirstName, LastName, AgentUUID);
            }
            return succeeded;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves a group name to an UUID by using the directory search.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="GroupName">a string to store the name to</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="GroupUUID">the UUID of the group to resolve</param>
        /// <returns>true if the group UUID could be resolved to an name</returns>
        private static bool directGroupUUIDToName(GridClient Client, UUID GroupUUID, uint millisecondsTimeout,
            ref string GroupName)
        {
            var groupName = string.Empty;
            var GroupProfileReceivedEvent = new ManualResetEventSlim(false);
            EventHandler<GroupProfileEventArgs> GroupProfileDelegate = (o, s) =>
            {
                if (s.Group.ID.Equals(GroupUUID))
                    groupName = s.Group.Name;
                GroupProfileReceivedEvent.Set();
            };
            Client.Groups.GroupProfile += GroupProfileDelegate;
            Client.Groups.RequestGroupProfile(GroupUUID);
            if (!GroupProfileReceivedEvent.Wait((int)millisecondsTimeout))
            {
                Client.Groups.GroupProfile -= GroupProfileDelegate;
                return false;
            }
            Client.Groups.GroupProfile -= GroupProfileDelegate;
            if (!string.IsNullOrEmpty(groupName))
            {
                GroupName = groupName;
                return true;
            }
            return false;
        }

        /// <summary>
        ///     A wrapper for resolving group names to UUIDs by using Corrade's internal cache.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="GroupName">a string to store the name to</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="GroupUUID">the UUID of the group to resolve</param>
        /// <returns>true if the group UUID could be resolved to an name</returns>
        public static bool GroupUUIDToName(GridClient Client, UUID GroupUUID, uint millisecondsTimeout,
            ref string GroupName)
        {
            var group = Cache.GetGroup(GroupUUID);
            if (!group.Equals(default(Cache.Group)))
            {
                GroupName = group.Name;
                return true;
            }
            Locks.ClientInstanceGroupsLock.EnterWriteLock();
            bool succeeded = directGroupUUIDToName(Client, GroupUUID, millisecondsTimeout, ref GroupName);
            Locks.ClientInstanceGroupsLock.ExitWriteLock();
            if (succeeded)
            {
                Cache.AddGroup(GroupName, GroupUUID);
            }
            return succeeded;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves an agent UUID to an agent name.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="AgentUUID">the UUID of the agent</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="AgentName">an object to store the name of the agent in</param>
        /// <returns>true if the UUID could be resolved to a name</returns>
        private static bool directAgentUUIDToName(GridClient Client, UUID AgentUUID, uint millisecondsTimeout,
            ref string AgentName)
        {
            if (AgentUUID.Equals(UUID.Zero))
                return false;
            var agentName = string.Empty;
            var UUIDNameReplyEvent = new ManualResetEventSlim(false);
            EventHandler<UUIDNameReplyEventArgs> UUIDNameReplyDelegate = (sender, args) =>
            {
                args.Names.TryGetValue(AgentUUID, out agentName);
                UUIDNameReplyEvent.Set();
            };
            Client.Avatars.UUIDNameReply += UUIDNameReplyDelegate;
            Client.Avatars.RequestAvatarName(AgentUUID);
            if (!UUIDNameReplyEvent.Wait((int)millisecondsTimeout))
            {
                Client.Avatars.UUIDNameReply -= UUIDNameReplyDelegate;
                return false;
            }
            Client.Avatars.UUIDNameReply -= UUIDNameReplyDelegate;
            if (!string.IsNullOrEmpty(agentName))
            {
                AgentName = agentName;
                return true;
            }
            return false;
        }

        /// <summary>
        ///     A wrapper for agent to UUID lookups using Corrade's internal cache.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="AgentUUID">the UUID of the agent</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="AgentName">an object to store the name of the agent in</param>
        /// <returns>true if the UUID could be resolved to a name</returns>
        public static bool AgentUUIDToName(GridClient Client, UUID AgentUUID, uint millisecondsTimeout,
            ref string AgentName)
        {
            var agent = Cache.GetAgent(AgentUUID);
            if (!agent.Equals(default(Cache.Agent)))
            {
                AgentName = string.Join(" ", agent.FirstName, agent.LastName);
                return true;
            }
            Locks.ClientInstanceAvatarsLock.EnterWriteLock();
            var succeeded = directAgentUUIDToName(Client, AgentUUID, millisecondsTimeout, ref AgentName);
            Locks.ClientInstanceAvatarsLock.ExitWriteLock();
            if (succeeded)
            {
                var fullName = new List<string>(Helpers.GetAvatarNames(AgentName));
                if (fullName.Any())
                {
                    Cache.AddAgent(fullName.First(), fullName.Last(), AgentUUID);
                }
            }
            return succeeded;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2017 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves an agent UUID to an agent name.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="AgentUUIDs">a list of UUIDs to resolve</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <returns>a dictionary of UUIDs to names</returns>
        private static Dictionary<UUID, string> directAgentUUIDToName(GridClient Client, List<UUID> AgentUUIDs, uint millisecondsTimeout)
        {
            var resolvedNames = new Dictionary<UUID, string>();
            var UUIDNameReplyEvent = new ManualResetEventSlim(false);
            EventHandler<UUIDNameReplyEventArgs> UUIDNameReplyDelegate = (sender, args) =>
            {
                resolvedNames = args.Names;
                UUIDNameReplyEvent.Set();
            };
            Client.Avatars.UUIDNameReply += UUIDNameReplyDelegate;
            Client.Avatars.RequestAvatarNames(AgentUUIDs);
            if (!UUIDNameReplyEvent.Wait((int)millisecondsTimeout))
            {
                Client.Avatars.UUIDNameReply -= UUIDNameReplyDelegate;
                return new Dictionary<UUID, string>();
            }
            Client.Avatars.UUIDNameReply -= UUIDNameReplyDelegate;
            return resolvedNames;
        }

        /// <summary>
        ///     A wrapper for agent to UUID lookups using Corrade's internal cache.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="AgentUUIDs">a list of UUIDs to resolve</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <returns>a dictionary of UUIDs to names</returns>
        public static Dictionary<UUID, string> AgentUUIDToName(GridClient Client, List<UUID> AgentUUIDs, uint millisecondsTimeout)
        {
            var cachedNames = new Dictionary<UUID, string>();
            var lookupUUIDs = new HashSet<UUID>();
            var LockObject = new[] {
                new object(),
                new object()
            };

            // Do not look up resolved agents that are in the cache.
            AgentUUIDs.AsParallel().ForAll(agentUUID =>
            {
                var agent = Cache.GetAgent(agentUUID);
                switch (!agent.Equals(default(Cache.Agent)))
                {
                    case true:
                        lock (LockObject[0])
                        {
                            if (!cachedNames.ContainsKey(agentUUID))
                                cachedNames.Add(agentUUID, string.Join(" ", agent.FirstName, agent.LastName));
                        }
                        return;

                    default:
                        lock (LockObject[1])
                        {
                            if (!lookupUUIDs.Contains(agentUUID))
                                lookupUUIDs.Add(agentUUID);
                        }
                        return;
                }
            });

            // Look up agents that are not in the cache.
            var resolvedNames = new Dictionary<UUID, string>();
            if (lookupUUIDs.Any())
            {
                Locks.ClientInstanceAvatarsLock.EnterWriteLock();
                resolvedNames = directAgentUUIDToName(Client, lookupUUIDs.ToList(), millisecondsTimeout);
                Locks.ClientInstanceAvatarsLock.ExitWriteLock();

                // Add agents to the cache.
                Task.Run(() =>
                    resolvedNames.AsParallel().ForAll(o =>
                    {
                        var fullName = new List<string>(Helpers.GetAvatarNames(o.Value));
                        if (fullName.Any())
                        {
                            Cache.AddAgent(fullName.First(), fullName.Last(), o.Key);
                        }
                    }));
            }

            return cachedNames.Union(resolvedNames).AsParallel()
                .GroupBy(o => o.Key)
                .Select(o => o.FirstOrDefault())
                .ToDictionary(o => o.Key, o => o.Value);
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves a role name to a role UUID.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="RoleName">the name of the role to be resolved to an UUID</param>
        /// <param name="GroupUUID">the UUID of the group to query for the role UUID</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="RoleUUID">an UUID object to store the role UUID in</param>
        /// <returns>true if the role could be found</returns>
        public static bool RoleNameToUUID(GridClient Client, string RoleName, UUID GroupUUID, uint millisecondsTimeout,
            ref UUID RoleUUID)
        {
            if (string.IsNullOrEmpty(RoleName))
                return false;
            if (RoleName.Equals(Constants.GROUPS.EVERYONE_ROLE_NAME, StringComparison.Ordinal))
            {
                RoleUUID = UUID.Zero;
                return true;
            }
            var GroupRoleDataReceivedEvent = new ManualResetEventSlim(false);
            var roleUUID = UUID.Zero;
            var requestUUID = UUID.Zero;
            EventHandler<GroupRolesDataReplyEventArgs> GroupRoleDataReplyDelegate = (sender, args) =>
            {
                if (!args.RequestID.Equals(requestUUID) || !args.GroupID.Equals(GroupUUID))
                    return;
                roleUUID =
                    args.Roles.AsParallel().Where(o => o.Value.Name.Equals(RoleName, StringComparison.Ordinal))
                        .Select(o => o.Key)
                        .FirstOrDefault();
                GroupRoleDataReceivedEvent.Set();
            };
            Client.Groups.GroupRoleDataReply += GroupRoleDataReplyDelegate;
            requestUUID = Client.Groups.RequestGroupRoles(GroupUUID);
            if (!GroupRoleDataReceivedEvent.Wait((int)millisecondsTimeout))
            {
                Client.Groups.GroupRoleDataReply -= GroupRoleDataReplyDelegate;
                return false;
            }
            Client.Groups.GroupRoleDataReply -= GroupRoleDataReplyDelegate;
            if (!roleUUID.Equals(UUID.Zero))
            {
                RoleUUID = roleUUID;
                return true;
            }
            return false;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves a role name to a role UUID.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="RoleUUID">the UUID of the role to be resolved to a name</param>
        /// <param name="GroupUUID">the UUID of the group to query for the role name</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="roleName">a string object to store the role name in</param>
        /// <returns>true if the role could be resolved</returns>
        public static bool RoleUUIDToName(GridClient Client, UUID RoleUUID, UUID GroupUUID, uint millisecondsTimeout,
            ref string roleName)
        {
            if (RoleUUID.Equals(UUID.Zero))
            {
                roleName = Constants.GROUPS.EVERYONE_ROLE_NAME;
                return true;
            }
            var GroupRoleDataReceivedEvent = new ManualResetEventSlim(false);
            var groupRole = new GroupRole();
            var requestUUID = UUID.Zero;
            EventHandler<GroupRolesDataReplyEventArgs> GroupRoleDataReplyDelegate = (sender, args) =>
            {
                if (!args.RequestID.Equals(requestUUID) || !args.GroupID.Equals(GroupUUID))
                    return;
                args.Roles.TryGetValue(RoleUUID, out groupRole);
                GroupRoleDataReceivedEvent.Set();
            };
            Client.Groups.GroupRoleDataReply += GroupRoleDataReplyDelegate;
            requestUUID = Client.Groups.RequestGroupRoles(GroupUUID);
            if (!GroupRoleDataReceivedEvent.Wait((int)millisecondsTimeout))
            {
                Client.Groups.GroupRoleDataReply -= GroupRoleDataReplyDelegate;
                return false;
            }
            Client.Groups.GroupRoleDataReply -= GroupRoleDataReplyDelegate;
            if (!groupRole.Equals(default(GroupRole)))
            {
                roleName = groupRole.Name;
                return true;
            }
            return false;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves a region name to a region handle.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="name">the name of the region</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="regionHandle">the found region handle</param>
        /// <returns>true if the region name could be resolved</returns>
        private static bool directRegionNameToHandle(GridClient Client, string name, uint millisecondsTimeout,
            ref ulong regionHandle)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            var GridRegionEvent = new ManualResetEventSlim(false);
            ulong localRegionHandle = 0;
            EventHandler<GridRegionEventArgs> GridRegionEventHandler =
                (sender, args) =>
                {
                    if (!string.Equals(name, args.Region.Name, StringComparison.OrdinalIgnoreCase))
                        return;
                    localRegionHandle = args.Region.RegionHandle;
                    GridRegionEvent.Set();
                };
            Client.Grid.GridRegion += GridRegionEventHandler;
            Client.Grid.RequestMapRegion(name, GridLayerType.Objects);
            if (!GridRegionEvent.Wait((int)millisecondsTimeout))
            {
                Client.Grid.GridRegion -= GridRegionEventHandler;
                return false;
            }
            Client.Grid.GridRegion -= GridRegionEventHandler;
            if (!localRegionHandle.Equals(0))
            {
                regionHandle = localRegionHandle;
                return true;
            }
            return false;
        }

        /// <summary>
        ///     A wrapper for region name to region handle lookups using caching.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="name">the name of the region</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="regionHandle">the found region handle</param>
        /// <returns>true if the region name could be resolved</returns>
        public static bool RegionNameToHandle(GridClient Client, string name, uint millisecondsTimeout,
            ref ulong regionHandle)
        {
            var region = Cache.GetRegion(name);
            if (!region.Equals(default(Cache.Region)))
            {
                regionHandle = region.Handle;
                // Region cache is weakly volatile so we need to check cached regions.
                new Thread(() =>
                {
                    ulong updateHandle = 0;
                    Locks.ClientInstanceGridLock.EnterReadLock();
                    var resolved = directRegionNameToHandle(Client, name, millisecondsTimeout, ref updateHandle);
                    Locks.ClientInstanceGridLock.ExitReadLock();

                    if (!resolved || region.Handle.Equals(updateHandle)) return;
                    Cache.UpdateRegion(name, updateHandle);
                })
                { IsBackground = true, Priority = ThreadPriority.Lowest }.Start();
                return true;
            }

            Locks.ClientInstanceGridLock.EnterReadLock();
            var succeeded = directRegionNameToHandle(Client, name, millisecondsTimeout, ref regionHandle);
            Locks.ClientInstanceGridLock.ExitReadLock();
            if (succeeded)
            {
                Cache.UpdateRegion(name, regionHandle);
            }
            return succeeded;
        }

        ///////////////////////////////////////////////////////////////////////////
        //    Copyright (C) 2013 Wizardry and Steamworks - License: GNU GPLv3    //
        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Resolves a region UUID to a region handle.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="regionUUID">the UUID of the region</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="regionHandle">the found region handle</param>
        /// <returns>true if the region UUID could be resolved</returns>
        private static bool directRegionUUIDToHandle(GridClient Client, UUID regionUUID, uint millisecondsTimeout,
            ref ulong regionHandle)
        {
            if (regionUUID.Equals(UUID.Zero))
                return false;
            var GridRegionEvent = new ManualResetEventSlim(false);
            ulong localRegionHandle = 0;
            EventHandler<RegionHandleReplyEventArgs> GridRegionEventHandler =
                (sender, args) =>
                {
                    localRegionHandle = args.RegionHandle;
                    GridRegionEvent.Set();
                };
            Client.Grid.RegionHandleReply += GridRegionEventHandler;
            Client.Grid.RequestRegionHandle(regionUUID);
            if (!GridRegionEvent.Wait((int)millisecondsTimeout))
            {
                Client.Grid.RegionHandleReply -= GridRegionEventHandler;
                return false;
            }
            Client.Grid.RegionHandleReply -= GridRegionEventHandler;
            if (!localRegionHandle.Equals(0))
            {
                regionHandle = localRegionHandle;
                return true;
            }
            return false;
        }

        /// <summary>
        ///     A wrapper for region UUID to region handle lookups using caching.
        /// </summary>
        /// <param name="Client">the OpenMetaverse grid client</param>
        /// <param name="regionUUID">the UUID of the region</param>
        /// <param name="millisecondsTimeout">timeout for the search in milliseconds</param>
        /// <param name="regionHandle">the found region handle</param>
        /// <returns>true if the region UUID could be resolved</returns>
        public static bool RegionUUIDToHandle(GridClient Client, UUID regionUUID, uint millisecondsTimeout,
            ref ulong regionHandle)
        {
            var region = Cache.GetRegion(regionUUID);
            if (!region.Equals(default(Cache.Region)))
            {
                regionHandle = region.Handle;
                // Region cache is weakly volatile so we need to check cached regions.
                new Thread(() =>
                {
                    ulong updateHandle = 0;
                    Locks.ClientInstanceGridLock.EnterReadLock();
                    var resolved = directRegionUUIDToHandle(Client, regionUUID, millisecondsTimeout,
                        ref updateHandle);
                    Locks.ClientInstanceGridLock.ExitReadLock();

                    if (!resolved || region.Handle.Equals(updateHandle)) return;
                    Cache.UpdateRegion(regionUUID, updateHandle);
                })
                { IsBackground = true, Priority = ThreadPriority.Lowest }.Start();
                return true;
            }

            Locks.ClientInstanceGridLock.EnterReadLock();
            var succeeded = directRegionUUIDToHandle(Client, regionUUID, millisecondsTimeout, ref regionHandle);
            Locks.ClientInstanceGridLock.ExitReadLock();
            if (succeeded)
            {
                Cache.UpdateRegion(regionUUID, regionHandle);
            }
            return succeeded;
        }
    }
}
