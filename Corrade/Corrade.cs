///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

#region

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceModel.Syndication;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using BayesSharp;
using CommandLine;
using Corrade.Constants;
using Corrade.Events;
using Corrade.HTTP;
using Corrade.Source;
using Corrade.Source.WebForms.SecondLife;
using Corrade.Structures;
using Corrade.Structures.Effects;
using CorradeConfigurationSharp;
using Jint;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using LanguageDetection;
using OpenMetaverse;
using OpenMetaverse.Assets;
using Syn.Bot.Siml;
using Syn.Bot.Siml.Events;
using wasOpenMetaverse;
using wasSharp;
using wasSharp.Collections.Generic;
using wasSharp.Collections.Specialized;
using wasSharp.Collections.Utilities;
using wasSharp.Timers;
using wasSharp.Web;
using wasSharp.Web.Utilities;
using wasSharpNET.Console;
using wasSharpNET.Cryptography;
using wasSharpNET.Diagnostics;
using wasSharpNET.Network.TCP;
using wasSharpNET.Serialization;
using static Corrade.Command;
using Group = OpenMetaverse.Group;
using GroupNotice = Corrade.Structures.GroupNotice;
using Inventory = wasOpenMetaverse.Inventory;
using Logger = OpenMetaverse.Logger;
using Parallel = System.Threading.Tasks.Parallel;
using ReaderWriterLockSlim = System.Threading.ReaderWriterLockSlim;
using Reflection = wasSharp.Reflection;
using ThreadState = System.Threading.ThreadState;
using Timer = wasSharp.Timers.Timer;

#endregion

namespace Corrade
{
    public partial class Corrade : ServiceBase
    {
        public delegate bool EventHandler(NativeMethods.CtrlType ctrlType);

        /// <summary>
        ///     log4net Log hierarchy.
        /// </summary>
        private static readonly Hierarchy LogHierarchy = (Hierarchy) LogManager.GetRepository();

        /// <summary>
        ///     Corrade logger.
        /// </summary>
        private static ILog CorradeLog;

        /// <summary>
        ///     OpenMetaverse logger.
        /// </summary>
        private static ILog OpenMetaverseLog;

        /// <summary>
        ///     Semaphores that sense the state of the connection. When any of these semaphores fail,
        ///     Corrade does not consider itself connected anymore and terminates.
        /// </summary>
        private static readonly Dictionary<char, ManualResetEvent> ConnectionSemaphores = new Dictionary
            <char, ManualResetEvent>
            {
                {'l', new ManualResetEvent(false)},
                {'s', new ManualResetEvent(false)},
                {'u', new ManualResetEvent(false)},
                {'c', new ManualResetEvent(false)}
            };

        /// <summary>
        ///     A map of notification name to notification.
        /// </summary>
        public static readonly Dictionary<string, Action<NotificationParameters, Dictionary<string, string>>>
            corradeNotifications = typeof(CorradeNotifications).GetFields(BindingFlags.Static |
                                                                          BindingFlags.Public)
                .AsParallel()
                .Where(
                    o =>
                        o.FieldType ==
                        typeof(Action<NotificationParameters, Dictionary<string, string>>))
                .ToDictionary(
                    o => string.Intern(o.Name), o =>
                        (Action<NotificationParameters, Dictionary<string, string>>) o.GetValue(null));

        /// <summary>
        ///     A map of Corrade command name to Corrade command.
        /// </summary>
        public static readonly Dictionary<string, Action<CorradeCommandParameters, Dictionary<string, string>>>
            corradeCommands = typeof(CorradeCommands).GetFields(BindingFlags.Static | BindingFlags.Public)
                .AsParallel()
                .Where(
                    o =>
                        o.FieldType ==
                        typeof(Action<CorradeCommandParameters, Dictionary<string, string>>))
                .ToDictionary(
                    o => string.Intern(o.Name), o =>
                        (Action<CorradeCommandParameters, Dictionary<string, string>>) o.GetValue(null));

        /// <summary>
        ///     Holds all the active RLV rules.
        /// </summary>
        public static readonly ConcurrentHashSet<wasOpenMetaverse.RLV.RLVRule> RLVRules =
            new ConcurrentHashSet<wasOpenMetaverse.RLV.RLVRule>();

        /// <summary>
        ///     A map of RLV behavior name to RLV behavior.
        /// </summary>
        public static readonly Dictionary<string, Action<string, wasOpenMetaverse.RLV.RLVRule, UUID>> rlvBehaviours =
            typeof
                    (RLVBehaviours).GetFields(BindingFlags.Static | BindingFlags.Public)
                .AsParallel()
                .Where(
                    o =>
                        o.FieldType ==
                        typeof(Action<string, wasOpenMetaverse.RLV.RLVRule, UUID>))
                .ToDictionary(
                    o => string.Intern(o.Name), o =>
                        (Action<string, wasOpenMetaverse.RLV.RLVRule, UUID>) o.GetValue(null));

        public static HashSet<string> CorradeResultKeys =
            new HashSet<string>(Reflection.GetEnumNames<ResultKeys>().AsParallel().Select(o => string.Intern(o)));

        public static HashSet<string> CorradeScriptKeys =
            new HashSet<string>(Reflection.GetEnumNames<ScriptKeys>().AsParallel().Select(o => string.Intern(o)));

        public static Configuration corradeConfiguration = new Configuration();

        public static readonly GridClient Client = new GridClient
        {
            // Set the initial client configuration.
            Settings =
            {
                ALWAYS_REQUEST_PARCEL_ACL = true,
                ALWAYS_DECODE_OBJECTS = true,
                ALWAYS_REQUEST_OBJECTS = true,
                SEND_AGENT_APPEARANCE = true,
                AVATAR_TRACKING = true,
                OBJECT_TRACKING = true,
                PARCEL_TRACKING = true,
                ALWAYS_REQUEST_PARCEL_DWELL = true,
                // Smoother movement for autopilot.
                SEND_AGENT_UPDATES = true,
                DISABLE_AGENT_UPDATE_DUPLICATE_CHECK = true,
                ENABLE_CAPS = true,
                // Inventory settings.
                FETCH_MISSING_INVENTORY = true,
                HTTP_INVENTORY = true,
                USE_ASSET_CACHE = true,
                // More precision for object and avatar tracking updates.
                USE_INTERPOLATION_TIMER = true,
                // Transfer textures over HTTP if poss,ble.
                USE_HTTP_TEXTURES = true,
                // Needed for commands dealing with terrain height.
                STORE_LAND_PATCHES = true,
                // Decode simulator statistics.
                ENABLE_SIMSTATS = true,
                // Send pings for lag measurement.
                SEND_PINGS = true,
                // Throttling.
                SEND_AGENT_THROTTLE = true
            }
        };

        public static string InstalledServiceName;
        private static Thread programThread;
        private static Thread TCPNotificationsThread;
        private static readonly ManualResetEventSlim CallbackThreadState = new ManualResetEventSlim(false);
        private static readonly ManualResetEventSlim NotificationThreadState = new ManualResetEventSlim(false);
        private static readonly ManualResetEventSlim TCPNotificationsThreadState = new ManualResetEventSlim(false);
        private static TcpListener TCPListener;

        private static CorradeHTTPServer CorradeHTTPServer;

        private static NucleusHTTPServer NucleusHTTPServer;

        private static readonly wasSharp.Collections.Generic.CircularQueue<string> StartLocationQueue =
            new wasSharp.Collections.Generic.CircularQueue<string>();

        private static readonly object CorradeLastExecStatusFileLock = new object();
        private static LastExecStatus _CorradeLastExecStatus = LastExecStatus.Normal;

        private static readonly object CorradeScriptedAgentStatusFileLock = new object();
        private static bool? _CorradeScriptedAgentStatus;

        private static InventoryFolder CurrentOutfitFolder;
        private static SimlBot SynBot = new SimlBot();
        private static BotUser SynBotUser = SynBot.MainUser;
        private static readonly LanguageDetector languageDetector = new LanguageDetector();
        private static readonly FileSystemWatcher SIMLBotConfigurationWatcher = new FileSystemWatcher();
        private static readonly FileSystemWatcher ConfigurationWatcher = new FileSystemWatcher();
        private static readonly FileSystemWatcher NotificationsWatcher = new FileSystemWatcher();
        private static readonly FileSystemWatcher SchedulesWatcher = new FileSystemWatcher();
        private static readonly FileSystemWatcher GroupFeedWatcher = new FileSystemWatcher();
        private static readonly FileSystemWatcher GroupSoftBansWatcher = new FileSystemWatcher();
        private static readonly object SIMLBotLock = new object();
        public static readonly object ConfigurationFileLock = new object();
        private static readonly object ClientLogFileLock = new object();
        private static readonly object GroupLogFileLock = new object();
        private static readonly object LocalLogFileLock = new object();
        private static readonly object OwnerSayLogFileLock = new object();
        private static readonly object RegionLogFileLock = new object();
        private static readonly object InstantMessageLogFileLock = new object();
        private static readonly object ConferenceMessageLogFileLock = new object();

        private static readonly object GroupMembersStateFileLock = new object();
        private static readonly object GroupSoftBansStateFileLock = new object();
        private static readonly object GroupSchedulesStateFileLock = new object();
        private static readonly object GroupNotificationsStateFileLock = new object();
        private static readonly object MovementStateFileLock = new object();
        private static readonly object ConferencesStateFileLock = new object();
        private static readonly object GroupFeedsStateFileLock = new object();
        private static readonly object GroupCookiesStateFileLock = new object();

        private static readonly TimedThrottle TimedTeleportThrottle =
            new TimedThrottle(wasOpenMetaverse.Constants.TELEPORTS.THROTTLE.MAX_TELEPORTS,
                wasOpenMetaverse.Constants.TELEPORTS.THROTTLE.GRACE_SECONDS);

        private static readonly object GroupNotificationsLock = new object();

        private static HashSet<Notifications> GroupNotifications =
            new HashSet<Notifications>();

        private static readonly Dictionary<Configuration.Notifications, HashSet<Notifications>> GroupNotificationsCache
            =
            new Dictionary<Configuration.Notifications, HashSet<Notifications>>();

        private static readonly Dictionary<UUID, InventoryOffer> InventoryOffers =
            new Dictionary<UUID, InventoryOffer>();

        private static readonly object InventoryOffersLock = new object();

        private static readonly object InventoryRequestsLock = new object();

        private static readonly
            SerializableDictionary<string, SerializableDictionary<UUID, string>> GroupFeeds =
                new SerializableDictionary<string, SerializableDictionary<UUID, string>>();

        private static readonly object GroupFeedsLock = new object();

        private static readonly BlockingQueue<CallbackQueueElement> CallbackQueue =
            new BlockingQueue<CallbackQueueElement>();

        private static readonly BlockingQueue<NotificationQueueElement> NotificationQueue =
            new BlockingQueue<NotificationQueueElement>();

        public static readonly Dictionary<UUID, BlockingQueue<NotificationQueueElement>> NucleusNotificationQueue =
            new Dictionary<UUID, BlockingQueue<NotificationQueueElement>>();

        public static readonly object NucleusNotificationQueueLock = new object();

        private static readonly BlockingQueue<NotificationTCPQueueElement> NotificationTCPQueue =
            new BlockingQueue<NotificationTCPQueueElement>();

        private static readonly Dictionary<UUID, GroupInvite> GroupInvites = new Dictionary<UUID, GroupInvite>();
        private static readonly object GroupInvitesLock = new object();
        private static readonly HashSet<GroupNotice> GroupNotices = new HashSet<GroupNotice>();
        private static readonly object GroupNoticeLock = new object();
        private static readonly Dictionary<UUID, TeleportLure> TeleportLures = new Dictionary<UUID, TeleportLure>();
        private static readonly object TeleportLuresLock = new object();

        // permission requests can be identical
        private static readonly List<ScriptPermissionRequest> ScriptPermissionRequests =
            new List<ScriptPermissionRequest>();

        private static readonly object ScriptPermissionsRequestsLock = new object();

        // script dialogs can be identical
        private static readonly Dictionary<UUID, ScriptDialog> ScriptDialogs = new Dictionary<UUID, ScriptDialog>();

        private static readonly object ScriptDialogsLock = new object();

        private static readonly HashSet<KeyValuePair<UUID, int>> CurrentAnimations =
            new HashSet<KeyValuePair<UUID, int>>();

        private static readonly object CurrentAnimationsLock = new object();

        private static readonly SerializableDictionary<UUID, ObservableHashSet<UUID>>
            GroupMembers =
                new SerializableDictionary<UUID, ObservableHashSet<UUID>>();

        private static readonly object GroupMembersLock = new object();

        public static readonly SerializableDictionary<UUID, ObservableHashSet<SoftBan>>
            GroupSoftBans =
                new SerializableDictionary<UUID, ObservableHashSet<SoftBan>>();

        public static readonly object GroupSoftBansLock = new object();

        public static readonly Hashtable GroupWorkers = new Hashtable();
        private static readonly object GroupWorkersLock = new object();

        private static readonly Dictionary<UUID, InventoryBase> GroupDirectoryTrackers =
            new Dictionary<UUID, InventoryBase>();

        private static readonly object GroupDirectoryTrackersLock = new object();
        private static readonly HashSet<LookAtEffect> LookAtEffects = new HashSet<LookAtEffect>();

        private static readonly HashSet<PointAtEffect> PointAtEffects =
            new HashSet<PointAtEffect>();

        private static readonly HashSet<SphereEffect> SphereEffects = new HashSet<SphereEffect>();
        private static readonly object SphereEffectsLock = new object();
        private static readonly HashSet<BeamEffect> BeamEffects = new HashSet<BeamEffect>();
        private static readonly Dictionary<uint, Primitive> RadarObjects = new Dictionary<uint, Primitive>();
        private static readonly object LookAtEffectsLock = new object();
        private static readonly object PointAtEffectsLock = new object();
        private static readonly object RadarObjectsLock = new object();
        private static readonly object BeamEffectsLock = new object();
        private static readonly object InputFiltersLock = new object();
        private static readonly object OutputFiltersLock = new object();
        private static readonly HashSet<GroupSchedule> GroupSchedules = new HashSet<GroupSchedule>();
        private static readonly object GroupSchedulesLock = new object();
        private static readonly HashSet<Conference> Conferences = new HashSet<Conference>();
        private static readonly object ConferencesLock = new object();

        private static readonly Dictionary<UUID, CookieContainer> GroupCookieContainers =
            new Dictionary<UUID, CookieContainer>();

        private static readonly object GroupCookieContainersLock = new object();

        private static readonly Dictionary<UUID, wasHTTPClient> GroupHTTPClients =
            new Dictionary<UUID, wasHTTPClient>();

        private static readonly object GroupHTTPClientsLock = new object();

        private static readonly Dictionary<string, wasHTTPClient> HordeHTTPClients =
            new Dictionary<string, wasHTTPClient>();

        private static readonly object HordeHTTPClientsLock = new object();

        public static readonly Dictionary<UUID, BayesSimpleTextClassifier> GroupBayesClassifiers =
            new Dictionary<UUID, BayesSimpleTextClassifier>();

        public static readonly object GroupBayesClassifiersLock = new object();

        private static string CorradePOSTMediaType;

        private static readonly AES CorradeAES = new AES();

        private static readonly object RLVInventoryLock = new object();

        public static readonly Heartbeat CorradeHeartbeat = new Heartbeat();

        public static readonly ReaderWriterLockSlim CorradeHeartbeatLock =
            new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        public static readonly Random CorradeRandom = new Random();

        /// <summary>
        ///     Heartbeat timer.
        /// </summary>
        private static readonly Timer CorradeHeartBeatTimer = new Timer(() =>
        {
            // Send notification.

            CorradeHeartbeatLock.EnterReadLock();
            var heartbeatEventArgs = new HeartbeatEventArgs
            {
                ExecutingCommands = CorradeHeartbeat.ExecutingCommands,
                ExecutingRLVBehaviours = CorradeHeartbeat.ExecutingRLVBehaviours,
                ProcessedCommands = CorradeHeartbeat.ProcessedCommands,
                ProcessedRLVBehaviours = CorradeHeartbeat.ProcessedRLVBehaviours,
                AverageCPUUsage = CorradeHeartbeat.AverageCPUUsage,
                AverageRAMUsage = CorradeHeartbeat.AverageRAMUsage,
                Heartbeats = CorradeHeartbeat.Heartbeats,
                Uptime = CorradeHeartbeat.Uptime,
                Version = CorradeHeartbeat.Version
            };
            CorradeHeartbeatLock.ExitReadLock();
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Heartbeat, heartbeatEventArgs),
                corradeConfiguration.MaximumNotificationThreads);
        }, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        /// <summary>
        ///     Heartbeat logging.
        /// </summary>
        private static readonly Timer CorradeHeartBeatLogTimer = new Timer(() =>
        {
            // Log heartbeat data.
            CorradeHeartbeatLock.EnterReadLock();
            Feedback("Heartbeat",
                $"CPU: {CorradeHeartbeat.AverageCPUUsage}% RAM: {CorradeHeartbeat.AverageRAMUsage / 1024 / 1024:0.}MiB Uptime: {TimeSpan.FromSeconds(CorradeHeartbeat.Uptime).Days}d:{TimeSpan.FromSeconds(CorradeHeartbeat.Uptime).Hours}h:{TimeSpan.FromSeconds(CorradeHeartbeat.Uptime).Minutes}m Commands: {CorradeHeartbeat.ProcessedCommands} Behaviours: {CorradeHeartbeat.ProcessedRLVBehaviours}");
            CorradeHeartbeatLock.ExitReadLock();
        }, TimeSpan.Zero, TimeSpan.Zero);

        /// <summary>
        ///     Effects expiration timer.
        /// </summary>
        private static readonly Timer EffectsExpirationTimer = new Timer(() =>
        {
            lock (SphereEffectsLock)
            {
                SphereEffects.RemoveWhere(o => DateTime.Compare(DateTime.UtcNow, o.Termination) > 0);
            }
            lock (BeamEffectsLock)
            {
                BeamEffects.RemoveWhere(o => DateTime.Compare(DateTime.UtcNow, o.Termination) > 0);
            }
        }, TimeSpan.Zero, TimeSpan.Zero);

        /// <summary>
        ///     Group membership timer.
        /// </summary>
        private static readonly Timer GroupMembershipTimer = new Timer(() =>
        {
            Locks.ClientInstanceNetworkLock.EnterReadLock();
            if (!Client.Network.Connected ||
                Client.Network.CurrentSim != null && !Client.Network.CurrentSim.Caps.IsEventQueueRunning)
            {
                Locks.ClientInstanceNetworkLock.ExitReadLock();
                return;
            }
            Locks.ClientInstanceNetworkLock.ExitReadLock();

            // Expire any hard soft bans.
            lock (GroupSoftBansLock)
            {
                GroupSoftBans.AsParallel()
                    // Select only the groups to which we have the capability of changing the group access list.
                    .Where(o => Services.HasGroupPowers(Client, Client.Self.AgentID, o.Key,
                        GroupPowers.GroupBanAccess,
                        corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                        new DecayingAlarm(corradeConfiguration.DataDecayType)))
                    // Select group and all the soft bans that have expired.
                    .Select(o => new
                    {
                        Group = o.Key,
                        SoftBans = o.Value.AsParallel().Where(p =>
                        {
                            // Only process softbans with a set hard-ban time.
                            if (p.Time.Equals(0))
                                return false;
                            // Get the softban timestamp and covert to datetime.
                            DateTime lastBanDate;
                            if (
                                !DateTime.TryParseExact(p.Last, CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out lastBanDate))
                                return false;
                            // If the current time exceeds the hard-ban time then select the softban for processing.
                            return DateTime.Compare(lastBanDate.AddMinutes(p.Time), DateTime.UtcNow) < 0;
                        })
                    })

                    // Only select groups with non-empty soft bans matching previous criteria.
                    .Where(o => o.SoftBans.Any())
                    // Select only soft bans that are also group bans.
                    .Select(o =>
                    {
                        // Get current group bans.
                        var agents = new HashSet<UUID>();
                        Dictionary<UUID, DateTime> bannedAgents = null;
                        if (Services.GetGroupBans(Client, o.Group, corradeConfiguration.ServicesTimeout,
                                ref bannedAgents) && bannedAgents != null)
                            agents.UnionWith(bannedAgents.Keys);
                        return new
                        {
                            o.Group,
                            SoftBans = o.SoftBans.Where(p => agents.Contains(p.Agent))
                        };
                    })
                    // Unban all the agents with expired soft bans that are also group bans.
                    .ForAll(o =>
                    {
                        var GroupBanEvent = new ManualResetEventSlim(false);
                        Client.Groups.RequestBanAction(o.Group,
                            GroupBanAction.Unban, o.SoftBans.Select(p => p.Agent).ToArray(),
                            (sender, args) => { GroupBanEvent.Set(); });
                        if (!GroupBanEvent.Wait((int) corradeConfiguration.ServicesTimeout))
                            Feedback(
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.UNABLE_TO_LIFT_HARD_SOFT_BAN),
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ScriptError.TIMEOUT_MODIFYING_GROUP_BAN_LIST));
                    });
            }

            // Get current groups.
            var groups = Enumerable.Empty<UUID>();
            if (!Services.GetCurrentGroups(Client, corradeConfiguration.ServicesTimeout, ref groups))
                return;
            var currentGroups = new HashSet<UUID>(groups);
            // Remove groups that are not configured.
            currentGroups.RemoveWhere(o => !corradeConfiguration.Groups.Any(p => p.UUID.Equals(o)));

            // Bail if no configured groups are also joined.
            if (!currentGroups.Any())
                return;

            var membersGroups = new HashSet<UUID>();
            lock (GroupMembersLock)
            {
                membersGroups.UnionWith(GroupMembers.Keys);
            }
            // Remove groups no longer handled.
            membersGroups.AsParallel().ForAll(o =>
            {
                if (!currentGroups.Contains(o))
                    lock (GroupMembersLock)
                    {
                        GroupMembers[o].CollectionChanged -= HandleGroupMemberJoinPart;
                        GroupMembers.Remove(o);
                    }
            });
            // Add new groups to be handled.
            currentGroups.AsParallel().ForAll(o =>
            {
                lock (GroupMembersLock)
                {
                    if (!GroupMembers.ContainsKey(o))
                    {
                        GroupMembers.Add(o, new ObservableHashSet<UUID>());
                        GroupMembers[o].CollectionChanged += HandleGroupMemberJoinPart;
                    }
                }
            });

            var LockObject = new object();
            var groupMembersRequestUUIDs = new HashSet<UUID>();
            var GroupMembersReplyEvent = new AutoResetEvent(false);
            EventHandler<GroupMembersReplyEventArgs> HandleGroupMembersReplyDelegate = (sender, args) =>
            {
                lock (LockObject)
                {
                    switch (groupMembersRequestUUIDs.Contains(args.RequestID))
                    {
                        case true:
                            groupMembersRequestUUIDs.Remove(args.RequestID);
                            break;

                        default:
                            return;
                    }
                }

                lock (GroupMembersLock)
                {
                    if (GroupMembers.ContainsKey(args.GroupID))
                        switch (!GroupMembers[args.GroupID].Any())
                        {
                            case true:
                                GroupMembers[args.GroupID].UnionWith(args.Members.Values.Select(o => o.ID));
                                break;

                            default:
                                GroupMembers[args.GroupID].ExceptWith(GroupMembers[args.GroupID].AsParallel()
                                    .Where(o => !args.Members.Values.Any(p => p.ID.Equals(o))));
                                GroupMembers[args.GroupID].UnionWith(args.Members.Values.AsParallel()
                                    .Where(o => !GroupMembers[args.GroupID].Contains(o.ID))
                                    .Select(o => o.ID));
                                break;
                        }
                }
                GroupMembersReplyEvent.Set();
            };

            currentGroups.AsParallel().ForAll(o =>
            {
                Client.Groups.GroupMembersReply += HandleGroupMembersReplyDelegate;
                lock (LockObject)
                {
                    groupMembersRequestUUIDs.Add(Client.Groups.RequestGroupMembers(o));
                }
                GroupMembersReplyEvent.WaitOne((int) corradeConfiguration.ServicesTimeout, true);
                Client.Groups.GroupMembersReply -= HandleGroupMembersReplyDelegate;
            });

            // Save group members.
            SaveGroupMembersState.Invoke();
        }, TimeSpan.Zero, TimeSpan.Zero);

        /// <summary>
        ///     Group feeds timer.
        /// </summary>
        private static readonly Timer GroupFeedsTimer = new Timer(() =>
        {
            lock (GroupFeedsLock)
            {
                GroupFeeds.AsParallel().ForAll(o =>
                {
                    try
                    {
                        using (var reader = XmlReader.Create(o.Key))
                        {
                            SyndicationFeed.Load(reader)?.Items.AsParallel()
                                .Where(
                                    p =>
                                        p != null && p.Title != null && p.Summary != null &&
                                        p.PublishDate.CompareTo(
                                            DateTimeOffset.Now.Subtract(
                                                TimeSpan.FromMilliseconds(corradeConfiguration.FeedsUpdateInterval))) >
                                        0)
                                .ForAll(p =>
                                {
                                    o.Value.AsParallel().ForAll(q =>
                                    {
                                        CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                                            () => SendNotification(
                                                Configuration.Notifications.Feed,
                                                new FeedEventArgs
                                                {
                                                    Title = p.Title.Text,
                                                    Summary = p.Summary.Text,
                                                    Date = p.PublishDate,
                                                    Name = q.Value,
                                                    GroupUUID = q.Key
                                                }),
                                            corradeConfiguration.MaximumNotificationThreads);
                                    });
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.ERROR_LOADING_FEED),
                            o.Key,
                            ex.PrettyPrint());
                    }
                });
            }
        }, TimeSpan.Zero, TimeSpan.Zero);

        /// <summary>
        ///     Timer for SynBot.
        /// </summary>
        private static readonly Timer SynBotTimer = new Timer(() => { SynBot.Timer.PerformTick(); }, TimeSpan.Zero,
            TimeSpan.Zero);

        /// <summary>
        ///     Group schedules timer.
        /// </summary>
        private static readonly Timer GroupSchedulesTimer = new Timer(() =>
        {
            var groupSchedules = new HashSet<GroupSchedule>();
            lock (GroupSchedulesLock)
            {
                groupSchedules.UnionWith(GroupSchedules.AsParallel()
                    .Where(
                        o =>
                            DateTime.Compare(DateTime.UtcNow, o.At) >= 0));
            }
            if (groupSchedules.Any())
            {
                groupSchedules.AsParallel().ForAll(
                    o =>
                    {
                        // Spawn the command.
                        CorradeThreadPool[Threading.Enumerations.ThreadType.COMMAND].Spawn(
                            () => HandleCorradeCommand(o.Message, o.Sender, o.Identifier, o.Group),
                            corradeConfiguration.MaximumCommandThreads, o.Group.UUID,
                            corradeConfiguration.SchedulerExpiration);
                        lock (GroupSchedulesLock)
                        {
                            GroupSchedules.Remove(o);
                        }
                    });

                SaveGroupSchedulesState.Invoke();
            }
        }, TimeSpan.Zero, TimeSpan.Zero);

        /// <summary>
        ///     The various types of threads created by Corrade.
        /// </summary>
        public static readonly Dictionary<Threading.Enumerations.ThreadType, Threading.Thread> CorradeThreadPool =
            new Dictionary<Threading.Enumerations.ThreadType, Threading.Thread>
            {
                {
                    Threading.Enumerations.ThreadType.COMMAND,
                    new Threading.Thread(Threading.Enumerations.ThreadType.COMMAND)
                },
                {Threading.Enumerations.ThreadType.RLV, new Threading.Thread(Threading.Enumerations.ThreadType.RLV)},
                {
                    Threading.Enumerations.ThreadType.NOTIFICATION,
                    new Threading.Thread(Threading.Enumerations.ThreadType.NOTIFICATION)
                },
                {
                    Threading.Enumerations.ThreadType.INSTANT_MESSAGE,
                    new Threading.Thread(Threading.Enumerations.ThreadType.INSTANT_MESSAGE)
                },
                {Threading.Enumerations.ThreadType.LOG, new Threading.Thread(Threading.Enumerations.ThreadType.LOG)},
                {Threading.Enumerations.ThreadType.POST, new Threading.Thread(Threading.Enumerations.ThreadType.POST)},
                {
                    Threading.Enumerations.ThreadType.PRELOAD,
                    new Threading.Thread(Threading.Enumerations.ThreadType.PRELOAD)
                },
                {
                    Threading.Enumerations.ThreadType.HORDE,
                    new Threading.Thread(Threading.Enumerations.ThreadType.HORDE)
                },
                {
                    Threading.Enumerations.ThreadType.SOFTBAN,
                    new Threading.Thread(Threading.Enumerations.ThreadType.SOFTBAN)
                },
                {
                    Threading.Enumerations.ThreadType.AUXILIARY,
                    new Threading.Thread(Threading.Enumerations.ThreadType.AUXILIARY)
                }
            };

        /// <summary>
        ///     Schedules a load of the configuration file.
        /// </summary>
        private static readonly Timer ConfigurationChangedTimer =
            new Timer(() =>
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.CONFIGURATION_FILE_MODIFIED));
                lock (ConfigurationFileLock)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.READING_CORRADE_CONFIGURATION));
                    try
                    {
                        using (var stream = new StreamReader(CORRADE_CONSTANTS.CONFIGURATION_FILE, Encoding.UTF8))
                        {
                            var loadedConfiguration = XmlSerializerCache.Deserialize<Configuration>(stream);
                            if (corradeConfiguration.EnableHorde)
                            {
                                corradeConfiguration.Groups.AsParallel()
                                    .Where(o => !loadedConfiguration.Groups.Any(p => p.UUID.Equals(o.UUID)))
                                    .ForAll(
                                        o =>
                                        {
                                            HordeDistributeConfigurationGroup(o,
                                                Configuration.HordeDataSynchronizationOption.Remove);
                                        });
                                loadedConfiguration.Groups.AsParallel()
                                    .Where(o => !corradeConfiguration.Groups.Any(p => p.UUID.Equals(o.UUID)))
                                    .Select(o => o).ForAll(o =>
                                    {
                                        HordeDistributeConfigurationGroup(o,
                                            Configuration.HordeDataSynchronizationOption.Add);
                                    });
                            }
                            corradeConfiguration = loadedConfiguration;
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_CONFIGURATION),
                            ex.PrettyPrint());
                        return;
                    }

                    // Check configuration file compatiblity.
                    Version minimalConfig;
                    Version versionConfig;
                    if (
                        !Version.TryParse(CORRADE_CONSTANTS.ASSEMBLY_CUSTOM_ATTRIBUTES["configuration"],
                            out minimalConfig) ||
                        !Version.TryParse(corradeConfiguration.Version, out versionConfig) ||
                        !minimalConfig.Major.Equals(versionConfig.Major) ||
                        !minimalConfig.Minor.Equals(versionConfig.Minor))
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.CONFIGURATION_FILE_VERSION_MISMATCH));

                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.READ_CORRADE_CONFIGURATION));
                }
                if (!corradeConfiguration.Equals(default(Configuration)))
                    UpdateDynamicConfiguration(corradeConfiguration);
            });

        /// <summary>
        ///     Schedules a load of the notifications file.
        /// </summary>
        private static readonly Timer NotificationsChangedTimer =
            new Timer(() =>
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.NOTIFICATIONS_FILE_MODIFIED));
                LoadNotificationState.Invoke();
            });

        /// <summary>
        ///     Schedules a load of the SIML configuration file.
        /// </summary>
        private static readonly Timer SIMLConfigurationChangedTimer =
            new Timer(() =>
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.SIML_CONFIGURATION_MODIFIED));
                CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                    () =>
                    {
                        lock (SIMLBotLock)
                        {
                            LoadChatBotFiles.Invoke();
                        }
                    });
            });

        /// <summary>
        ///     Schedules a load of the group schedules file.
        /// </summary>
        private static readonly Timer GroupSchedulesChangedTimer =
            new Timer(() =>
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.GROUP_SCHEDULES_FILE_MODIFIED));
                LoadGroupSchedulesState.Invoke();
            });

        /// <summary>
        ///     Schedules a load of the group feeds file.
        /// </summary>
        private static readonly Timer GroupFeedsChangedTimer =
            new Timer(() =>
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.GROUP_FEEDS_FILE_MODIFIED));
                LoadGroupFeedState.Invoke();
            });

        /// <summary>
        ///     Schedules a load of the group soft bans file.
        /// </summary>
        private static readonly Timer GroupSoftBansChangedTimer =
            new Timer(() =>
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.GROUP_SOFT_BANS_FILE_MODIFIED));
                LoadGroupSoftBansState.Invoke();
            });

        /// <summary>
        ///     Global rebake timer.
        /// </summary>
        private static readonly Timer RebakeTimer = new Timer(() =>
        {
            Locks.ClientInstanceAppearanceLock.EnterWriteLock();
            var AppearanceSetEvent = new ManualResetEventSlim(false);
            EventHandler<AppearanceSetEventArgs> HandleAppearanceSet =
                (sender, args) => { AppearanceSetEvent.Set(); };
            Client.Appearance.AppearanceSet += HandleAppearanceSet;
            Client.Appearance.RequestSetAppearance(true);
            AppearanceSetEvent.Wait((int) corradeConfiguration.ServicesTimeout);
            Client.Appearance.AppearanceSet -= HandleAppearanceSet;
            Locks.ClientInstanceAppearanceLock.ExitWriteLock();
        });

        /// <summary>
        ///     Current land group activation timer.
        /// </summary>
        private static readonly Timer ActivateCurrentLandGroupTimer =
            new Timer(() =>
            {
                Parcel parcel = null;
                if (
                    !Services.GetParcelAtPosition(Client, Client.Network.CurrentSim, Client.Self.SimPosition,
                        corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout, ref parcel))
                    return;
                var group = corradeConfiguration.Groups.AsParallel().FirstOrDefault(o => o.UUID.Equals(parcel.GroupID));
                if (group == null) return;
                Client.Groups.ActivateGroup(parcel.GroupID);
            });

        public static EventHandler ConsoleEventHandler;

        /// <summary>
        ///     Corrade's input filter function.
        /// </summary>
        private static readonly Func<string, string> wasInput = o =>
        {
            if (string.IsNullOrEmpty(o))
                return string.Empty;

            ConcurrentList<Configuration.Filter> safeFilters;
            lock (InputFiltersLock)
            {
                safeFilters = corradeConfiguration.InputFilters;
            }
            foreach (var filter in safeFilters)
                switch (filter)
                {
                    case Configuration.Filter.RFC1738:
                        o = o.URLUnescapeDataString();
                        break;

                    case Configuration.Filter.RFC3986:
                        o = o.URIUnescapeDataString();
                        break;

                    case Configuration.Filter.ENIGMA:
                        o = Cryptography.ENIGMA(o, corradeConfiguration.ENIGMAConfiguration.rotors.ToArray(),
                            corradeConfiguration.ENIGMAConfiguration.plugs.ToArray(),
                            corradeConfiguration.ENIGMAConfiguration.reflector);
                        break;

                    case Configuration.Filter.VIGENERE:
                        o = Cryptography.DecryptVIGENERE(o, corradeConfiguration.VIGENERESecret);
                        break;

                    case Configuration.Filter.ATBASH:
                        o = Cryptography.ATBASH(o);
                        break;

                    case Configuration.Filter.AES:
                        o = CorradeAES.wasAESDecrypt(o, corradeConfiguration.AESKey);
                        break;

                    case Configuration.Filter.BASE64:
                        o = Encoding.UTF8.GetString(Convert.FromBase64String(o));
                        break;
                }
            return o;
        };

        /// <summary>
        ///     Corrade's output filter function.
        /// </summary>
        public static readonly Func<string, string> wasOutput = o =>
        {
            if (string.IsNullOrEmpty(o))
                return string.Empty;

            ConcurrentList<Configuration.Filter> safeFilters;
            lock (OutputFiltersLock)
            {
                safeFilters = corradeConfiguration.OutputFilters;
            }
            foreach (var filter in safeFilters)
                switch (filter)
                {
                    case Configuration.Filter.RFC1738:
                        o = o.URLEscapeDataString();
                        break;

                    case Configuration.Filter.RFC3986:
                        o = o.URIEscapeDataString();
                        break;

                    case Configuration.Filter.ENIGMA:
                        o = Cryptography.ENIGMA(o, corradeConfiguration.ENIGMAConfiguration.rotors.ToArray(),
                            corradeConfiguration.ENIGMAConfiguration.plugs.ToArray(),
                            corradeConfiguration.ENIGMAConfiguration.reflector);
                        break;

                    case Configuration.Filter.VIGENERE:
                        o = Cryptography.EncryptVIGENERE(o, corradeConfiguration.VIGENERESecret);
                        break;

                    case Configuration.Filter.ATBASH:
                        o = Cryptography.ATBASH(o);
                        break;

                    case Configuration.Filter.AES:
                        o = CorradeAES.wasAESEncrypt(o, corradeConfiguration.AESKey);
                        break;

                    case Configuration.Filter.BASE64:
                        o = Convert.ToBase64String(Encoding.UTF8.GetBytes(o));
                        break;
                }
            return o;
        };

        /// <summary>
        ///     Loads the OpenMetaverse inventory cache.
        /// </summary>
        private static readonly Action LoadInventoryCache = () =>
        {
            // Create the cache directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.CACHE_DIRECTORY);

            int itemsLoaded;
            Locks.ClientInstanceInventoryLock.EnterWriteLock();
            itemsLoaded = Client.Inventory.Store.RestoreFromDisk(Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY,
                CORRADE_CONSTANTS.INVENTORY_CACHE_FILE));
            Locks.ClientInstanceInventoryLock.ExitWriteLock();

            Feedback(
                Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.INVENTORY_CACHE_ITEMS_LOADED),
                itemsLoaded < 0 ? "0" : itemsLoaded.ToString(Utils.EnUsCulture));
        };

        /// <summary>
        ///     Saves the OpenMetaverse inventory cache.
        /// </summary>
        private static readonly Action SaveInventoryCache = () =>
        {
            // Create the cache directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.CACHE_DIRECTORY);

            var path = Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY,
                CORRADE_CONSTANTS.INVENTORY_CACHE_FILE);
            int itemsSaved;
            Locks.ClientInstanceInventoryLock.EnterReadLock();
            itemsSaved = Client.Inventory.Store.Items.Count;
            Client.Inventory.Store.SaveToDisk(path);
            Locks.ClientInstanceInventoryLock.ExitReadLock();

            Feedback(
                Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.INVENTORY_CACHE_ITEMS_SAVED),
                itemsSaved.ToString(Utils.EnUsCulture));
        };

        /// <summary>
        ///     Loads Corrade's caches.
        /// </summary>
        private static readonly Action LoadCorradeCache = () =>
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                () =>
                {
                    try
                    {
                        // Create the cache directory if it does not exist.
                        Directory.CreateDirectory(CORRADE_CONSTANTS.CACHE_DIRECTORY);

                        Cache.AgentCache =
                            Cache.Load(
                                Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.AGENT_CACHE_FILE),
                                Cache.AgentCache);
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_CACHE),
                            ex.PrettyPrint());
                    }
                });

            CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                () =>
                {
                    try
                    {
                        // Create the cache directory if it does not exist.
                        Directory.CreateDirectory(CORRADE_CONSTANTS.CACHE_DIRECTORY);

                        Cache.GroupCache =
                            Cache.Load(
                                Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.GROUP_CACHE_FILE),
                                Cache.GroupCache);
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_CACHE),
                            ex.PrettyPrint());
                    }
                });

            CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                () =>
                {
                    try
                    {
                        // Create the cache directory if it does not exist.
                        Directory.CreateDirectory(CORRADE_CONSTANTS.CACHE_DIRECTORY);

                        Cache.RegionCache =
                            Cache.Load(
                                Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.REGION_CACHE_FILE),
                                Cache.RegionCache);
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_CACHE),
                            ex.PrettyPrint());
                    }
                });
        };

        /// <summary>
        ///     Saves Corrade's caches.
        /// </summary>
        private static readonly Action SaveCorradeCache = () =>
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                () =>
                {
                    try
                    {
                        // Create the cache directory if it does not exist.
                        Directory.CreateDirectory(CORRADE_CONSTANTS.CACHE_DIRECTORY);

                        Cache.Save(
                            Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.AGENT_CACHE_FILE),
                            Cache.AgentCache);
                    }
                    catch (Exception e)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_SAVE_CORRADE_CACHE),
                            e.Message);
                    }
                });

            CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                () =>
                {
                    try
                    {
                        // Create the cache directory if it does not exist.
                        Directory.CreateDirectory(CORRADE_CONSTANTS.CACHE_DIRECTORY);

                        Cache.Save(
                            Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.GROUP_CACHE_FILE),
                            Cache.GroupCache);
                    }
                    catch (Exception e)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_SAVE_CORRADE_CACHE),
                            e.Message);
                    }
                });

            CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                () =>
                {
                    try
                    {
                        // Create the cache directory if it does not exist.
                        Directory.CreateDirectory(CORRADE_CONSTANTS.CACHE_DIRECTORY);

                        Cache.Save(
                            Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY, CORRADE_CONSTANTS.REGION_CACHE_FILE),
                            Cache.RegionCache);
                    }
                    catch (Exception e)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_SAVE_CORRADE_CACHE),
                            e.Message);
                    }
                });
        };

        /// <summary>
        ///     Saves Corrade group members.
        /// </summary>
        public static readonly Action SaveGroupBayesClassificiations = () =>
        {
            corradeConfiguration.Groups.AsParallel().ForAll(group =>
            {
                try
                {
                    lock (GroupBayesClassifiersLock)
                    {
                        if (!GroupBayesClassifiers.ContainsKey(group.UUID) || GroupBayesClassifiers[group.UUID] == null)
                            return;
                    }

                    // Create Bayes directory if it does not exist.
                    Directory.CreateDirectory(CORRADE_CONSTANTS.BAYES_DIRECTORY);

                    var path = Path.Combine(CORRADE_CONSTANTS.BAYES_DIRECTORY,
                        $"{group.UUID}.{CORRADE_CONSTANTS.BAYES_CLASSIFICATION_EXTENSION}");
                    using (
                        var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 16384,
                            true))
                    {
                        using (var streamWriter = new StreamWriter(fileStream, Encoding.UTF8))
                        {
                            lock (GroupBayesClassifiersLock)
                            {
                                var data = GroupBayesClassifiers[group.UUID].ExportJsonData();
                                if (!string.IsNullOrEmpty(data))
                                    streamWriter.WriteLine(data);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_SAVE_GROUP_BAYES_DATA),
                        e.Message);
                }
            });
        };

        /// <summary>
        ///     Loads Corrade group members.
        /// </summary>
        private static readonly Action LoadGroupBayesClassificiations = () =>
        {
            corradeConfiguration.Groups.AsParallel().ForAll(group =>
            {
                lock (GroupBayesClassifiersLock)
                {
                    if (!GroupBayesClassifiers.ContainsKey(group.UUID))
                        GroupBayesClassifiers.Add(group.UUID, new BayesSimpleTextClassifier());
                }

                // Create Bayes directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.BAYES_DIRECTORY);

                var bayesClassifierFile = Path.Combine(CORRADE_CONSTANTS.BAYES_DIRECTORY,
                    $"{group.UUID}.{CORRADE_CONSTANTS.BAYES_CLASSIFICATION_EXTENSION}");

                if (!File.Exists(bayesClassifierFile))
                    return;

                try
                {
                    using (
                        var fileStream = new FileStream(bayesClassifierFile, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 16384, true))
                    {
                        using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                        {
                            lock (GroupBayesClassifiersLock)
                            {
                                GroupBayesClassifiers[group.UUID].ImportJsonData(streamReader.ReadToEnd());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_GROUP_BAYES_DATA),
                        ex.PrettyPrint());
                }
            });
        };

        /// <summary>
        ///     Saves Corrade group members.
        /// </summary>
        private static readonly Action SaveGroupMembersState = () =>
        {
            try
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                lock (GroupMembersStateFileLock)
                {
                    var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                        CORRADE_CONSTANTS.GROUP_MEMBERS_STATE_FILE);
                    using (
                        var fileStream = new FileStream(path, FileMode.Create,
                            FileAccess.Write, FileShare.None, 16384, true))
                    {
                        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                        {
                            lock (GroupMembersLock)
                            {
                                XmlSerializerCache.Serialize(writer, GroupMembers);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.UNABLE_TO_SAVE_GROUP_MEMBERS_STATE),
                    e.Message);
            }
        };

        /// <summary>
        ///     Loads Corrade group members.
        /// </summary>
        private static readonly Action LoadGroupMembersState = () =>
        {
            // Create the state directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

            var groupMembersStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                CORRADE_CONSTANTS.GROUP_MEMBERS_STATE_FILE);
            if (File.Exists(groupMembersStateFile))
                try
                {
                    lock (GroupMembersStateFileLock)
                    {
                        using (
                            var fileStream = new FileStream(groupMembersStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                        {
                            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                            {
                                var groups =
                                    new HashSet<UUID>(corradeConfiguration.Groups.Select(o => new UUID(o.UUID)));
                                XmlSerializerCache.Deserialize<SerializableDictionary
                                        <UUID, ObservableHashSet<UUID>>>(streamReader)
                                    .AsParallel()
                                    .Where(
                                        o => groups.Contains(o.Key))
                                    .ForAll(o =>
                                    {
                                        lock (GroupMembersLock)
                                        {
                                            switch (!GroupMembers.ContainsKey(o.Key))
                                            {
                                                case true:
                                                    GroupMembers.Add(o.Key, new ObservableHashSet<UUID>());
                                                    GroupMembers[o.Key].CollectionChanged += HandleGroupMemberJoinPart;
                                                    GroupMembers[o.Key].UnionWith(o.Value);
                                                    break;

                                                default:
                                                    GroupMembers[o.Key].UnionWith(o.Value);
                                                    break;
                                            }
                                        }
                                    });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_GROUP_MEMBERS_STATE),
                        ex.PrettyPrint());
                }
        };

        /// <summary>
        ///     Saves Corrade group soft bans.
        /// </summary>
        public static readonly Action SaveGroupSoftBansState = () =>
        {
            try
            {
                GroupSoftBansWatcher.EnableRaisingEvents = false;

                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                    CORRADE_CONSTANTS.GROUP_SOFT_BAN_STATE_FILE);
                lock (GroupSoftBansStateFileLock)
                {
                    using (
                        var fileStream = new FileStream(path, FileMode.Create,
                            FileAccess.Write, FileShare.None, 16384, true))
                    {
                        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                        {
                            lock (GroupSoftBansLock)
                            {
                                XmlSerializerCache.Serialize(writer, GroupSoftBans);
                            }
                        }
                    }
                }
                GroupSoftBansWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.UNABLE_TO_SAVE_GROUP_SOFT_BAN_STATE),
                    e.Message);
            }
        };

        /// <summary>
        ///     Loads Corrade group soft bans.
        /// </summary>
        private static readonly Action LoadGroupSoftBansState = () =>
        {
            // Create the state directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

            var groupSoftBansStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                CORRADE_CONSTANTS.GROUP_SOFT_BAN_STATE_FILE);
            if (File.Exists(groupSoftBansStateFile))
                try
                {
                    lock (GroupSoftBansStateFileLock)
                    {
                        using (
                            var fileStream = new FileStream(groupSoftBansStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                        {
                            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                            {
                                var groups =
                                    new HashSet<UUID>(corradeConfiguration.Groups.Select(o => new UUID(o.UUID)));
                                XmlSerializerCache
                                    .Deserialize<SerializableDictionary<UUID, ObservableHashSet<SoftBan>>>(streamReader)
                                    .AsParallel()
                                    .Where(
                                        o => groups.Contains(o.Key))
                                    .ForAll(o =>
                                    {
                                        lock (GroupSoftBansLock)
                                        {
                                            switch (!GroupSoftBans.ContainsKey(o.Key))
                                            {
                                                case true:
                                                    GroupSoftBans.Add(o.Key,
                                                        new ObservableHashSet<SoftBan>());
                                                    GroupSoftBans[o.Key].CollectionChanged +=
                                                        HandleGroupSoftBansChanged;
                                                    GroupSoftBans[o.Key].UnionWith(o.Value);
                                                    break;

                                                default:
                                                    GroupSoftBans[o.Key].UnionWith(o.Value);
                                                    break;
                                            }
                                        }
                                    });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_GROUP_SOFT_BAN_STATE),
                        ex.PrettyPrint());
                }
        };

        /// <summary>
        ///     Saves Corrade group schedules.
        /// </summary>
        private static readonly Action SaveGroupSchedulesState = () =>
        {
            try
            {
                SchedulesWatcher.EnableRaisingEvents = false;

                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                    CORRADE_CONSTANTS.GROUP_SCHEDULES_STATE_FILE);

                lock (GroupSchedulesStateFileLock)
                {
                    using (
                        var fileStream = new FileStream(path, FileMode.Create,
                            FileAccess.Write, FileShare.None, 16384, true))
                    {
                        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                        {
                            lock (GroupSchedulesLock)
                            {
                                XmlSerializerCache.Serialize(writer, GroupSchedules);
                            }
                        }
                    }
                }

                SchedulesWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.UNABLE_TO_SAVE_CORRADE_GROUP_SCHEDULES_STATE),
                    e.Message);
            }
        };

        /// <summary>
        ///     Loads Corrade group schedules.
        /// </summary>
        private static readonly Action LoadGroupSchedulesState = () =>
        {
            SchedulesWatcher.EnableRaisingEvents = false;

            // Create the state directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

            var groupSchedulesStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                CORRADE_CONSTANTS.GROUP_SCHEDULES_STATE_FILE);
            if (File.Exists(groupSchedulesStateFile))
                try
                {
                    lock (GroupSchedulesStateFileLock)
                    {
                        using (
                            var fileStream = new FileStream(groupSchedulesStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                        {
                            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                            {
                                var groups =
                                    new HashSet<UUID>(
                                        corradeConfiguration.Groups
                                            .AsParallel()
                                            .Where(
                                                o =>
                                                    !o.Schedules.Equals(0) &&
                                                    o.PermissionMask.IsMaskFlagSet(Configuration.Permissions.Schedule))
                                            .Select(o => new UUID(o.UUID)));
                                XmlSerializerCache.Deserialize<HashSet<GroupSchedule>>(streamReader)
                                    .AsParallel()
                                    .Where(o => groups.Contains(o.Group.UUID)).ForAll(o =>
                                    {
                                        lock (GroupSchedulesLock)
                                        {
                                            if (!GroupSchedules.Contains(o))
                                                GroupSchedules.Add(o);
                                        }
                                    });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_GROUP_SCHEDULES_STATE),
                        ex.PrettyPrint());
                }
            SchedulesWatcher.EnableRaisingEvents = true;
        };

        /// <summary>
        ///     Saves Corrade notifications.
        /// </summary>
        private static readonly Action SaveNotificationState = () =>
        {
            try
            {
                NotificationsWatcher.EnableRaisingEvents = false;

                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                lock (GroupNotificationsStateFileLock)
                {
                    using (
                        var fileStream = new FileStream(Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                                CORRADE_CONSTANTS.NOTIFICATIONS_STATE_FILE), FileMode.Create,
                            FileAccess.Write, FileShare.None, 4096, true))
                    {
                        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                        {
                            lock (GroupNotificationsLock)
                            {
                                XmlSerializerCache.Serialize(writer, GroupNotifications);
                            }
                        }
                    }
                }
                NotificationsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.UNABLE_TO_SAVE_CORRADE_NOTIFICATIONS_STATE),
                    e.Message);
            }
        };

        /// <summary>
        ///     Loads Corrade notifications.
        /// </summary>
        private static readonly Action LoadNotificationState = () =>
        {
            NotificationsWatcher.EnableRaisingEvents = false;

            // Create the state directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

            var groupNotificationsStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                CORRADE_CONSTANTS.NOTIFICATIONS_STATE_FILE);

            if (File.Exists(groupNotificationsStateFile))
            {
                var groups = new HashSet<UUID>(corradeConfiguration.Groups.Select(o => new UUID(o.UUID)));
                try
                {
                    lock (GroupNotificationsStateFileLock)
                    {
                        using (
                            var fileStream = new FileStream(groupNotificationsStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                        {
                            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                            {
                                XmlSerializerCache.Deserialize<HashSet<Notifications>>(streamReader)
                                    .AsParallel()
                                    .Where(
                                        o => groups.Contains(o.GroupUUID))
                                    .ForAll(o =>
                                    {
                                        lock (GroupNotificationsLock)
                                        {
                                            if (!GroupNotifications.Contains(o))
                                                GroupNotifications.Add(o);
                                        }
                                    });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_NOTIFICATIONS_STATE),
                        ex.PrettyPrint());
                }

                // Build the group notification cache.
                var LockObject = new object();
                new List<Configuration.Notifications>(Reflection.GetEnumValues<Configuration.Notifications>())
                    .AsParallel().ForAll(o =>
                    {
                        lock (GroupNotificationsLock)
                        {
                            GroupNotifications.AsParallel()
                                .Where(p => p.NotificationMask.IsMaskFlagSet(o)).ForAll(p =>
                                {
                                    lock (LockObject)
                                    {
                                        if (GroupNotificationsCache.ContainsKey(o))
                                        {
                                            GroupNotificationsCache[o].Add(p);
                                            return;
                                        }
                                        GroupNotificationsCache.Add(o, new HashSet<Notifications> {p});
                                    }
                                });
                        }
                    });
            }
            NotificationsWatcher.EnableRaisingEvents = true;
        };

        /// <summary>
        ///     Saves Corrade movement state.
        /// </summary>
        private static readonly Action SaveMovementState = () =>
        {
            try
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                    CORRADE_CONSTANTS.MOVEMENT_STATE_FILE);

                lock (MovementStateFileLock)
                {
                    using (
                        var fileStream = new FileStream(path, FileMode.Create,
                            FileAccess.Write, FileShare.None, 16384, true))
                    {
                        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                        {
                            Locks.ClientInstanceSelfLock.EnterReadLock();
                            XmlSerializerCache.Serialize(writer, new AgentMovement
                            {
                                BodyRotation = Client.Self.Movement.BodyRotation,
                                HeadRotation = Client.Self.Movement.HeadRotation,
                                AlwaysRun = Client.Self.Movement.AlwaysRun,
                                AutoResetControls = Client.Self.Movement.AutoResetControls,
                                Away = Client.Self.Movement.Away,
                                Flags = Client.Self.Movement.Flags,
                                Fly = Client.Self.Movement.Fly,
                                Mouselook = Client.Self.Movement.Mouselook,
                                SitOnGround = Client.Self.Movement.SitOnGround,
                                StandUp = Client.Self.Movement.StandUp,
                                State = Client.Self.Movement.State
                            });
                            Locks.ClientInstanceSelfLock.ExitReadLock();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.UNABLE_TO_SAVE_CORRADE_MOVEMENT_STATE),
                    e.Message);
            }
        };

        /// <summary>
        ///     Loads Corrade movement state.
        /// </summary>
        private static readonly Action LoadMovementState = () =>
        {
            // Create the state directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

            var movementStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                CORRADE_CONSTANTS.MOVEMENT_STATE_FILE);
            if (File.Exists(movementStateFile))
                try
                {
                    lock (MovementStateFileLock)
                    {
                        using (
                            var fileStream = new FileStream(movementStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                        {
                            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                            {
                                var movement = XmlSerializerCache.Deserialize<AgentMovement>(streamReader);
                                Locks.ClientInstanceSelfLock.EnterWriteLock();

                                // Only restore rotations if they are sane.
                                if (!(movement.BodyRotation.W == 0 &&
                                      movement.BodyRotation.X == 0 &&
                                      movement.BodyRotation.Y == 0 &&
                                      movement.BodyRotation.Z == 0) &&
                                    !float.IsNaN(movement.BodyRotation.W) &&
                                    !float.IsNaN(movement.BodyRotation.X) &&
                                    !float.IsNaN(movement.BodyRotation.Y) &&
                                    !float.IsNaN(movement.BodyRotation.Z) &&
                                    !float.IsInfinity(movement.BodyRotation.W) &&
                                    !float.IsInfinity(movement.BodyRotation.X) &&
                                    !float.IsInfinity(movement.BodyRotation.Y) &&
                                    !float.IsInfinity(movement.BodyRotation.Z))
                                    Client.Self.Movement.BodyRotation = movement.BodyRotation;
                                if (!(movement.HeadRotation.W == 0 &&
                                      movement.HeadRotation.X == 0 &&
                                      movement.HeadRotation.Y == 0 &&
                                      movement.HeadRotation.Z == 0) &&
                                    !float.IsNaN(movement.HeadRotation.W) &&
                                    !float.IsNaN(movement.HeadRotation.X) &&
                                    !float.IsNaN(movement.HeadRotation.Y) &&
                                    !float.IsNaN(movement.HeadRotation.Z) &&
                                    !float.IsInfinity(movement.HeadRotation.W) &&
                                    !float.IsInfinity(movement.HeadRotation.X) &&
                                    !float.IsInfinity(movement.HeadRotation.Y) &&
                                    !float.IsInfinity(movement.HeadRotation.Z))
                                    Client.Self.Movement.HeadRotation = movement.HeadRotation;

                                Client.Self.Movement.AlwaysRun = movement.AlwaysRun;
                                Client.Self.Movement.AutoResetControls = movement.AutoResetControls;
                                Client.Self.Movement.Away = movement.Away;
                                Client.Self.Movement.Flags = movement.Flags;
                                Client.Self.Movement.State = movement.State;
                                Client.Self.Movement.Mouselook = movement.Mouselook;
                                Client.Self.Movement.StandUp = movement.StandUp;
                                Client.Self.Movement.Fly = movement.Fly;

                                // Sitting down while airborne makes Corrade vanish (libomv issue).
                                Client.Self.Movement.SitOnGround = movement.SitOnGround;

                                Client.Self.Movement.SendUpdate(true);
                                Locks.ClientInstanceSelfLock.ExitWriteLock();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_MOVEMENT_STATE),
                        ex.PrettyPrint());
                }
        };

        /// <summary>
        ///     Saves Corrade movement state.
        /// </summary>
        private static readonly Action SaveConferenceState = () =>
        {
            try
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                    CORRADE_CONSTANTS.CONFERENCE_STATE_FILE);

                lock (ConferencesStateFileLock)
                {
                    using (
                        var fileStream = new FileStream(path, FileMode.Create,
                            FileAccess.Write, FileShare.None, 16384, true))
                    {
                        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                        {
                            lock (ConferencesLock)
                            {
                                XmlSerializerCache.Serialize(writer, Conferences);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.UNABLE_TO_SAVE_CONFERENCE_STATE),
                    e.Message);
            }
        };

        /// <summary>
        ///     Loads Corrade movement state.
        /// </summary>
        private static readonly Action LoadConferenceState = () =>
        {
            // Create the state directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

            var conferenceStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                CORRADE_CONSTANTS.CONFERENCE_STATE_FILE);

            if (File.Exists(conferenceStateFile))
                try
                {
                    lock (ConferencesStateFileLock)
                    {
                        using (
                            var fileStream = new FileStream(conferenceStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                        {
                            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                            {
                                XmlSerializerCache.Deserialize<HashSet<Conference>>(streamReader)
                                    .AsParallel()
                                    .ForAll(o =>
                                    {
                                        try
                                        {
                                            // Attempt to rejoin the conference.
                                            Locks.ClientInstanceSelfLock.EnterWriteLock();
                                            if (!Client.Self.GroupChatSessions.ContainsKey(o.Session))
                                                Client.Self.ChatterBoxAcceptInvite(o.Session);
                                            Locks.ClientInstanceSelfLock.ExitWriteLock();
                                            // Add the conference to the list of conferences.
                                            lock (ConferencesLock)
                                            {
                                                if (!Conferences.AsParallel()
                                                    .Any(
                                                        p =>
                                                            p.Name.Equals(o.Name, StringComparison.Ordinal) &&
                                                            p.Session.Equals(o.Session)))
                                                    Conferences.Add(new Conference
                                                    {
                                                        Name = o.Name,
                                                        Session = o.Session,
                                                        Restored = true
                                                    });
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Feedback(
                                                Reflection.GetDescriptionFromEnumValue(
                                                    Enumerations.ConsoleMessage.UNABLE_TO_RESTORE_CONFERENCE),
                                                o.Name,
                                                ex.PrettyPrint());
                                        }
                                    });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CONFERENCE_STATE),
                        ex.PrettyPrint());
                }
        };

        /// <summary>
        ///     Loads Corrade group cookies.
        /// </summary>
        private static readonly Action LoadGroupCookiesState = () =>
        {
            // Create the state directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

            var groupCookiesStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                CORRADE_CONSTANTS.GROUP_COOKIES_STATE_FILE);

            if (File.Exists(groupCookiesStateFile))
                try
                {
                    lock (GroupCookiesStateFileLock)
                    {
                        using (
                            var fileStream = new FileStream(groupCookiesStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                        {
                            var groups = new HashSet<UUID>(corradeConfiguration.Groups.Select(o => new UUID(o.UUID)));
                            var serializer = new BinaryFormatter();
                            ((Dictionary<UUID, CookieContainer>)
                                    serializer.Deserialize(fileStream)).AsParallel()
                                .Where(o => groups.Contains(o.Key))
                                .ForAll(o =>
                                {
                                    lock (GroupCookieContainersLock)
                                    {
                                        if (!GroupCookieContainers.Contains(o))
                                        {
                                            GroupCookieContainers.Add(o.Key, o.Value);
                                            return;
                                        }
                                        GroupCookieContainers[o.Key] = o.Value;
                                    }
                                });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_GROUP_COOKIES_STATE),
                        ex.PrettyPrint());
                }
        };

        /// <summary>
        ///     Saves Corrade group cookies.
        /// </summary>
        private static readonly Action SaveGroupCookiesState = () =>
        {
            try
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                    CORRADE_CONSTANTS.GROUP_COOKIES_STATE_FILE);

                lock (GroupCookiesStateFileLock)
                {
                    using (
                        var fileStream = new FileStream(path, FileMode.Create,
                            FileAccess.Write, FileShare.None, 16384, true))
                    {
                        var serializer = new BinaryFormatter();
                        lock (GroupCookieContainersLock)
                        {
                            serializer.Serialize(fileStream, GroupCookieContainers);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.UNABLE_TO_SAVE_GROUP_COOKIES_STATE),
                    e.Message);
            }
        };

        /// <summary>
        ///     Saves Corrade feeds.
        /// </summary>
        private static readonly Action SaveGroupFeedState = () =>
        {
            try
            {
                GroupFeedWatcher.EnableRaisingEvents = false;

                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                    CORRADE_CONSTANTS.FEEDS_STATE_FILE);

                lock (GroupFeedsStateFileLock)
                {
                    using (
                        var fileStream = new FileStream(path, FileMode.Create,
                            FileAccess.Write, FileShare.None, 16384, true))
                    {
                        using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                        {
                            lock (GroupFeedsLock)
                            {
                                XmlSerializerCache.Serialize(writer, GroupFeeds);
                            }
                        }
                    }
                }

                GroupFeedWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.UNABLE_TO_SAVE_CORRADE_FEEDS_STATE),
                    e.Message);
            }
        };

        /// <summary>
        ///     Loads Corrade notifications.
        /// </summary>
        private static readonly Action LoadGroupFeedState = () =>
        {
            // Create the state directory if it does not exist.
            Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

            var feedStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                CORRADE_CONSTANTS.FEEDS_STATE_FILE);

            if (File.Exists(feedStateFile))
                try
                {
                    lock (GroupFeedsStateFileLock)
                    {
                        using (
                            var fileStream = new FileStream(feedStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                        {
                            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                            {
                                var groups =
                                    new HashSet<UUID>(corradeConfiguration.Groups.Select(o => new UUID(o.UUID)));
                                XmlSerializerCache
                                    .Deserialize<SerializableDictionary<string, SerializableDictionary<UUID, string>>>(
                                        streamReader)
                                    .AsParallel()
                                    .Where(o => o.Value.Any(p => groups.Contains(p.Key)))
                                    .ForAll(o =>
                                    {
                                        lock (GroupFeedsLock)
                                        {
                                            if (!GroupFeeds.ContainsKey(o.Key))
                                            {
                                                GroupFeeds.Add(o.Key, o.Value);
                                                return;
                                            }
                                            // Clone.
                                            XmlSerializerCache
                                                .Deserialize<SerializableDictionary<UUID, string>>(
                                                    XmlSerializerCache.Serialize(GroupFeeds[o.Key])).AsParallel()
                                                .ForAll(p =>
                                                {
                                                    if (!GroupFeeds[o.Key].ContainsKey(p.Key))
                                                    {
                                                        GroupFeeds[o.Key].Add(p.Key, p.Value);
                                                        return;
                                                    }
                                                    GroupFeeds[o.Key][p.Key] = p.Value;
                                                });
                                        }
                                    });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_FEEDS_STATE),
                        ex.PrettyPrint());
                }
        };

        /// <summary>
        ///     Loads the chatbot configuration and SIML files.
        /// </summary>
        private static readonly Action LoadChatBotFiles = () =>
        {
            if (!string.IsNullOrEmpty(SIMLBotConfigurationWatcher.Path))
                SIMLBotConfigurationWatcher.EnableRaisingEvents = false;
            Feedback(
                Reflection.GetDescriptionFromEnumValue(
                    Enumerations.ConsoleMessage.READING_SIML_BOT_CONFIGURATION));
            try
            {
                var SIMLPackage = Path.Combine(
                    Directory.GetCurrentDirectory(), SIML_BOT_CONSTANTS.ROOT_DIRECTORY,
                    SIML_BOT_CONSTANTS.PACKAGE_FILE);

                switch (File.Exists(SIMLPackage))
                {
                    case true:
                        SynBot.PackageManager.LoadFromFile(SIMLPackage);
                        break;

                    default:
                        var elementList = new List<XDocument>();
                        foreach (var simlDocument in Directory.GetFiles(Path.Combine(
                                Directory.GetCurrentDirectory(), SIML_BOT_CONSTANTS.ROOT_DIRECTORY,
                                SIML_BOT_CONSTANTS.SIML_DIRECTORY,
                                SIML_BOT_CONSTANTS.SIML_SETTINGS_DIRECTORY), @"*.siml")
                            .Select(XDocument.Load))
                        {
                            elementList.Add(simlDocument);
                            SynBot.AddSiml(simlDocument);
                        }
                        foreach (var simlDocument in Directory.GetFiles(Path.Combine(
                                Directory.GetCurrentDirectory(), SIML_BOT_CONSTANTS.ROOT_DIRECTORY,
                                SIML_BOT_CONSTANTS.SIML_DIRECTORY), @"*.siml")
                            .Select(XDocument.Load))
                        {
                            elementList.Add(simlDocument);
                            SynBot.AddSiml(simlDocument);
                        }
                        File.WriteAllText(Path.Combine(
                            Directory.GetCurrentDirectory(), SIML_BOT_CONSTANTS.ROOT_DIRECTORY,
                            SIML_BOT_CONSTANTS.PACKAGE_FILE), SynBot.PackageManager.ConvertToPackage(elementList));
                        break;
                }

                // Load learned and memorized.
                var SIMLLearned = Path.Combine(
                    Directory.GetCurrentDirectory(), SIML_BOT_CONSTANTS.ROOT_DIRECTORY,
                    SIML_BOT_CONSTANTS.EVOLVE_DIRECTORY,
                    SIML_BOT_CONSTANTS.LEARNED_FILE);
                if (File.Exists(SIMLLearned))
                    SynBot.AddSiml(XDocument.Load(SIMLLearned));
                var SIMLMemorized = Path.Combine(
                    Directory.GetCurrentDirectory(), SIML_BOT_CONSTANTS.ROOT_DIRECTORY,
                    SIML_BOT_CONSTANTS.EVOLVE_DIRECTORY,
                    SIML_BOT_CONSTANTS.MEMORIZED_FILE);
                if (File.Exists(SIMLMemorized))
                    SynBot.AddSiml(XDocument.Load(SIMLMemorized), SynBotUser);
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_LOADING_SIML_BOT_FILES),
                    ex.PrettyPrint());
                if (!string.IsNullOrEmpty(SIMLBotConfigurationWatcher.Path))
                    SIMLBotConfigurationWatcher.EnableRaisingEvents = true;
                return;
            }
            Feedback(
                Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.READ_SIML_BOT_CONFIGURATION));
            if (!string.IsNullOrEmpty(SIMLBotConfigurationWatcher.Path))
                SIMLBotConfigurationWatcher.EnableRaisingEvents = true;
        };

        private static volatile bool runTCPNotificationsServer;
        private static volatile bool runCallbackThread = true;
        private static volatile bool runNotificationThread = true;

        public Corrade()
        {
            if (Environment.UserInteractive)
                return;
            switch (Utils.GetRunningPlatform())
            {
                case Utils.Platform.Windows:
                    try
                    {
                        InstalledServiceName = (string)
                            new ManagementObjectSearcher("SELECT * FROM Win32_Service where ProcessId = " +
                                                         Process.GetCurrentProcess().Id).Get()
                                .Cast<ManagementBaseObject>()
                                .First()["Name"];
                    }
                    catch (Exception)
                    {
                        InstalledServiceName = CORRADE_CONSTANTS.DEFAULT_SERVICE_NAME;
                    }
                    break;

                default:
                    InstalledServiceName = CORRADE_CONSTANTS.DEFAULT_SERVICE_NAME;
                    break;
            }
        }

        private static bool? CorradeScriptedAgentStatus
        {
            get
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                // Get the stored scripted agent status
                var lastScriptedAgentStatusStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                    CORRADE_CONSTANTS.SCRIPTED_AGENT_STATUS_STATE_FILE);
                if (File.Exists(lastScriptedAgentStatusStateFile))
                    lock (CorradeScriptedAgentStatusFileLock)
                    {
                        try
                        {
                            using (var fileStream = new FileStream(lastScriptedAgentStatusStateFile, FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read, 16384, true))
                            {
                                using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                                {
                                    _CorradeScriptedAgentStatus = XmlSerializerCache.Deserialize<bool?>(streamReader);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Feedback(
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.UNABLE_TO_RETRIEVE_LAST_SCRIPTED_AGENT_STATUS_STATE),
                                ex.PrettyPrint());
                        }
                    }

                return _CorradeScriptedAgentStatus;
            }
            set
            {
                try
                {
                    // Create the state directory if it does not exist.
                    Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                    var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                        CORRADE_CONSTANTS.SCRIPTED_AGENT_STATUS_STATE_FILE);
                    lock (CorradeScriptedAgentStatusFileLock)
                    {
                        using (
                            var fileStream = new FileStream(path, FileMode.Create,
                                FileAccess.Write, FileShare.None, 16384, true))
                        {
                            using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                            {
                                XmlSerializerCache.Serialize(writer, value);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_STORE_LAST_SCRIPTED_AGENT_STATUS_STATE),
                        ex.PrettyPrint());
                }

                _CorradeScriptedAgentStatus = value;
            }
        }

        private static LastExecStatus CorradeLastExecStatus
        {
            get
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                // Get the last execution status
                var lastExecStateFile = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                    CORRADE_CONSTANTS.LAST_EXEC_STATE_FILE);

                if (File.Exists(lastExecStateFile))
                    lock (CorradeLastExecStatusFileLock)
                    {
                        try
                        {
                            using (var fileStream = new FileStream(lastExecStateFile, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 16384, true))
                            {
                                using (var streamReader = new StreamReader(fileStream, Encoding.UTF8))
                                {
                                    _CorradeLastExecStatus =
                                        XmlSerializerCache.Deserialize<LastExecStatus>(streamReader);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Feedback(
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.UNABLE_TO_RETRIEVE_LAST_EXECUTION_STATE),
                                ex.PrettyPrint());
                        }
                    }

                return _CorradeLastExecStatus;
            }
            set
            {
                try
                {
                    // Create the state directory if it does not exist.
                    Directory.CreateDirectory(CORRADE_CONSTANTS.STATE_DIRECTORY);

                    var path = Path.Combine(CORRADE_CONSTANTS.STATE_DIRECTORY,
                        CORRADE_CONSTANTS.LAST_EXEC_STATE_FILE);
                    lock (CorradeLastExecStatusFileLock)
                    {
                        using (
                            var fileStream = new FileStream(path, FileMode.Create,
                                FileAccess.Write, FileShare.None, 16384, true))
                        {
                            using (var writer = new StreamWriter(fileStream, Encoding.UTF8))
                            {
                                XmlSerializerCache.Serialize(writer, value);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNABLE_TO_STORE_LAST_EXECUTION_STATE),
                        ex.PrettyPrint());
                }

                _CorradeLastExecStatus = value;
            }
        }

        /// <summary>
        ///     Main thread that processes TCP connections.
        /// </summary>
        private static void ProcessTCPNotifications()
        {
            // Attempt to create a new TCP listener by binding to the address.
            try
            {
                TCPListener =
                    new TcpListener(
                        new IPEndPoint(IPAddress.Parse(corradeConfiguration.TCPNotificationsServerAddress),
                            (int) corradeConfiguration.TCPNotificationsServerPort));
                TCPListener.Start();
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.TCP_NOTIFICATIONS_SERVER_ERROR),
                    ex.PrettyPrint());
                return;
            }

            do
            {
                TCPNotificationsThreadState.Wait();

                var TCPClient = TCPListener.AcceptTcpClient();

                new Thread(() =>
                {
                    IPEndPoint remoteEndPoint = null;
                    var commandGroup = new Configuration.Group();
                    try
                    {
                        remoteEndPoint = TCPClient.Client.RemoteEndPoint as IPEndPoint;
                        var certificate =
                            new X509Certificate(corradeConfiguration.TCPNotificationsCertificatePath,
                                corradeConfiguration.TCPNotificationsCertificatePassword);
                        using (var networkStream = new SslStream(TCPClient.GetStream()))
                        {
                            SslProtocols protocol;
                            if (!Enum.TryParse(corradeConfiguration.TCPNotificationsSSLProtocol, out protocol))
                                protocol = SslProtocols.Tls12;

                            // Do not require a client certificate.
                            networkStream.AuthenticateAsServer(certificate, false, protocol, true);

                            using (
                                var streamReader = new StreamReader(networkStream,
                                    Encoding.UTF8))
                            {
                                var receiveLine = streamReader.ReadLine();

                                using (
                                    var streamWriter = new StreamWriter(networkStream,
                                        Encoding.UTF8))
                                {
                                    commandGroup = GetCorradeGroupFromMessage(receiveLine, corradeConfiguration);
                                    switch (
                                        commandGroup != null &&
                                        !commandGroup.Equals(default(Configuration.Group)) &&
                                        Authenticate(commandGroup.UUID,
                                            wasInput(
                                                KeyValue.Get(
                                                    wasOutput(
                                                        Reflection.GetNameFromEnumValue(
                                                            ScriptKeys.PASSWORD)),
                                                    receiveLine))))
                                    {
                                        case false:
                                            streamWriter.WriteLine(
                                                KeyValue.Encode(new Dictionary<string, string>
                                                {
                                                    {
                                                        Reflection.GetNameFromEnumValue(
                                                            ScriptKeys.SUCCESS),
                                                        false.ToString()
                                                    }
                                                }));
                                            streamWriter.Flush();
                                            TCPClient.Close();
                                            return;
                                    }

                                    var notificationTypes =
                                        wasInput(
                                            KeyValue.Get(
                                                wasOutput(
                                                    Reflection.GetNameFromEnumValue(ScriptKeys.TYPE)),
                                                receiveLine));
                                    Notifications notification;
                                    lock (GroupNotificationsLock)
                                    {
                                        notification =
                                            GroupNotifications.AsParallel().FirstOrDefault(
                                                o =>
                                                    o.GroupUUID.Equals(commandGroup.UUID));
                                    }
                                    // Build any requested data for raw notifications.
                                    var fields = wasInput(
                                        KeyValue.Get(
                                            wasOutput(
                                                Reflection.GetNameFromEnumValue(ScriptKeys.DATA)),
                                            receiveLine));
                                    var data = new HashSet<string>();
                                    var LockObject = new object();
                                    if (!string.IsNullOrEmpty(fields))
                                        CSV.ToEnumerable(fields)
                                            .AsParallel()
                                            .Where(o => !string.IsNullOrEmpty(o)).ForAll(o =>
                                            {
                                                lock (LockObject)
                                                {
                                                    data.Add(o);
                                                }
                                            });
                                    switch (notification != null)
                                    {
                                        case false:
                                            notification = new Notifications
                                            {
                                                GroupName = commandGroup.Name,
                                                GroupUUID = commandGroup.UUID,
                                                HTTPNotifications =
                                                    new SerializableDictionary<Configuration.Notifications,
                                                        SerializableDictionary<string, HashSet<string>>>(),
                                                NotificationTCPDestination =
                                                    new Dictionary
                                                        <Configuration.Notifications, HashSet<IPEndPoint>>(),
                                                Data = data
                                            };
                                            break;

                                        case true:
                                            if (notification.NotificationTCPDestination == null)
                                                notification.NotificationTCPDestination =
                                                    new Dictionary
                                                        <Configuration.Notifications, HashSet<IPEndPoint>>();
                                            if (notification.HTTPNotifications == null)
                                                notification.HTTPNotifications =
                                                    new SerializableDictionary<Configuration.Notifications,
                                                        SerializableDictionary<string, HashSet<string>>>();
                                            break;
                                    }

                                    var succeeded = true;
                                    Parallel.ForEach(CSV.ToEnumerable(
                                                notificationTypes)
                                            .AsParallel()
                                            .Where(o => !string.IsNullOrEmpty(o)),
                                        (o, state) =>
                                        {
                                            var notificationValue =
                                                (ulong)
                                                Reflection
                                                    .GetEnumValueFromName
                                                    <Configuration.Notifications>(o);
                                            if (
                                                !GroupHasNotification(commandGroup.UUID,
                                                    notificationValue))
                                            {
                                                // one of the notification was not allowed, so abort
                                                succeeded = false;
                                                state.Break();
                                            }
                                            switch (
                                                !notification.NotificationTCPDestination.ContainsKey(
                                                    (Configuration.Notifications) notificationValue))
                                            {
                                                case true:
                                                    lock (LockObject)
                                                    {
                                                        notification.NotificationTCPDestination.Add(
                                                            (Configuration.Notifications) notificationValue,
                                                            new HashSet<IPEndPoint> {remoteEndPoint});
                                                    }
                                                    break;

                                                default:
                                                    lock (LockObject)
                                                    {
                                                        notification.NotificationTCPDestination[
                                                                (Configuration.Notifications) notificationValue]
                                                            .Add(
                                                                remoteEndPoint);
                                                    }
                                                    break;
                                            }
                                        });

                                    switch (succeeded)
                                    {
                                        case true:
                                            lock (GroupNotificationsLock)
                                            {
                                                // Replace notification.
                                                GroupNotifications.RemoveWhere(
                                                    o =>
                                                        o.GroupUUID.Equals(commandGroup.UUID));
                                                GroupNotifications.Add(notification);
                                                // Build the group notification cache.
                                                GroupNotificationsCache.Clear();
                                                new List<Configuration.Notifications>(
                                                        Reflection.GetEnumValues<Configuration.Notifications>())
                                                    .AsParallel().ForAll(o =>
                                                    {
                                                        GroupNotifications.AsParallel()
                                                            .Where(p => p.NotificationMask.IsMaskFlagSet(o)).ForAll(p =>
                                                            {
                                                                lock (LockObject)
                                                                {
                                                                    if (GroupNotificationsCache.ContainsKey(o))
                                                                    {
                                                                        GroupNotificationsCache[o].Add(p);
                                                                        return;
                                                                    }
                                                                    GroupNotificationsCache.Add(o,
                                                                        new HashSet<Notifications> {p});
                                                                }
                                                            });
                                                    });
                                            }
                                            // Save the notifications state.
                                            SaveNotificationState.Invoke();
                                            streamWriter.WriteLine(
                                                KeyValue.Encode(new Dictionary<string, string>
                                                {
                                                    {
                                                        Reflection.GetNameFromEnumValue(
                                                            ScriptKeys.SUCCESS),
                                                        true.ToString()
                                                    }
                                                }));
                                            streamWriter.Flush();
                                            break;

                                        default:
                                            streamWriter.WriteLine(
                                                KeyValue.Encode(new Dictionary<string, string>
                                                {
                                                    {
                                                        Reflection.GetNameFromEnumValue(
                                                            ScriptKeys.SUCCESS),
                                                        false.ToString()
                                                    }
                                                }));
                                            streamWriter.Flush();
                                            TCPClient.Close();
                                            return;
                                    }

                                    do
                                    {
                                        var notificationTCPQueueElement = new NotificationTCPQueueElement();
                                        if (
                                            !NotificationTCPQueue.Dequeue(
                                                (int) corradeConfiguration.TCPNotificationThrottle,
                                                ref notificationTCPQueueElement))
                                            continue;
                                        if (notificationTCPQueueElement.Equals(default(NotificationTCPQueueElement)) ||
                                            !notificationTCPQueueElement.IPEndPoint.Equals(remoteEndPoint))
                                            continue;
                                        streamWriter.WriteLine(KeyValue.Encode(notificationTCPQueueElement.Message));
                                        streamWriter.Flush();
                                    } while (runTCPNotificationsServer && TCPClient.Connected);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.TCP_NOTIFICATIONS_SERVER_ERROR),
                            ex.PrettyPrint());
                    }
                    finally
                    {
                        if (remoteEndPoint != null && commandGroup != null &&
                            !commandGroup.Equals(default(Configuration.Group)))
                            lock (GroupNotificationsLock)
                            {
                                var notification =
                                    GroupNotifications.AsParallel().FirstOrDefault(
                                        o =>
                                            o.GroupUUID.Equals(commandGroup.UUID));
                                if (notification != null)
                                {
                                    var
                                        notificationTCPDestination =
                                            new Dictionary<Configuration.Notifications, HashSet<IPEndPoint>>
                                                ();
                                    notification.NotificationTCPDestination.AsParallel().ForAll(o =>
                                    {
                                        switch (o.Value.Contains(remoteEndPoint))
                                        {
                                            case true:
                                                var destinations =
                                                    new HashSet<IPEndPoint>(
                                                        o.Value.Where(p => !p.Equals(remoteEndPoint)));
                                                notificationTCPDestination.Add(o.Key, destinations);
                                                break;

                                            default:
                                                notificationTCPDestination.Add(o.Key, o.Value);
                                                break;
                                        }
                                    });

                                    GroupNotifications.Remove(notification);
                                    GroupNotifications.Add(new Notifications
                                    {
                                        GroupName = notification.GroupName,
                                        GroupUUID = notification.GroupUUID,
                                        HTTPNotifications =
                                            notification.HTTPNotifications,
                                        NotificationTCPDestination = notificationTCPDestination,
                                        Afterburn = notification.Afterburn,
                                        Data = notification.Data
                                    });
                                    // Build the group notification cache.
                                    GroupNotificationsCache.Clear();
                                    var LockObject = new object();
                                    new List<Configuration.Notifications>(
                                            Reflection.GetEnumValues<Configuration.Notifications>())
                                        .AsParallel().ForAll(o =>
                                        {
                                            GroupNotifications.AsParallel()
                                                .Where(p => p.NotificationMask.IsMaskFlagSet(o)).ForAll(p =>
                                                {
                                                    lock (LockObject)
                                                    {
                                                        if (GroupNotificationsCache.ContainsKey(o))
                                                        {
                                                            GroupNotificationsCache[o].Add(p);
                                                            return;
                                                        }
                                                        GroupNotificationsCache.Add(o,
                                                            new HashSet<Notifications> {p});
                                                    }
                                                });
                                        });
                                }
                            }
                    }
                })
                {
                    IsBackground = true
                }.Start();
            } while (runTCPNotificationsServer);
        }

        private static bool ConsoleCtrlCheck(NativeMethods.CtrlType ctrlType)
        {
            // Set the user disconnect semaphore.
            ConnectionSemaphores['u'].Set();
            // Wait for threads to finish.
            Thread.Sleep((int) corradeConfiguration.ServicesTimeout);
            return true;
        }

        /// <summary>
        ///     Used to check whether a group UUID matches a group password.
        /// </summary>
        /// <param name="group">the UUID of the group</param>
        /// <param name="password">the password for the group</param>
        /// <returns>true if the agent has authenticated</returns>
        public static bool Authenticate(UUID group, string password)
        {
            /*
             * If the master override feature is enabled and the password matches the
             * master override password then consider the request to be authenticated.
             * Otherwise, check that the password matches the password for the group.
             */
            return !group.Equals(UUID.Zero) && !string.IsNullOrEmpty(password) &&
                   (corradeConfiguration.EnableMasterPasswordOverride &&
                    !string.IsNullOrEmpty(corradeConfiguration.MasterPasswordOverride) && (
                        string.Equals(corradeConfiguration.MasterPasswordOverride, password,
                            StringComparison.Ordinal) ||
                        Utils.SHA1String(password)
                            .Equals(corradeConfiguration.MasterPasswordOverride, StringComparison.OrdinalIgnoreCase)) ||
                    corradeConfiguration.Groups.AsParallel().Any(
                        o =>
                            group.Equals(o.UUID) &&
                            (string.Equals(o.Password, password, StringComparison.Ordinal) ||
                             Utils.SHA1String(password)
                                 .Equals(o.Password, StringComparison.OrdinalIgnoreCase))));
        }

        /// <summary>
        ///     Used to check whether a group has certain permissions for Corrade.
        /// </summary>
        /// <param name="group">the UUID of the group</param>
        /// <param name="permission">the numeric Corrade permission</param>
        /// <returns>true if the group has permission</returns>
        private static bool HasCorradePermission(UUID group, ulong permission)
        {
            return !permission.Equals(0) && corradeConfiguration.Groups.AsParallel()
                       .Any(
                           o => group.Equals(o.UUID) &&
                                o.PermissionMask.IsMaskFlagSet((Configuration.Permissions) permission));
        }

        /// <summary>
        ///     Fetches a Corrade group from a key-value formatted message message.
        /// </summary>
        /// <param name="message">the message to inspect</param>
        /// <returns>the configured group</returns>
        public static Configuration.Group GetCorradeGroupFromMessage(string message, Configuration corradeConfiguration)
        {
            var group =
                wasInput(KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                    message));
            UUID groupUUID;
            return UUID.TryParse(group, out groupUUID)
                ? corradeConfiguration.Groups.AsParallel().FirstOrDefault(o => o.UUID.Equals(groupUUID))
                : corradeConfiguration.Groups.AsParallel()
                    .FirstOrDefault(o => string.Equals(group, o.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        ///     Used to check whether a group has a certain notification for Corrade.
        /// </summary>
        /// <param name="group">the UUID of the group</param>
        /// <param name="notification">the numeric Corrade notification</param>
        /// <returns>true if the group has the notification</returns>
        private static bool GroupHasNotification(UUID group, ulong notification)
        {
            return !notification.Equals(0) && corradeConfiguration.Groups.AsParallel().Any(
                       o => group.Equals(o.UUID) &&
                            o.NotificationMask.IsMaskFlagSet((Configuration.Notifications) notification));
        }

        /// <summary>
        ///     Posts messages to console or log-files.
        /// </summary>
        /// <param name="messages">a list of messages</param>
        public static void Feedback(params string[] messages)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(
                () => { CorradeLog.Info(string.Join(CORRADE_CONSTANTS.ERROR_SEPARATOR, messages)); },
                corradeConfiguration.MaximumLogThreads, corradeConfiguration.ServicesTimeout);
        }

        /// <summary>
        ///     Posts messages to console or log-files.
        /// </summary>
        /// <param name="messages">a list of messages</param>
        public static void Feedback(object[] messages)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(
                () =>
                {
                    foreach (var message in messages)
                        CorradeLog.Info(message);
                },
                corradeConfiguration.MaximumLogThreads, corradeConfiguration.ServicesTimeout);
        }

        public static int Main(string[] args)
        {
            if (!Environment.UserInteractive)
            {
                // run as a service
                Run(new Corrade());
                return 0;
            }

            switch (args.Any())
            {
                case false:
                    // run interactively and log to console
                    var corrade = new Corrade();
                    corrade.OnStart(null);
                    return 0;

                default:
                    var exitCode = 0;
                    if (Parser.Default.ParseArgumentsStrict(args, new CommandLineOptions(), (o, p) =>
                    {
                        switch (o)
                        {
                            case "info":
                                var infoOptions = (InfoSubOptions) p;
                                if (infoOptions == null)
                                {
                                    exitCode = -1;
                                    return;
                                }
                                Console.WriteLine("Version: " + CORRADE_CONSTANTS.CORRADE_VERSION);
                                Console.WriteLine("Compiled on: " + CORRADE_CONSTANTS.CORRADE_COMPILE_DATE);
                                break;

                            case "install":
                                var installOptions = (InstallSubOptions) p;
                                if (installOptions == null)
                                {
                                    exitCode = -1;
                                    return;
                                }
                                InstalledServiceName = installOptions.Name;
                                // If administrator privileges are obtained, then install the service.
                                if (new WindowsPrincipal(WindowsIdentity.GetCurrent())
                                    .IsInRole(WindowsBuiltInRole.Administrator))
                                {
                                    try
                                    {
                                        // install the service with the Windows Service Control Manager (SCM)
                                        ManagedInstallerClass.InstallHelper(new[]
                                            {Assembly.GetExecutingAssembly().Location});
                                    }
                                    catch (Exception ex)
                                    {
                                        if (ex.InnerException != null &&
                                            ex.InnerException.GetType() == typeof(Win32Exception))
                                        {
                                            var we = (Win32Exception) ex.InnerException;
                                            Console.WriteLine("Error(0x{0:X}): Service already installed!",
                                                we.ErrorCode);
                                            exitCode = we.ErrorCode;
                                        }
                                        Console.WriteLine(ex);
                                        exitCode = -1;
                                    }
                                    break;
                                }
                                if (!wasSharpNET.Platform.Windows.Utilities.ElevatePrivileges())
                                {
                                    Feedback(
                                        Reflection.GetDescriptionFromEnumValue(
                                            Enumerations.ConsoleMessage.UNABLE_TO_INSTALL_SERVICE));
                                    exitCode = -1;
                                }
                                break;

                            case "uninstall":
                                var uninstallOptions = (UninstallSubOptions) p;
                                if (uninstallOptions == null)
                                {
                                    exitCode = -1;
                                    return;
                                }
                                InstalledServiceName = uninstallOptions.Name;
                                // If administrator privileges are obtained, then uninstall the service.
                                if (new WindowsPrincipal(WindowsIdentity.GetCurrent())
                                    .IsInRole(WindowsBuiltInRole.Administrator))
                                {
                                    try
                                    {
                                        // uninstall the service from the Windows Service Control Manager (SCM)
                                        ManagedInstallerClass.InstallHelper(new[]
                                            {"/u", Assembly.GetExecutingAssembly().Location});
                                    }
                                    catch (Exception ex)
                                    {
                                        if (ex.InnerException?.GetType() == typeof(Win32Exception))
                                        {
                                            var we = (Win32Exception) ex.InnerException;
                                            Console.WriteLine("Error(0x{0:X}): Service not installed!", we.ErrorCode);
                                            exitCode = we.ErrorCode;
                                        }
                                        Console.WriteLine(ex);
                                        exitCode = -1;
                                    }
                                    break;
                                }
                                if (!wasSharpNET.Platform.Windows.Utilities.ElevatePrivileges())
                                {
                                    Feedback(
                                        Reflection.GetDescriptionFromEnumValue(
                                            Enumerations.ConsoleMessage.UNABLE_TO_UNINSTALL_SERVICE));
                                    exitCode = -1;
                                }
                                break;
                        }
                    }))
                        Environment.Exit(Parser.DefaultExitCodeFail);
                    return exitCode;
            }
        }

        protected override void OnStop()
        {
            base.OnStop();
            ConnectionSemaphores['u'].Set();
        }

        protected override void OnStart(string[] args)
        {
            base.OnStart(args);
            //Debugger.Break();
            programThread = new Thread(new Corrade().Program);
            programThread.Start();
        }

        // Main entry point.
        public void Program()
        {
            // Set default en-US culture with no overrides in order to be compatible with SecondLife.
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US", false);
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US", false);

            // Prevent process suspension under Windows.
            if (Utils.GetRunningPlatform().Equals(Utils.Platform.Windows))
                NativeMethods.PreventCorradeSuspend();

            // Remove OpenMetaverse logging.
            Settings.LOG_LEVEL = OpenMetaverse.Helpers.LogLevel.None;

            // Set the current directory to the service directory.
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            // Set location of debugging symbols.
            Environment.SetEnvironmentVariable(@"_NT_SYMBOL_PATH",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    CORRADE_CONSTANTS.DEBUG_FOLDER_NAME));

            var initialNucleusPort = 0;
            var firstRun = false;
            // Enter configuration stage in case no configuration file is found.
            switch (!File.Exists(CORRADE_CONSTANTS.CONFIGURATION_FILE))
            {
                case true:
                    // Check that the HTTP listener is supported.
                    if (!HttpListener.IsSupported)
                    {
                        $"{Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.HTTP_SERVER_NOT_SUPPORTED)} {"Could not enter configuration stage - please configure Corrade with a different tool or manually."}"
                            .WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        return;
                    }
                    // Attempt to retrieve a new unbound port.
                    if (!Utilities.TryGetUnusedPort(IPAddress.Any, out initialNucleusPort))
                    {
                        "Could not find a port to bind to! You will need to configure Corrade manually using a different tool."
                            .WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        return;
                    }
                    // Compose prefix.
                    var prefix = $"http://+:{initialNucleusPort}/";
                    // Start Nucleus without authentication.
                    NucleusHTTPServer = new NucleusHTTPServer
                    {
                        AuthenticationSchemes = AuthenticationSchemes.Anonymous
                    };
                    if (!NucleusHTTPServer.Start(new List<string> {prefix}))
                    {
                        Console.WriteLine();
                        "Unable to start Nucleus server for bootstrapping - you will need to use a different configuration tool to configure Corrade."
                            .WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        Console.WriteLine();
                        "Press any key to terminate."
                            .WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        Console.ReadKey();
                        Environment.Exit(-1);
                    }
                    ConsoleCancelEventHandler ConsoleCancelKeyPress = (sender, args) =>
                    {
                        try
                        {
                            NucleusHTTPServer?.Stop((int) corradeConfiguration.ServicesTimeout);
                        }
                        catch (Exception)
                        {
                            /* We are going down and we do not care. */
                        }
                    };
                    EventHandler ConsoleXButton = o =>
                    {
                        try
                        {
                            NucleusHTTPServer?.Stop((int) corradeConfiguration.ServicesTimeout);
                        }
                        catch (Exception)
                        {
                            /* We are going down and we do not care. */
                        }
                        return true;
                    };
                    var consoleSpinner = new ConsoleSpin(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                    if (Environment.UserInteractive)
                    {
                        if (Utils.GetRunningPlatform().Equals(Utils.Platform.Windows))
                        {
                            // Setup native console handler.
                            ConsoleEventHandler += ConsoleXButton;
                            NativeMethods.SetConsoleCtrlHandler(ConsoleEventHandler, true);
                            NativeMethods.SetCorradeConsole();
                        }
                        Console.CancelKeyPress += ConsoleCancelKeyPress;
                        Console.WriteLine();
                        // Write Logo.
                        CORRADE_CONSTANTS.LOGO.WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        // Write Sub-Logo.
                        CORRADE_CONSTANTS.SUB_LOGO.WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        Console.WriteLine();
                        "No Corrade configuration file has been found - you will need to bootstrap Corrade or shut down and use a different configuration tool to configure Corrade."
                            .WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        "The configuration panel is available at: ".WriteLine(
                            ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        Console.WriteLine();
                        $"http://{Dns.GetHostEntry(Environment.MachineName).HostName}:{initialNucleusPort}/bootstrap"
                            .WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER,
                                ConsoleColor.Yellow);
                        Console.WriteLine();
                        "Waiting for bootstrap...".WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                        Console.WriteLine();
                        consoleSpinner.Start();
                    }
                    // Open browser to bootstrap Corrade.
                    try
                    {
                        Process.Start($@"http://127.0.0.1:{initialNucleusPort}/bootstrap");
                    }
                    catch
                    {
                        // Could not open the URL automatically.
                    }
                    // Watch the directory for files.
                    var watchConfiguration = new FileSystemWatcher(Directory.GetCurrentDirectory(),
                            CORRADE_CONSTANTS.CONFIGURATION_FILE)
                        {EnableRaisingEvents = true};
                    // Wait for the Corrade configuration to be created.
                    watchConfiguration.WaitForChanged(WatcherChangeTypes.Created);
                    // Attempt to acquire an exclusive lock on the configuration file.
                    lock (ConfigurationFileLock)
                    {
                        ACQUIRE_EXCLUSIVE_LOCK:
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                        try
                        {
                            using (
                                var fileStream = new FileStream(CORRADE_CONSTANTS.CONFIGURATION_FILE, FileMode.Open,
                                    FileAccess.ReadWrite, FileShare.None, 16384, true))
                            {
                                corradeConfiguration.Load(fileStream, ref corradeConfiguration);
                            }
                        }
                        catch (IOException)
                        {
                            goto ACQUIRE_EXCLUSIVE_LOCK;
                        }
                        catch (Exception)
                        {
                            consoleSpinner.Stop();
                            Console.WriteLine();
                            "Unable to create configuration file! Please use a different tool to configure Corrade."
                                .WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                            return;
                        }
                    }
                    // Unbind from console keypress event.
                    if (Environment.UserInteractive)
                    {
                        // Stop the console spinner.
                        consoleSpinner.Stop();
                        consoleSpinner.Dispose();
                        Console.Clear();
                        if (Utils.GetRunningPlatform().Equals(Utils.Platform.Windows))
                            ConsoleEventHandler -= ConsoleXButton;
                        Console.CancelKeyPress -= ConsoleCancelKeyPress;
                    }
                    // This was a first run.
                    firstRun = true;
                    break;

                default:
                    // Load the configuration file.
                    lock (ConfigurationFileLock)
                    {
                        try
                        {
                            using (
                                var fileStream = new FileStream(CORRADE_CONSTANTS.CONFIGURATION_FILE, FileMode.Open,
                                    FileAccess.Read, FileShare.Read, 16384, true))
                            {
                                corradeConfiguration.Load(fileStream, ref corradeConfiguration);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (Environment.UserInteractive)
                            {
                                Console.WriteLine("{0} {1}",
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.UNABLE_TO_LOAD_CORRADE_CONFIGURATION),
                                    ex.PrettyPrint());
                            }
                            return;
                        }
                    }
                    break;
            }

            // Branch on platform and set-up termination handlers.
            switch (Utils.GetRunningPlatform())
            {
                case Utils.Platform.Windows:
                    if (Environment.UserInteractive)
                    {
                        // Setup console handler.
                        Console.CancelKeyPress += (sender, args) => ConnectionSemaphores['u'].Set();
                        ConsoleEventHandler += ConsoleCtrlCheck;
                        NativeMethods.SetConsoleCtrlHandler(ConsoleEventHandler, true);
                        NativeMethods.SetCorradeConsole();
                    }
                    break;
            }

            // Nucleus would have generated the prefix by now and saved it to the configuration.
            // Either there is no prefix set in the configuration and nucleus has not ran -
            // in which case we need to generate a prefix for Nucleus if Nucleus is enabled.
            if (corradeConfiguration.EnableNucleusServer &&
                string.IsNullOrEmpty(corradeConfiguration.NucleusServerPrefix) && initialNucleusPort.Equals(0) &&
                Utilities.TryGetUnusedPort(IPAddress.Any, out initialNucleusPort))
            {
                corradeConfiguration.NucleusServerPrefix = $"http://+:{initialNucleusPort}/";
                lock (ConfigurationFileLock)
                {
                    using (
                        var fileStream = new FileStream(CORRADE_CONSTANTS.CONFIGURATION_FILE, FileMode.Create,
                            FileAccess.Write,
                            FileShare.None, 16384, true))
                    {
                        corradeConfiguration.Save(fileStream, ref corradeConfiguration);
                    }
                }
            }

            // Initialize the loggers.
            var CorradeLogger = LogHierarchy.LoggerFactory.CreateLogger("Corrade");
            CorradeLogger.Hierarchy = LogHierarchy;

            // Initialize the console logger if we are running in interactive mode.
            if (Environment.UserInteractive)
            {
                var layout = new PatternLayout
                {
                    ConversionPattern =
                        @"%date{" + CORRADE_CONSTANTS.DATE_TIME_STAMP + "} : " + corradeConfiguration.FirstName +
                        @" " +
                        corradeConfiguration.LastName + @" : %message%newline"
                };
                layout.ActivateOptions();
                var consoleAppender = new ConsoleAppender
                {
                    Layout = layout
                };
                consoleAppender.ActivateOptions();
                CorradeLogger.AddAppender(consoleAppender);
            }

            // Only enable the logging file if it has been enabled.
            switch (corradeConfiguration.ClientLogEnabled)
            {
                case true:
                    var layout = new PatternLayout
                    {
                        ConversionPattern = @"%date{" + CORRADE_CONSTANTS.DATE_TIME_STAMP + "} : " +
                                            corradeConfiguration.FirstName +
                                            @" " +
                                            corradeConfiguration.LastName + " : %message%newline"
                    };
                    var rollingFileAppender = new RollingFileAppender
                    {
                        Layout = layout,
                        File = corradeConfiguration.ClientLogFile,
                        StaticLogFileName = true,
                        RollingStyle = RollingFileAppender.RollingMode.Size,
                        MaxSizeRollBackups = 10,
                        MaximumFileSize = "1MB"
                    };
                    layout.ActivateOptions();
                    rollingFileAppender.ActivateOptions();
                    CorradeLogger.AddAppender(rollingFileAppender);
                    break;

                default:
                    break;
            }

            switch (Utils.GetRunningPlatform())
            {
                case Utils.Platform.Windows: // only initialize the event logger on Windows in service mode
                    if (!Environment.UserInteractive)
                    {
                        var eventLogLayout = new PatternLayout
                        {
                            ConversionPattern = @"%date{" + CORRADE_CONSTANTS.DATE_TIME_STAMP + "} : " +
                                                corradeConfiguration.FirstName +
                                                @" " +
                                                corradeConfiguration.LastName + " : %message%newline"
                        };
                        var eventLogAppender = new EventLogAppender
                        {
                            Layout = eventLogLayout,
                            ApplicationName = !string.IsNullOrEmpty(InstalledServiceName)
                                ? InstalledServiceName
                                : CORRADE_CONSTANTS.DEFAULT_SERVICE_NAME
                        };
                        eventLogLayout.ActivateOptions();
                        eventLogAppender.ActivateOptions();
                        CorradeLogger.AddAppender(eventLogAppender);
                    }
                    break;

                case Utils.Platform.OSX:
                case Utils.Platform.Linux:
                    var sysLogLayout = new PatternLayout
                    {
                        ConversionPattern = @"%date{" + CORRADE_CONSTANTS.DATE_TIME_STAMP + "} : " +
                                            corradeConfiguration.FirstName +
                                            @" " +
                                            corradeConfiguration.LastName + " : %message%newline"
                    };
                    var sysLogAppender = new LocalSyslogAppender
                    {
                        Layout = sysLogLayout,
                        Facility = LocalSyslogAppender.SyslogFacility.Daemons
                    };
                    sysLogLayout.ActivateOptions();
                    sysLogAppender.ActivateOptions();
                    CorradeLogger.AddAppender(sysLogAppender);
                    break;
            }

            // Set the log level.
            CorradeLogger.Level = Level.All;
            CorradeLogger.Repository.Configured = true;
            // Initialize the Corrade log.
            CorradeLog = new LogImpl(CorradeLogger);

            // Only enable the logging file if it has been enabled.
            switch (corradeConfiguration.OpenMetaverseLogEnabled)
            {
                case true:
                    var OpenMetaverseLogger = LogHierarchy.LoggerFactory.CreateLogger("OpenMetaverse");
                    OpenMetaverseLogger.Hierarchy = LogHierarchy;

                    var layout = new PatternLayout
                    {
                        ConversionPattern = @"%date{" + CORRADE_CONSTANTS.DATE_TIME_STAMP + "} : " +
                                            corradeConfiguration.FirstName +
                                            @" " +
                                            corradeConfiguration.LastName + " : %message%newline"
                    };
                    var rollingFileAppender = new RollingFileAppender
                    {
                        Layout = layout,
                        File = corradeConfiguration.OpenMetaverseLogFile,
                        StaticLogFileName = true,
                        RollingStyle = RollingFileAppender.RollingMode.Size,
                        MaxSizeRollBackups = 10,
                        MaximumFileSize = "1MB"
                    };
                    layout.ActivateOptions();
                    rollingFileAppender.ActivateOptions();
                    OpenMetaverseLogger.AddAppender(rollingFileAppender);

                    OpenMetaverseLogger.Level = Level.All;
                    OpenMetaverseLogger.Repository.Configured = true;
                    OpenMetaverseLog = new LogImpl(OpenMetaverseLogger);
                    Logger.OnLogMessage += OnLogOpenmetaverseMessage;
                    break;

                default:
                    Logger.OnLogMessage -= OnLogOpenmetaverseMessage;
                    break;
            }

            if (Environment.UserInteractive)
            {
                Console.WriteLine();
                // Write Logo.
                CORRADE_CONSTANTS.LOGO.WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                // Write Sub-Logo.
                CORRADE_CONSTANTS.SUB_LOGO.WriteLine(ConsoleExtensions.ConsoleTextAlignment.TOP_CENTER);
                Console.WriteLine();
            }

            // Check configuration file compatiblity.
            Version minimalConfig;
            Version versionConfig;
            if (
                !Version.TryParse(CORRADE_CONSTANTS.ASSEMBLY_CUSTOM_ATTRIBUTES["configuration"], out minimalConfig) ||
                !Version.TryParse(corradeConfiguration.Version, out versionConfig) ||
                !minimalConfig.Major.Equals(versionConfig.Major) || !minimalConfig.Minor.Equals(versionConfig.Minor))
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.CONFIGURATION_FILE_VERSION_MISMATCH));

            // Load language detection
            try
            {
                languageDetector.AddAllLanguages();
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_LOADING_LANGUAGE_DETECTION),
                    ex.PrettyPrint());
                Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
            }

            // Set-up watcher for dynamically reading the configuration file.
            FileSystemEventHandler HandleConfigurationFileChanged = null;
            try
            {
                ConfigurationWatcher.Path = Directory.GetCurrentDirectory();
                ConfigurationWatcher.Filter = CORRADE_CONSTANTS.CONFIGURATION_FILE;
                ConfigurationWatcher.NotifyFilter = NotifyFilters.LastWrite;
                HandleConfigurationFileChanged = (sender, args) => ConfigurationChangedTimer.Change(1000, 0);
                ConfigurationWatcher.Changed += HandleConfigurationFileChanged;
                ConfigurationWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SETTING_UP_CONFIGURATION_WATCHER),
                    ex.PrettyPrint());
                Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
            }

            // Set-up watcher for dynamically reading the notifications file.
            FileSystemEventHandler HandleNotificationsFileChanged = null;
            try
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(),
                    CORRADE_CONSTANTS.STATE_DIRECTORY));

                NotificationsWatcher.Path = Path.Combine(Directory.GetCurrentDirectory(),
                    CORRADE_CONSTANTS.STATE_DIRECTORY);
                NotificationsWatcher.Filter = CORRADE_CONSTANTS.NOTIFICATIONS_STATE_FILE;
                NotificationsWatcher.NotifyFilter = NotifyFilters.LastWrite;
                HandleNotificationsFileChanged = (sender, args) => NotificationsChangedTimer.Change(1000, 0);
                NotificationsWatcher.Changed += HandleNotificationsFileChanged;
                NotificationsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SETTING_UP_NOTIFICATIONS_WATCHER),
                    ex.PrettyPrint());
                Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
            }

            // Set-up watcher for dynamically reading the group schedules file.
            FileSystemEventHandler HandleGroupSchedulesFileChanged = null;
            try
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(),
                    CORRADE_CONSTANTS.STATE_DIRECTORY));

                SchedulesWatcher.Path = Path.Combine(Directory.GetCurrentDirectory(),
                    CORRADE_CONSTANTS.STATE_DIRECTORY);
                SchedulesWatcher.Filter = CORRADE_CONSTANTS.GROUP_SCHEDULES_STATE_FILE;
                SchedulesWatcher.NotifyFilter = NotifyFilters.LastWrite;
                HandleGroupSchedulesFileChanged = (sender, args) => GroupSchedulesChangedTimer.Change(1000, 0);
                SchedulesWatcher.Changed += HandleGroupSchedulesFileChanged;
                SchedulesWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SETTING_UP_SCHEDULES_WATCHER),
                    ex.PrettyPrint());
                Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
            }

            // Set-up watcher for dynamically reading the feeds file.
            FileSystemEventHandler HandleGroupFeedsFileChanged = null;
            try
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(),
                    CORRADE_CONSTANTS.STATE_DIRECTORY));

                GroupFeedWatcher.Path = Path.Combine(Directory.GetCurrentDirectory(),
                    CORRADE_CONSTANTS.STATE_DIRECTORY);
                GroupFeedWatcher.Filter = CORRADE_CONSTANTS.FEEDS_STATE_FILE;
                GroupFeedWatcher.NotifyFilter = NotifyFilters.LastWrite;
                HandleGroupFeedsFileChanged = (sender, args) => GroupFeedsChangedTimer.Change(1000, 0);
                GroupFeedWatcher.Changed += HandleGroupFeedsFileChanged;
                GroupFeedWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SETTING_UP_FEEDS_WATCHER),
                    ex.PrettyPrint());
                Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
            }

            // Set-up watcher for dynamically reading the group soft bans file.
            FileSystemEventHandler HandleGroupSoftBansFileChanged = null;
            try
            {
                // Create the state directory if it does not exist.
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(),
                    CORRADE_CONSTANTS.STATE_DIRECTORY));

                GroupSoftBansWatcher.Path = Path.Combine(Directory.GetCurrentDirectory(),
                    CORRADE_CONSTANTS.STATE_DIRECTORY);
                GroupSoftBansWatcher.Filter = CORRADE_CONSTANTS.GROUP_SOFT_BAN_STATE_FILE;
                GroupSoftBansWatcher.NotifyFilter = NotifyFilters.LastWrite;
                HandleGroupSoftBansFileChanged = (sender, args) => GroupSoftBansChangedTimer.Change(1000, 0);
                GroupSoftBansWatcher.Changed += HandleGroupSoftBansFileChanged;
                GroupSoftBansWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SETTING_UP_SOFT_BANS_WATCHER),
                    ex.PrettyPrint());
                Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
            }

            // Set-up the SIML bot in case it has been enabled.
            FileSystemEventHandler HandleSIMLBotConfigurationChanged = null;
            try
            {
                SIMLBotConfigurationWatcher.Path = Path.Combine(Directory.GetCurrentDirectory(),
                    SIML_BOT_CONSTANTS.ROOT_DIRECTORY);
                SIMLBotConfigurationWatcher.NotifyFilter = NotifyFilters.LastWrite;
                HandleSIMLBotConfigurationChanged = (sender, args) => SIMLConfigurationChangedTimer.Change(1000, 0);
                SIMLBotConfigurationWatcher.Changed += HandleSIMLBotConfigurationChanged;
                if (corradeConfiguration.EnableSIML)
                    SIMLBotConfigurationWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SETTING_UP_SIML_CONFIGURATION_WATCHER),
                    ex.PrettyPrint());
                Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
            }

            // Load Corrade caches.
            LoadCorradeCache.Invoke();
            // Load group members.
            LoadGroupMembersState.Invoke();
            // Load notification state.
            LoadNotificationState.Invoke();
            // Load group scheduls state.
            LoadGroupSchedulesState.Invoke();
            // Load feeds state.
            LoadGroupFeedState.Invoke();
            // Load group soft bans state.
            LoadGroupSoftBansState.Invoke();
            // Load group Bayes classifications.
            LoadGroupBayesClassificiations.Invoke();
            // Load group cookies.
            LoadGroupCookiesState.Invoke();

            // Start the callback thread to send callbacks.
            var CallbackThread = new Thread(() =>
            {
                do
                {
                    CallbackThreadState.Wait();
                    try
                    {
                        var callbackQueueElement = new CallbackQueueElement();
                        if (CallbackQueue.Dequeue((int) corradeConfiguration.CallbackThrottle,
                            ref callbackQueueElement))
                            CorradeThreadPool[Threading.Enumerations.ThreadType.POST].Spawn(async () =>
                            {
                                wasHTTPClient wasHTTPClient;
                                lock (GroupHTTPClientsLock)
                                {
                                    GroupHTTPClients.TryGetValue(callbackQueueElement.GroupUUID,
                                        out wasHTTPClient);
                                }
                                if (wasHTTPClient != null)
                                    await
                                        wasHTTPClient.POST(callbackQueueElement.URL,
                                            KeyValue.Escape(callbackQueueElement.message, wasOutput));
                            }, corradeConfiguration.MaximumPOSTThreads);
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.CALLBACK_ERROR),
                            ex.PrettyPrint());
                    }
                } while (runCallbackThread);
            })
            {
                IsBackground = true
            };
            CallbackThread.Start();
            // Start the notification thread for notifications.
            var NotificationThread = new Thread(() =>
            {
                do
                {
                    NotificationThreadState.Wait();

                    try
                    {
                        var notificationQueueElement = new NotificationQueueElement();
                        if (NotificationQueue.Dequeue((int) corradeConfiguration.NotificationThrottle,
                            ref notificationQueueElement))
                            CorradeThreadPool[Threading.Enumerations.ThreadType.POST].Spawn(async () =>
                                {
                                    wasHTTPClient wasHTTPClient;
                                    lock (GroupHTTPClientsLock)
                                    {
                                        GroupHTTPClients.TryGetValue(notificationQueueElement.GroupUUID,
                                            out wasHTTPClient);
                                    }
                                    if (wasHTTPClient != null)
                                        await
                                            wasHTTPClient.POST(notificationQueueElement.URL,
                                                KeyValue.Escape(notificationQueueElement.Message, wasOutput));
                                },
                                corradeConfiguration.MaximumPOSTThreads);
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.NOTIFICATION_ERROR),
                            ex.PrettyPrint());
                    }
                } while (runNotificationThread);
            })
            {
                IsBackground = true
            };
            NotificationThread.Start();

            do
            {
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.CYCLING_SIMULATORS));

                // If this is Second Life, ensure that the scripted agent status is set as per the Terms of Service.
                if (string.Equals(corradeConfiguration.LoginURL,
                        Settings.AGNI_LOGIN_SERVER, StringComparison.InvariantCultureIgnoreCase) &&
                    corradeConfiguration.AutoScriptedAgentStatus)
                    try
                    {
                        using (var status = new ScriptedAgentStatus())
                        {
                            CorradeScriptedAgentStatus = status.IsScriptedAgent();
                            if (CorradeScriptedAgentStatus == false)
                            {
                                status.SetScriptedAgentStatus(true);
                                Feedback(Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.REGISTERED_AS_SCRIPTED_AGENT));
                            }
                        }
                    }
                    catch (ScriptedAgentStatusException ex)
                    {
                        Feedback(Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.SCRIPTED_AGENT_STATUS), ex.Message);
                    }
                    catch (Exception ex)
                    {
                        Feedback(Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.SCRIPTED_AGENT_STATUS),
                            ex.PrettyPrint());
                    }

                // Update the configuration.
                UpdateDynamicConfiguration(corradeConfiguration, firstRun);

                // Get the next location.
                var location = StartLocationQueue.Dequeue();
                // Generate a grid location.
                var startLocation = new wasOpenMetaverse.Helpers.GridLocation(location);

                // Check if we have any start locations.
                if (StartLocationQueue.IsEmpty)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.START_LOCATIONS_EXHAUSTED));
                    break;
                }

                // Create a new login object.
                var login = new LoginParams(Client,
                    corradeConfiguration.FirstName,
                    corradeConfiguration.LastName,
                    corradeConfiguration.Password,
                    CORRADE_CONSTANTS.CLIENT_CHANNEL,
                    CORRADE_CONSTANTS.CORRADE_VERSION.ToString(Utils.EnUsCulture),
                    corradeConfiguration.LoginURL)
                {
                    Author = CORRADE_CONSTANTS.WIZARDRY_AND_STEAMWORKS,
                    AgreeToTos = corradeConfiguration.TOSAccepted,
                    UserAgent = CORRADE_CONSTANTS.USER_AGENT.ToString(),
                    Version = CORRADE_CONSTANTS.CORRADE_VERSION,
                    Timeout = (int) corradeConfiguration.ServicesTimeout,
                    LastExecEvent = CorradeLastExecStatus,
                    Platform = Utils.GetRunningPlatform().ToString(),
                    Start = startLocation.isCustom
                        ? NetworkManager.StartLocation(startLocation.Sim, startLocation.X, startLocation.Y,
                            startLocation.Z)
                        : location
                };

                // Install non-dynamic global event handlers.
                Client.Inventory.InventoryObjectOffered += HandleInventoryObjectOffered;
                Client.Network.LoginProgress += HandleLoginProgress;
                Client.Network.LoggedOut += HandleLoggedOut;
                Client.Appearance.AppearanceSet += HandleAppearanceSet;
                Client.Network.SimConnected += HandleSimulatorConnected;
                Client.Network.Disconnected += HandleDisconnected;
                Client.Network.SimDisconnected += HandleSimulatorDisconnected;
                Client.Network.EventQueueRunning += HandleEventQueueRunning;
                Client.Self.TeleportProgress += HandleTeleportProgress;
                Client.Self.ChatFromSimulator += HandleChatFromSimulator;
                Client.Groups.GroupJoinedReply += HandleGroupJoined;
                Client.Groups.GroupLeaveReply += HandleGroupLeave;
                Client.Sound.PreloadSound += HandlePreloadSound;
                Client.Self.IM += HandleSelfIM;

                // Start all event watchers.
                SIMLBotConfigurationWatcher.EnableRaisingEvents = true;
                ConfigurationWatcher.EnableRaisingEvents = true;
                NotificationsWatcher.EnableRaisingEvents = true;
                SchedulesWatcher.EnableRaisingEvents = true;
                GroupFeedWatcher.EnableRaisingEvents = true;
                GroupSoftBansWatcher.EnableRaisingEvents = true;

                // Start threads.
                GroupMembershipTimer.Change(TimeSpan.FromMilliseconds(corradeConfiguration.MembershipSweepInterval),
                    TimeSpan.FromMilliseconds(corradeConfiguration.MembershipSweepInterval));
                NotificationThreadState.Set();
                CallbackThreadState.Set();
                TCPNotificationsThreadState.Set();

                // Reset all semaphores.
                ConnectionSemaphores.Values.AsParallel().ForAll(o => o.Reset());

                // Log-in to the grid.
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.LOGGING_IN), location);
                Locks.ClientInstanceNetworkLock.EnterWriteLock();
                Client.Network.BeginLogin(login);
                Locks.ClientInstanceNetworkLock.ExitWriteLock();

                // Assume Corrade crashed.
                CorradeLastExecStatus = LastExecStatus.OtherCrash;
                // Wait for any semaphore.
                WaitHandle.WaitAny(ConnectionSemaphores.Values.ToArray());
                // User disconnect.
                CorradeLastExecStatus = LastExecStatus.Normal;

                // Stop all event watchers.
                SIMLBotConfigurationWatcher.EnableRaisingEvents = false;
                ConfigurationWatcher.EnableRaisingEvents = false;
                NotificationsWatcher.EnableRaisingEvents = false;
                SchedulesWatcher.EnableRaisingEvents = false;
                GroupFeedWatcher.EnableRaisingEvents = false;
                GroupSoftBansWatcher.EnableRaisingEvents = false;

                // Uninstall non-dynamic event handlers
                Client.Inventory.InventoryObjectOffered -= HandleInventoryObjectOffered;
                Client.Network.LoginProgress -= HandleLoginProgress;
                Client.Network.LoggedOut -= HandleLoggedOut;
                Client.Appearance.AppearanceSet -= HandleAppearanceSet;
                Client.Network.SimConnected -= HandleSimulatorConnected;
                Client.Network.Disconnected -= HandleDisconnected;
                Client.Network.SimDisconnected -= HandleSimulatorDisconnected;
                Client.Network.EventQueueRunning -= HandleEventQueueRunning;
                Client.Self.TeleportProgress -= HandleTeleportProgress;
                Client.Self.ChatFromSimulator -= HandleChatFromSimulator;
                Client.Groups.GroupJoinedReply -= HandleGroupJoined;
                Client.Groups.GroupLeaveReply -= HandleGroupLeave;
                Client.Sound.PreloadSound -= HandlePreloadSound;
                Client.Self.IM -= HandleSelfIM;

                // Suspend threads.
                GroupMembershipTimer.Change(0, 0);
                NotificationThreadState.Reset();
                CallbackThreadState.Reset();
                TCPNotificationsThreadState.Reset();

                // Perform the logout now.
                Locks.ClientInstanceNetworkLock.EnterWriteLock();
                if (Client.Network.Connected)
                {
                    // Full speed ahead; do not even attempt to grab a lock.
                    var LoggedOutEvent = new ManualResetEventSlim(false);
                    EventHandler<LoggedOutEventArgs> LoggedOutEventHandler = (sender, args) =>
                    {
                        CorradeLastExecStatus = LastExecStatus.Normal;
                        LoggedOutEvent.Set();
                    };
                    Client.Network.LoggedOut += LoggedOutEventHandler;
                    CorradeLastExecStatus = LastExecStatus.LogoutCrash;
                    Client.Network.BeginLogout();
                    if (!LoggedOutEvent.Wait((int) corradeConfiguration.ServicesTimeout))
                    {
                        CorradeLastExecStatus = LastExecStatus.LogoutFroze;
                        Client.Network.LoggedOut -= LoggedOutEventHandler;
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.TIMEOUT_LOGGING_OUT));
                    }
                    Client.Network.LoggedOut -= LoggedOutEventHandler;
                }
                Locks.ClientInstanceNetworkLock.ExitWriteLock();

                // If this is Second Life, return the agent status to its initial value if one was set initially.
                if (CorradeScriptedAgentStatus != null && string.Equals(corradeConfiguration.LoginURL,
                        Settings.AGNI_LOGIN_SERVER, StringComparison.InvariantCultureIgnoreCase) &&
                    corradeConfiguration.AutoScriptedAgentStatus)
                {
                    try
                    {
                        using (var status = new ScriptedAgentStatus())
                        {
                            status.SetScriptedAgentStatus(CorradeScriptedAgentStatus.Value);
                        }

                        Feedback(Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.UNREGISTERED_AS_SCRIPTED_AGENT));
                    }
                    catch (ScriptedAgentStatusException ex)
                    {
                        Feedback(Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.SCRIPTED_AGENT_STATUS), ex.Message);
                    }
                    catch (Exception ex)
                    {
                        Feedback(Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.SCRIPTED_AGENT_STATUS),
                            ex.PrettyPrint());
                    }
                }
                
            } while (!ConnectionSemaphores['u'].WaitOne(0));

            // Now log-out.
            Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.LOGGING_OUT));

            // Disable the configuration watcher.
            try
            {
                ConfigurationWatcher.EnableRaisingEvents = false;
                ConfigurationWatcher.Changed -= HandleConfigurationFileChanged;
            }
            catch (Exception)
            {
                /* We are going down and we do not care. */
            }
            // Disable the notifications watcher.
            try
            {
                NotificationsWatcher.EnableRaisingEvents = false;
                NotificationsWatcher.Changed -= HandleNotificationsFileChanged;
            }
            catch (Exception)
            {
                /* We are going down and we do not care. */
            }
            // Disable the group schedule watcher.
            try
            {
                SchedulesWatcher.EnableRaisingEvents = false;
                SchedulesWatcher.Changed -= HandleGroupSchedulesFileChanged;
            }
            catch (Exception)
            {
                /* We are going down and we do not care. */
            }
            // Disable the SIML bot configuration watcher.
            try
            {
                SIMLBotConfigurationWatcher.EnableRaisingEvents = false;
                SIMLBotConfigurationWatcher.Changed -= HandleSIMLBotConfigurationChanged;
            }
            catch (Exception)
            {
                /* We are going down and we do not care. */
            }
            // Disable the RSS feeds watcher.
            try
            {
                GroupFeedWatcher.EnableRaisingEvents = false;
                GroupFeedWatcher.Changed -= HandleGroupFeedsFileChanged;
            }
            catch (Exception)
            {
                /* We are going down and we do not care. */
            }
            // Disable the group soft bans watcher.
            try
            {
                GroupSoftBansWatcher.EnableRaisingEvents = false;
                GroupSoftBansWatcher.Changed -= HandleGroupSoftBansFileChanged;
            }
            catch (Exception)
            {
                /* We are going down and we do not care. */
            }

            // Reject any inventory that has not been accepted.
            lock (InventoryOffersLock)
            {
                InventoryOffers.Values.AsParallel().ForAll(o =>
                {
                    o.Args.Accept = false;
                    o.Event.Set();
                });
            }

            // Stop the sphere effects expiration timer.
            EffectsExpirationTimer.Stop();
            // Stop the group membership timer.
            GroupMembershipTimer.Stop();
            // Stop the group feed thread.
            GroupFeedsTimer.Stop();
            // Stop the group schedules timer.
            GroupSchedulesTimer.Stop();
            // Stop the heartbeat timer.
            CorradeHeartBeatTimer.Stop();

            // Stop the notification thread.
            try
            {
                runNotificationThread = false;
                if (
                    NotificationThread.ThreadState.Equals(ThreadState.Running) ||
                    NotificationThread.ThreadState.Equals(ThreadState.WaitSleepJoin))
                    if (!NotificationThread.Join(1000))
                    {
                        NotificationThread.Abort();
                        NotificationThread.Join();
                    }
            }
            catch (Exception)
            {
                /* We are going down and we do not care. */
            }
            finally
            {
                NotificationThread = null;
            }

            // Stop the callback thread.
            try
            {
                runCallbackThread = false;
                if (
                    CallbackThread.ThreadState.Equals(ThreadState.Running) ||
                    CallbackThread.ThreadState.Equals(ThreadState.WaitSleepJoin))
                    if (!CallbackThread.Join(1000))
                    {
                        CallbackThread.Abort();
                        CallbackThread.Join();
                    }
            }
            catch (Exception)
            {
                /* We are going down and we do not care. */
            }
            finally
            {
                NotificationThread = null;
            }

            // Close HTTP server
            if (HttpListener.IsSupported && CorradeHTTPServer != null && CorradeHTTPServer.IsRunning)
            {
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.STOPPING_HTTP_SERVER));
                try
                {
                    CorradeHTTPServer?.Stop((int) corradeConfiguration.ServicesTimeout);
                }
                catch (Exception ex)
                {
                    Feedback(Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.HTTP_SERVER_ERROR),
                        ex.PrettyPrint());
                }
            }

            // Close Nucleus server
            if (HttpListener.IsSupported && NucleusHTTPServer != null && NucleusHTTPServer.IsRunning)
            {
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.STOPPING_NUCLEUS_SERVER));
                try
                {
                    NucleusHTTPServer?.Stop((int) corradeConfiguration.ServicesTimeout);
                }
                catch (Exception ex)
                {
                    Feedback(Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.NUCLEUS_SERVER_ERROR),
                        ex.PrettyPrint());
                }
            }

            // Write the last execution status.
            CorradeLastExecStatus = LastExecStatus.Normal;

            // Terminate.
            Environment.Exit(corradeConfiguration.ExitCodeExpected);
        }

        private static void OnLogOpenmetaverseMessage(object message, OpenMetaverse.Helpers.LogLevel level)
        {
            switch (level)
            {
                case OpenMetaverse.Helpers.LogLevel.Info:
                    OpenMetaverseLog.Info(message);
                    break;

                case OpenMetaverse.Helpers.LogLevel.Debug:
                    OpenMetaverseLog.Debug(message);
                    break;

                case OpenMetaverse.Helpers.LogLevel.Error:
                    OpenMetaverseLog.Error(message);
                    break;

                case OpenMetaverse.Helpers.LogLevel.Warning:
                    OpenMetaverseLog.Warn(message);
                    break;
            }
        }

        private static void HandlePreloadSound(object sender, PreloadSoundEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Preload, e),
                corradeConfiguration.MaximumNotificationThreads);

            // Check if the sound is already cached.
            Locks.ClientInstanceAssetsLock.EnterReadLock();
            if (Client.Assets.Cache.HasAsset(e.SoundID))
            {
                Locks.ClientInstanceAssetsLock.ExitReadLock();
                return;
            }
            Locks.ClientInstanceAssetsLock.ExitReadLock();

            // Start a thread to download the sound.
            CorradeThreadPool[Threading.Enumerations.ThreadType.PRELOAD].Spawn(
                () =>
                {
                    var RequestAssetEvent = new ManualResetEventSlim(false);
                    byte[] assetData = null;
                    var succeeded = false;
                    Locks.ClientInstanceAssetsLock.EnterReadLock();
                    Client.Assets.RequestAsset(e.SoundID, AssetType.Sound, false,
                        delegate(AssetDownload transfer, Asset asset)
                        {
                            if (!transfer.AssetID.Equals(e.SoundID))
                                return;
                            succeeded = transfer.Success;
                            assetData = asset.AssetData;
                            RequestAssetEvent.Set();
                        });
                    if (!RequestAssetEvent.Wait((int) corradeConfiguration.ServicesTimeout))
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.TIMEOUT_DOWNLOADING_PRELOAD_SOUND));
                    Locks.ClientInstanceAssetsLock.ExitReadLock();
                    if (succeeded)
                        if (corradeConfiguration.EnableHorde)
                            HordeDistributeCacheAsset(e.SoundID, assetData,
                                Configuration.HordeDataSynchronizationOption.Add);
                });
        }

        private static void HandleAvatarUpdate(object sender, AvatarUpdateEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.RadarAvatars, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleObjectUpdate(object sender, PrimEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.RadarPrimitives, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleKillObject(object sender, KillObjectEventArgs e)
        {
            Primitive primitive;
            lock (RadarObjectsLock)
            {
                if (!RadarObjects.TryGetValue(e.ObjectLocalID, out primitive))
                    return;
            }
            switch (primitive is Avatar)
            {
                case true:
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.RadarAvatars, e),
                        corradeConfiguration.MaximumNotificationThreads);
                    break;

                default:
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.RadarPrimitives, e),
                        corradeConfiguration.MaximumNotificationThreads);
                    break;
            }
        }

        private static void HandleGroupJoined(object sender, GroupOperationEventArgs e)
        {
            // Add the group to the cache.
            Cache.AddCurrentGroup(e.GroupID);

            // Join group chat if possible.
            if (!Client.Self.GroupChatSessions.ContainsKey(e.GroupID) &&
                Services.HasGroupPowers(Client, Client.Self.AgentID, e.GroupID, GroupPowers.JoinChat,
                    corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                    new DecayingAlarm(corradeConfiguration.DataDecayType)))
                Services.JoinGroupChat(Client, e.GroupID, corradeConfiguration.ServicesTimeout);
        }

        private static void HandleGroupLeave(object sender, GroupOperationEventArgs e)
        {
            // Remove the group from the cache.
            Cache.CurrentGroupsCache.Remove(e.GroupID);
        }

        private static void HandleLoadURL(object sender, LoadUrlEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.LoadURL, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleScriptControlChange(object sender, ScriptControlEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.ScriptControl, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleAppearanceSet(object sender, AppearanceSetEventArgs e)
        {
            switch (e.Success)
            {
                case true:
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.APPEARANCE_SET_SUCCEEDED));
                    break;

                default:
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.APPEARANCE_SET_FAILED));
                    break;
            }
        }

        private static void HandleRegionCrossed(object sender, RegionCrossedEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.RegionCrossed, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleMeanCollision(object sender, MeanCollisionEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.MeanCollision, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleViewerEffect(object sender, object e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.ViewerEffect, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        /// <summary>
        ///     Sends a notification to each group with a configured and installed notification.
        /// </summary>
        /// <param name="notification">the notification to send</param>
        /// <param name="args">the event arguments</param>
        private static void SendNotification(Configuration.Notifications notification, object args)
        {
            // Create a list of groups that have the notification installed.
            HashSet<Notifications> notifications;
            lock (GroupNotificationsLock)
            {
                if (!GroupNotificationsCache.TryGetValue(notification, out notifications) || !notifications.Any())
                    return;
            }

            // Find the notification action.
            var CorradeNotification = corradeNotifications[Reflection.GetNameFromEnumValue(notification)];
            if (CorradeNotification == null)
            {
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.NOTIFICATION_ERROR),
                    Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.UNKNOWN_NOTIFICATION_TYPE),
                    Reflection.GetNameFromEnumValue(notification));
                return;
            }

            // For each group build the notification.
            notifications.AsParallel().ForAll(z =>
            {
                // Create the notification data storage for this notification.
                var notificationData = new Dictionary<string, string>();

                try
                {
                    CorradeNotification.Invoke(new NotificationParameters
                    {
                        Notification = z,
                        Event = args,
                        Type = notification
                    }, notificationData);
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.NOTIFICATION_ERROR),
                        ex.PrettyPrint());
                    return;
                }

                // Do not send empty notifications.
                if (!notificationData.Any())
                    return;

                // Add the notification type.
                notificationData.Add(Reflection.GetNameFromEnumValue(ScriptKeys.TYPE),
                    Reflection.GetNameFromEnumValue(notification));

                // Build the afterburn.
                if (z.Afterburn != null && z.Afterburn.Any())
                    notificationData = notificationData
                        .Concat(z.Afterburn)
                        .AsParallel()
                        .GroupBy(o => o.Key)
                        .Select(o => o.FirstOrDefault())
                        .ToDictionary(o => o.Key, o => o.Value);

                // Enqueue the notification for the group.
                if (z.HTTPNotifications != null && z.HTTPNotifications.Any())
                {
                    SerializableDictionary<string, HashSet<string>> HTTPNotificationData;
                    if (z.HTTPNotifications.TryGetValue(notification, out HTTPNotificationData))
                        HTTPNotificationData.Keys.AsParallel().ForAll(p =>
                        {
                            var notificationQueueElement = new NotificationQueueElement
                            {
                                GroupUUID = z.GroupUUID,
                                URL = p,
                                Message = notificationData
                            };

                            lock (NucleusNotificationQueueLock)
                            {
                                switch (NucleusNotificationQueue.ContainsKey(z.GroupUUID))
                                {
                                    case true:
                                        while (NucleusNotificationQueue[z.GroupUUID].Count >
                                               corradeConfiguration.NucleusServerNotificationQueueLength)
                                            NucleusNotificationQueue[z.GroupUUID].Dequeue();
                                        NucleusNotificationQueue[z.GroupUUID].Enqueue(notificationQueueElement);
                                        break;

                                    default:
                                        NucleusNotificationQueue.Add(z.GroupUUID,
                                            new BlockingQueue<NotificationQueueElement>(
                                                new[] {notificationQueueElement}));
                                        break;
                                }
                            }

                            // Check that the notification queue is not already full.
                            switch (NotificationQueue.Count <= corradeConfiguration.NotificationQueueLength)
                            {
                                case true:
                                    NotificationQueue.Enqueue(notificationQueueElement);
                                    break;

                                default:
                                    Feedback(
                                        Reflection.GetDescriptionFromEnumValue(
                                            Enumerations.ConsoleMessage.NOTIFICATION_THROTTLED));
                                    break;
                            }
                        });
                }

                // Enqueue the TCP notification for the group.
                if (z.NotificationTCPDestination != null && z.NotificationTCPDestination.Any())
                {
                    HashSet<IPEndPoint> TCPdestinations;
                    if (z.NotificationTCPDestination.TryGetValue(notification, out TCPdestinations))
                        TCPdestinations.AsParallel().ForAll(p =>
                        {
                            switch (
                                NotificationTCPQueue.Count <= corradeConfiguration.TCPNotificationQueueLength)
                            {
                                case true:
                                    NotificationTCPQueue.Enqueue(new NotificationTCPQueueElement
                                    {
                                        Message = KeyValue.Escape(notificationData, wasOutput),
                                        IPEndPoint = p
                                    });
                                    break;

                                default:
                                    Feedback(
                                        Reflection.GetDescriptionFromEnumValue(
                                            Enumerations.ConsoleMessage.TCP_NOTIFICATION_THROTTLED));
                                    break;
                            }
                        });
                }
            });
        }

        private static void HandleScriptDialog(object sender, ScriptDialogEventArgs e)
        {
            var dialogUUID = UUID.Random();
            var scriptDialog = new ScriptDialog
            {
                Message = e.Message,
                Agent = new Agent
                {
                    FirstName = e.FirstName,
                    LastName = e.LastName,
                    UUID = e.OwnerID
                },
                Channel = e.Channel,
                Name = e.ObjectName,
                Item = e.ObjectID,
                Button = e.ButtonLabels,
                ID = dialogUUID
            };
            lock (ScriptDialogsLock)
            {
                ScriptDialogs.Add(dialogUUID, scriptDialog);
            }
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.ScriptDialog, scriptDialog),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleAvatarSitChanged(object sender, AvatarSitChangedEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.SitChanged, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleSoundTrigger(object sender, SoundTriggerEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Sound, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleAttachedSound(object sender, AttachedSoundEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Sound, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleAttachedSoundGain(object sender, AttachedSoundGainChangeEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Sound, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleAnimationsChanged(object sender, AnimationsChangedEventArgs e)
        {
            lock (CurrentAnimationsLock)
            {
                if (!e.Animations.Copy().Except(CurrentAnimations).Any())
                    return;
                CurrentAnimations.Clear();
                CurrentAnimations.UnionWith(e.Animations.Copy());
            }
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.AnimationsChanged, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleChatFromSimulator(object sender, ChatEventArgs e)
        {
            // Check if message is from muted agent or object and ignore it.
            if (Cache.MuteCache.Any(o => o.ID.Equals(e.SourceID) || o.ID.Equals(e.OwnerID)))
                return;
            // Get the full name.
            var fullName = new List<string>(wasOpenMetaverse.Helpers.GetAvatarNames(e.FromName));
            Configuration.Group commandGroup;
            switch (e.Type)
            {
                case ChatType.StartTyping:
                case ChatType.StopTyping:
                    // Check that we have a valid agent name.
                    if (!fullName.Any())
                        break;
                    Cache.AddAgent(fullName.First(), fullName.Last(), e.SourceID);
                    // Send typing notifications.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.Typing, new TypingEventArgs
                        {
                            Action = !e.Type.Equals(
                                ChatType.StartTyping)
                                ? Enumerations.Action.STOP
                                : Enumerations.Action.START,
                            AgentUUID = e.SourceID,
                            FirstName = fullName.First(),
                            LastName = fullName.Last(),
                            Entity = Enumerations.Entity.LOCAL
                        }),
                        corradeConfiguration.MaximumNotificationThreads);
                    break;

                case ChatType.OwnerSay:
                    // If this is a message from an agent, add the agent to the cache.
                    if (!fullName.Any() && e.SourceType.Equals(ChatSourceType.Agent))
                        Cache.AddAgent(fullName.First(), fullName.Last(), e.SourceID);
                    // If RLV is enabled, process RLV and terminate.
                    if (corradeConfiguration.EnableRLV &&
                        e.Message.StartsWith(wasOpenMetaverse.RLV.RLV_CONSTANTS.COMMAND_OPERATOR))
                    {
                        // Send RLV message notifications.
                        CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                            () => SendNotification(Configuration.Notifications.RLVMessage, e),
                            corradeConfiguration.MaximumNotificationThreads);
                        CorradeThreadPool[Threading.Enumerations.ThreadType.RLV].SpawnSequential(
                            () =>
                                RLV.HandleRLVBehaviour(e.Message.Substring(1, e.Message.Length - 1), e.SourceID),
                            corradeConfiguration.MaximumRLVThreads, corradeConfiguration.ServicesTimeout);
                        break;
                    }
                    // If this is a Corrade command, process it and terminate.
                    if (Helpers.Utilities.IsCorradeCommand(e.Message))
                    {
                        // If the group was not set properly, then bail.
                        commandGroup = GetCorradeGroupFromMessage(e.Message, corradeConfiguration);
                        if (commandGroup == null || commandGroup.Equals(default(Configuration.Group)))
                            return;
                        // Spawn the command.
                        CorradeThreadPool[Threading.Enumerations.ThreadType.COMMAND].Spawn(
                            () => HandleCorradeCommand(e.Message, e.FromName, e.OwnerID.ToString(), commandGroup),
                            corradeConfiguration.MaximumCommandThreads, commandGroup.UUID,
                            corradeConfiguration.SchedulerExpiration);
                        return;
                    }
                    // Otherwise, send llOwnerSay notifications.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.OwnerSay, e),
                        corradeConfiguration.MaximumNotificationThreads);

                    // Log ownersay messages.
                    if (corradeConfiguration.OwnerSayMessageLogEnabled)
                        CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(() =>
                        {
                            try
                            {
                                lock (OwnerSayLogFileLock)
                                {
                                    Directory.CreateDirectory(corradeConfiguration.OwnerSayMessageLogDirectory);

                                    var path =
                                        $"{Path.Combine(corradeConfiguration.OwnerSayMessageLogDirectory, e.SourceID.ToString())}.{CORRADE_CONSTANTS.LOG_FILE_EXTENSION}";
                                    using (
                                        var fileStream =
                                            new FileStream(path, FileMode.Append,
                                                FileAccess.Write, FileShare.None, 16384, true))
                                    {
                                        using (var logWriter = new StreamWriter(fileStream, Encoding.UTF8))
                                        {
                                            logWriter.WriteLine(CORRADE_CONSTANTS.LOCAL_MESSAGE_LOG_MESSAGE_FORMAT,
                                                DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                    Utils.EnUsCulture.DateTimeFormat),
                                                e.FromName,
                                                $"({e.SourceID})",
                                                Enum.GetName(typeof(ChatType), e.Type),
                                                e.Message);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // or fail and append the fail message.
                                Feedback(
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.COULD_NOT_WRITE_TO_OWNERSAY_MESSAGE_LOG_FILE),
                                    ex.PrettyPrint());
                            }
                        }, corradeConfiguration.MaximumLogThreads, corradeConfiguration.ServicesTimeout);
                    break;

                case ChatType.Debug:
                    // Send debug notifications.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.DebugMessage, e),
                        corradeConfiguration.MaximumNotificationThreads);
                    break;

                case ChatType.Normal:
                case ChatType.Shout:
                case ChatType.Whisper:
                    // If this is a message from an agent, add the agent to the cache.
                    if (!fullName.Any() && e.SourceType.Equals(ChatSourceType.Agent))
                        Cache.AddAgent(fullName.First(), fullName.Last(), e.SourceID);
                    // Send chat notifications.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.LocalChat, e),
                        corradeConfiguration.MaximumNotificationThreads);
                    // Log local chat if the message could be heard.
                    if (corradeConfiguration.LocalMessageLogEnabled)
                        CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(() =>
                        {
                            try
                            {
                                lock (LocalLogFileLock)
                                {
                                    Directory.CreateDirectory(corradeConfiguration.LocalMessageLogDirectory);

                                    var path =
                                        $"{Path.Combine(corradeConfiguration.LocalMessageLogDirectory, Client.Network.CurrentSim.Name)}.{CORRADE_CONSTANTS.LOG_FILE_EXTENSION}";
                                    using (
                                        var fileStream =
                                            new FileStream(path, FileMode.Append,
                                                FileAccess.Write, FileShare.None, 16384, true))
                                    {
                                        using (var logWriter = new StreamWriter(fileStream, Encoding.UTF8))
                                        {
                                            switch (fullName.Any())
                                            {
                                                case true:
                                                    logWriter.WriteLine(
                                                        CORRADE_CONSTANTS.LOCAL_MESSAGE_LOG_MESSAGE_FORMAT,
                                                        DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                            Utils.EnUsCulture.DateTimeFormat),
                                                        fullName.First(), fullName.Last(),
                                                        Enum.GetName(typeof(ChatType), e.Type),
                                                        e.Message);
                                                    break;

                                                default:
                                                    logWriter.WriteLine(
                                                        CORRADE_CONSTANTS.LOCAL_MESSAGE_LOG_MESSAGE_FORMAT,
                                                        DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                            Utils.EnUsCulture.DateTimeFormat),
                                                        e.FromName,
                                                        Enum.GetName(typeof(ChatType), e.Type),
                                                        e.Message);
                                                    break;
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // or fail and append the fail message.
                                Feedback(
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.COULD_NOT_WRITE_TO_LOCAL_MESSAGE_LOG_FILE),
                                    ex.PrettyPrint());
                            }
                        }, corradeConfiguration.MaximumLogThreads, corradeConfiguration.ServicesTimeout);
                    break;

                case (ChatType) 9:
                    // Send llRegionSayTo notification in case we do not have a command.
                    if (!Helpers.Utilities.IsCorradeCommand(e.Message))
                    {
                        // Send chat notifications.
                        CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                            () => SendNotification(Configuration.Notifications.RegionSayTo, e),
                            corradeConfiguration.MaximumNotificationThreads);
                        break;
                    }
                    // If the group was not set properly, then bail.
                    commandGroup = GetCorradeGroupFromMessage(e.Message, corradeConfiguration);
                    if (commandGroup == null || commandGroup.Equals(default(Configuration.Group)))
                        return;

                    // Spawn the command.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.COMMAND].Spawn(
                        () => HandleCorradeCommand(e.Message, e.FromName, e.OwnerID.ToString(), commandGroup),
                        corradeConfiguration.MaximumCommandThreads, commandGroup.UUID,
                        corradeConfiguration.SchedulerExpiration);
                    break;
            }
        }

        private static void HandleAlertMessage(object sender, AlertMessageEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.AlertMessage, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleInventoryObjectAdded(object sender, InventoryObjectAddedEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Store, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleInventoryObjectRemoved(object sender, InventoryObjectRemovedEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Store, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleInventoryObjectUpdated(object sender, InventoryObjectUpdatedEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Store, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleInventoryObjectOffered(object sender, InventoryObjectOfferedEventArgs e)
        {
            // Accept anything from master avatars.
            InventoryNode node;
            if (
                corradeConfiguration.Masters.AsParallel().Select(
                        o => string.Format(Utils.EnUsCulture, "{0} {1}", o.FirstName, o.LastName))
                    .Any(
                        p =>
                            string.Equals(e.Offer.FromAgentName, p,
                                StringComparison.OrdinalIgnoreCase)))
            {
                e.Accept = true;
                // It is accepted, so update the inventory.
                // Find the node.
                Locks.ClientInstanceInventoryLock.EnterReadLock();
                node = Client.Inventory.Store.GetNodeFor(e.FolderID.Equals(UUID.Zero)
                    ? Client.Inventory.FindFolderForType(e.AssetType)
                    : e.FolderID);
                Locks.ClientInstanceInventoryLock.ExitReadLock();
                if (node != null)
                {
                    // Set the node to be updated.
                    node.NeedsUpdate = true;
                    // Update the inventory.
                    try
                    {
                        switch (node.Data is InventoryFolder)
                        {
                            case true:
                                Inventory.UpdateInventoryRecursive(Client, Client.Inventory.Store.RootFolder,
                                    corradeConfiguration.ServicesTimeout);
                                break;

                            default:
                                Inventory.UpdateInventoryRecursive(Client,
                                    Client.Inventory.Store.Items[
                                            Client.Inventory.FindFolderForType(e.AssetType)]
                                        .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
                                break;
                        }
                    }
                    catch (Exception)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.ERROR_UPDATING_INVENTORY));
                    }
                }
                // Send notification
                CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                    () => SendNotification(Configuration.Notifications.Inventory, e),
                    corradeConfiguration.MaximumNotificationThreads);
                return;
            }

            // We need to block until we get a reply from a script.
            var inventoryOffer = new InventoryOffer
            {
                Args = e,
                Event = new ManualResetEventSlim(false)
            };

            // It is temporary, so update the inventory.
            Locks.ClientInstanceInventoryLock.EnterReadLock();
            Client.Inventory.Store.GetNodeFor(inventoryOffer.Args.FolderID.Equals(UUID.Zero)
                    ? Client.Inventory.FindFolderForType(inventoryOffer.Args.AssetType)
                    : inventoryOffer.Args.FolderID).NeedsUpdate =
                true;
            Locks.ClientInstanceInventoryLock.ExitReadLock();

            // Update the inventory.
            try
            {
                Inventory.UpdateInventoryRecursive(Client,
                    Client.Inventory.Store.Items[inventoryOffer.Args.FolderID]
                        .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
            }
            catch (Exception)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_UPDATING_INVENTORY));
            }

            // Find the item in the inventory.
            InventoryBase inventoryBaseItem = null;
            switch (e.Offer.Dialog)
            {
                case InstantMessageDialog.TaskInventoryOffered: // from objects
                    var groups = CORRADE_CONSTANTS.InventoryOfferObjectNameRegEx.Match(e.Offer.Message).Groups;
                    inventoryOffer.Name = groups.Count > 0 ? groups[1].Value : e.Offer.Message;
                    break;

                case InstantMessageDialog.InventoryOffered: // from agents
                    if (e.Offer.BinaryBucket.Length.Equals(17))
                    {
                        var itemUUID = new UUID(e.Offer.BinaryBucket, 1);
                        Locks.ClientInstanceInventoryLock.EnterReadLock();
                        if (Client.Inventory.Store.Contains(itemUUID))
                        {
                            inventoryBaseItem = Client.Inventory.Store[itemUUID];
                            // Set the name.
                            inventoryOffer.Name = inventoryBaseItem.Name;
                        }
                        Locks.ClientInstanceInventoryLock.ExitReadLock();
                    }
                    break;
            }

            if (inventoryBaseItem != null)
            {
                var parentUUID = inventoryBaseItem.ParentUUID;
                // Assume we do not want the item.
                Locks.ClientInstanceInventoryLock.EnterWriteLock();
                Client.Inventory.Move(
                    inventoryBaseItem,
                    Client.Inventory.Store.Items[Client.Inventory.FindFolderForType(AssetType.TrashFolder)].Data as
                        InventoryFolder);
                Client.Inventory.Store.GetNodeFor(parentUUID).NeedsUpdate = true;
                Client.Inventory.Store.GetNodeFor(Client.Inventory.FindFolderForType(AssetType.TrashFolder))
                    .NeedsUpdate = true;
                Locks.ClientInstanceInventoryLock.ExitWriteLock();

                // Update the inventory.
                try
                {
                    Inventory.UpdateInventoryRecursive(Client,
                        Client.Inventory.Store.Items[parentUUID]
                            .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
                    Inventory.UpdateInventoryRecursive(Client,
                        Client.Inventory.Store.Items[Client.Inventory.FindFolderForType(AssetType.TrashFolder)]
                            .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
                }
                catch (Exception)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.ERROR_UPDATING_INVENTORY));
                }
            }

            // Add the inventory offer to the list of inventory offers.
            lock (InventoryOffersLock)
            {
                InventoryOffers.Add(inventoryOffer.Args.Offer.IMSessionID, inventoryOffer);
            }

            // Send notification
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Inventory, inventoryOffer.Args),
                corradeConfiguration.MaximumNotificationThreads);

            // Wait for a reply.
            inventoryOffer.Event.Wait(Timeout.Infinite);

            // Remove the inventory offer.
            lock (InventoryOffersLock)
            {
                if (InventoryOffers.ContainsKey(inventoryOffer.Args.Offer.IMSessionID))
                    InventoryOffers.Remove(inventoryOffer.Args.Offer.IMSessionID);
            }

            // If item has been declined no inventory cleanup is required.
            if (inventoryBaseItem == null)
                return;

            var sourceParentUUID = UUID.Zero;
            var destinationParentUUID = UUID.Zero;
            switch (inventoryBaseItem.ParentUUID.Equals(UUID.Zero))
            {
                case true:
                    Locks.ClientInstanceInventoryLock.EnterReadLock();
                    var rootFolderUUID = Client.Inventory.Store.RootFolder.UUID;
                    var libraryFolderUUID = Client.Inventory.Store.LibraryFolder.UUID;
                    Locks.ClientInstanceInventoryLock.ExitReadLock();
                    if (inventoryBaseItem.UUID.Equals(rootFolderUUID))
                    {
                        sourceParentUUID = rootFolderUUID;
                        break;
                    }
                    if (inventoryBaseItem.UUID.Equals(libraryFolderUUID))
                        sourceParentUUID = libraryFolderUUID;
                    break;

                default:
                    sourceParentUUID = inventoryBaseItem.ParentUUID;
                    break;
            }

            switch (inventoryOffer.Args.Accept)
            {
                case false: // if the item is to be discarded, then remove the item from inventory
                    Locks.ClientInstanceInventoryLock.EnterWriteLock();
                    switch (inventoryBaseItem is InventoryFolder)
                    {
                        case true:
                            Client.Inventory.RemoveFolder(inventoryBaseItem.UUID);
                            break;

                        default:
                            Client.Inventory.RemoveItem(inventoryBaseItem.UUID);
                            break;
                    }
                    Client.Inventory.Store.GetNodeFor(sourceParentUUID).NeedsUpdate = true;
                    Client.Inventory.Store.GetNodeFor(Client.Inventory.FindFolderForType(AssetType.TrashFolder))
                        .NeedsUpdate = true;
                    Locks.ClientInstanceInventoryLock.ExitWriteLock();

                    // Update the inventory.
                    try
                    {
                        Inventory.UpdateInventoryRecursive(Client,
                            Client.Inventory.Store.Items[sourceParentUUID]
                                .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
                        Inventory.UpdateInventoryRecursive(Client,
                            Client.Inventory.Store.Items[Client.Inventory.FindFolderForType(AssetType.TrashFolder)]
                                .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
                    }
                    catch (Exception)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.ERROR_UPDATING_INVENTORY));
                    }
                    return;
            }

            // If no folder UUID was specified, move it to the default folder for the asset type.
            switch (!inventoryOffer.Args.FolderID.Equals(UUID.Zero))
            {
                case true: // a destination folder was specified
                    InventoryFolder inventoryFolder = null;
                    Locks.ClientInstanceInventoryLock.EnterReadLock();
                    node = Client.Inventory.Store.GetNodeFor(inventoryOffer.Args.FolderID);
                    if (node != null)
                        inventoryFolder = node.Data as InventoryFolder;
                    Locks.ClientInstanceInventoryLock.ExitReadLock();
                    if (inventoryFolder != null)
                    {
                        // grab the destination parent UUID for updates.
                        destinationParentUUID = inventoryFolder.ParentUUID;

                        Locks.ClientInstanceInventoryLock.EnterWriteLock();
                        switch (inventoryBaseItem is InventoryFolder)
                        {
                            case true: // folders
                                // if a name was specified, rename the item as well.

                                switch (string.IsNullOrEmpty(inventoryOffer.Name))
                                {
                                    case false:
                                        Client.Inventory.MoveFolder(inventoryBaseItem.UUID,
                                            inventoryFolder.UUID, inventoryOffer.Name);
                                        break;

                                    default:
                                        Client.Inventory.MoveFolder(inventoryBaseItem.UUID,
                                            inventoryFolder.UUID);
                                        break;
                                }
                                break;

                            default: // all other items
                                switch (string.IsNullOrEmpty(inventoryOffer.Name))
                                {
                                    case false:
                                        Client.Inventory.Move(inventoryBaseItem, inventoryFolder,
                                            inventoryOffer.Name);
                                        Client.Inventory.RequestUpdateItem(inventoryBaseItem as InventoryItem);
                                        break;

                                    default:
                                        Client.Inventory.Move(inventoryBaseItem, inventoryFolder);
                                        break;
                                }
                                break;
                        }
                        Client.Inventory.Store.GetNodeFor(sourceParentUUID).NeedsUpdate = true;
                        Client.Inventory.Store.GetNodeFor(inventoryFolder.UUID).NeedsUpdate = true;
                        Locks.ClientInstanceInventoryLock.ExitWriteLock();
                    }
                    break;

                default: // no destination folder was specified
                    switch (inventoryBaseItem is InventoryFolder)
                    {
                        case true: // move inventory folders into the root
                            Locks.ClientInstanceInventoryLock.EnterWriteLock();
                            destinationParentUUID = Client.Inventory.Store.RootFolder.UUID;
                            // if a name was specified, rename the item as well.
                            switch (string.IsNullOrEmpty(inventoryOffer.Name))
                            {
                                case false:
                                    Client.Inventory.MoveFolder(
                                        inventoryBaseItem.UUID, Client.Inventory.Store.RootFolder.UUID,
                                        inventoryOffer.Name);
                                    break;

                                default:
                                    Client.Inventory.MoveFolder(
                                        inventoryBaseItem.UUID, Client.Inventory.Store.RootFolder.UUID);
                                    break;
                            }
                            Client.Inventory.Store.GetNodeFor(sourceParentUUID).NeedsUpdate = true;
                            Client.Inventory.Store.GetNodeFor(Client.Inventory.Store.RootFolder.UUID).NeedsUpdate =
                                true;
                            Locks.ClientInstanceInventoryLock.ExitWriteLock();
                            break;

                        default: // move items to their respective asset folder type
                            InventoryFolder destinationFolder = null;
                            Locks.ClientInstanceInventoryLock.EnterReadLock();
                            node =
                                Client.Inventory.Store.GetNodeFor(
                                    Client.Inventory.FindFolderForType(inventoryOffer.Args.AssetType));
                            Locks.ClientInstanceInventoryLock.ExitReadLock();
                            if (node != null)
                                destinationFolder = node.Data as InventoryFolder;
                            if (destinationFolder != null)
                            {
                                destinationParentUUID = destinationFolder.ParentUUID;
                                Locks.ClientInstanceInventoryLock.EnterWriteLock();
                                switch (string.IsNullOrEmpty(inventoryOffer.Name))
                                {
                                    case false:
                                        Client.Inventory.Move(inventoryBaseItem, destinationFolder,
                                            inventoryOffer.Name);
                                        Client.Inventory.RequestUpdateItem(inventoryBaseItem as InventoryItem);
                                        break;

                                    default:
                                        Client.Inventory.Move(inventoryBaseItem, destinationFolder);
                                        break;
                                }
                                Client.Inventory.Store.GetNodeFor(sourceParentUUID).NeedsUpdate = true;
                                Client.Inventory.Store.GetNodeFor(destinationFolder.UUID).NeedsUpdate = true;
                                Locks.ClientInstanceInventoryLock.ExitWriteLock();
                            }
                            break;
                    }
                    break;
            }

            // Update the source parent.
            try
            {
                if (!sourceParentUUID.Equals(UUID.Zero))
                    Inventory.UpdateInventoryRecursive(Client,
                        Client.Inventory.Store.Items[sourceParentUUID]
                            .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
            }
            catch (Exception)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_UPDATING_INVENTORY));
            }

            // Update the destination parent.
            try
            {
                if (!destinationParentUUID.Equals(UUID.Zero))
                    Inventory.UpdateInventoryRecursive(Client,
                        Client.Inventory.Store.Items[destinationParentUUID]
                            .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
            }
            catch (Exception)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_UPDATING_INVENTORY));
            }

            // Update the trash folder.
            try
            {
                Inventory.UpdateInventoryRecursive(Client,
                    Client.Inventory.Store.Items[Client.Inventory.FindFolderForType(AssetType.TrashFolder)]
                        .Data as InventoryFolder, corradeConfiguration.ServicesTimeout);
            }
            catch (Exception)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_UPDATING_INVENTORY));
            }
        }

        private static void HandleScriptQuestion(object sender, ScriptQuestionEventArgs e)
        {
            // Get the full name of the avatar sending a script permission request.
            var fullName = new List<string>(wasOpenMetaverse.Helpers.GetAvatarNames(e.ObjectOwnerName));
            var ownerUUID = UUID.Zero;
            // Don't add permission requests from unknown agents.
            if (!fullName.Any() ||
                !Resolvers.AgentNameToUUID(Client, fullName.First(), fullName.Last(),
                    corradeConfiguration.ServicesTimeout,
                    corradeConfiguration.DataTimeout,
                    new DecayingAlarm(corradeConfiguration.DataDecayType),
                    ref ownerUUID))
                return;

            // Handle RLV: acceptpermission / declinepermission
            if (corradeConfiguration.EnableRLV)
                switch (e.Questions)
                {
                    case ScriptPermission.Attach:
                    case ScriptPermission.TakeControls:
                        var succeeded = false;
                        Parallel.ForEach(RLVRules, (o, s) =>
                        {
                            switch (Reflection.GetEnumValueFromName<RLV.RLVBehaviour>(o.Behaviour))
                            {
                                case RLV.RLVBehaviour.ACCEPTPERMISSION:
                                    Locks.ClientInstanceSelfLock.EnterWriteLock();
                                    Client.Self.ScriptQuestionReply(e.Simulator, e.ItemID, e.TaskID, e.Questions);
                                    Locks.ClientInstanceSelfLock.ExitWriteLock();
                                    succeeded = true;
                                    s.Break();
                                    break;
                                case RLV.RLVBehaviour.DECLINEPERMISSION:
                                    Locks.ClientInstanceSelfLock.EnterWriteLock();
                                    Client.Self.ScriptQuestionReply(e.Simulator, e.ItemID, e.TaskID,
                                        ScriptPermission.None);
                                    Locks.ClientInstanceSelfLock.ExitWriteLock();
                                    succeeded = true;
                                    s.Break();
                                    break;
                            }
                        });

                        // RLV takes preceence.
                        if (succeeded)
                            return;
                        break;
                }

            lock (ScriptPermissionsRequestsLock)
            {
                ScriptPermissionRequests.Add(new ScriptPermissionRequest
                {
                    Name = e.ObjectName,
                    Agent = new Agent
                    {
                        FirstName = fullName.First(),
                        LastName = fullName.Last(),
                        UUID = ownerUUID
                    },
                    Item = e.ItemID,
                    Task = e.TaskID,
                    Permissions = e.Questions,
                    Region = e.Simulator.Name
                });
            }
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.ScriptPermission, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleDisconnected(object sender, DisconnectedEventArgs e)
        {
            Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.DISCONNECTED), e.Message);
            ConnectionSemaphores['l'].Set();
        }

        private static void HandleEventQueueRunning(object sender, EventQueueRunningEventArgs e)
        {
            Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.EVENT_QUEUE_STARTED),
                e.Simulator.Name);

            // Set language.
            Locks.ClientInstanceSelfLock.EnterWriteLock();
            Client.Self.UpdateAgentLanguage(corradeConfiguration.ClientLanguage,
                corradeConfiguration.AdvertiseClientLanguage);
            Locks.ClientInstanceSelfLock.ExitWriteLock();
        }

        private static void HandleSimulatorConnected(object sender, SimConnectedEventArgs e)
        {
            Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.SIMULATOR_CONNECTED),
                e.Simulator.Name);
        }

        private static void HandleSimulatorDisconnected(object sender, SimDisconnectedEventArgs e)
        {
            Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.SIMULATOR_DISCONNECTED),
                e.Simulator.Name, e.Reason.ToString());

            // if any simulators are still connected, we are not disconnected
            Locks.ClientInstanceNetworkLock.EnterReadLock();
            if (Client.Network.Simulators.Any())
            {
                Locks.ClientInstanceNetworkLock.ExitReadLock();
                return;
            }
            Locks.ClientInstanceNetworkLock.ExitReadLock();

            // Announce that we lost all connections to simulators.
            Feedback(
                Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.ALL_SIMULATORS_DISCONNECTED));

            // Set the semaphore.
            ConnectionSemaphores['s'].Set();
        }

        private static void HandleLoggedOut(object sender, LoggedOutEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Login, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleLoginProgress(object sender, LoginProgressEventArgs e)
        {
            // Send the notification.
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Login, e),
                corradeConfiguration.MaximumNotificationThreads);

            switch (e.Status)
            {
                case LoginStatus.Success:
                    // Login succeeded so start all the updates.
                    Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.LOGIN_SUCCEEDED));

                    // Load movement state.
                    LoadMovementState.Invoke();

                    // Start thread and wait on caps to restore conferences.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                        () =>
                        {
                            // Wait for CAPs.
                            if (!Client.Network.CurrentSim.Caps.IsEventQueueRunning)
                            {
                                var EventQueueRunningEvent = new AutoResetEvent(false);
                                EventHandler<EventQueueRunningEventArgs> handler =
                                    (o, p) => { EventQueueRunningEvent.Set(); };
                                Client.Network.EventQueueRunning += handler;
                                EventQueueRunningEvent.WaitOne((int) corradeConfiguration.ServicesTimeout, true);
                                Client.Network.EventQueueRunning -= handler;
                            }

                            // Load conference state.
                            LoadConferenceState.Invoke();
                        });

                    // Start inventory update thread.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                        () =>
                        {
                            // First load the caches.
                            LoadInventoryCache.Invoke();
                            // Update the inventory.
                            try
                            {
                                // Update the inventory.
                                Inventory.UpdateInventoryRecursive(Client, Client.Inventory.Store.RootFolder,
                                    corradeConfiguration.ServicesTimeout);

                                // Update the library.
                                Inventory.UpdateInventoryRecursive(Client, Client.Inventory.Store.LibraryFolder,
                                    corradeConfiguration.ServicesTimeout);

                                // Get COF.
                                Locks.ClientInstanceInventoryLock.EnterReadLock();
                                CurrentOutfitFolder =
                                    Client.Inventory.Store[
                                            Client.Inventory.FindFolderForType(AssetType.CurrentOutfitFolder)
                                        ] as
                                        InventoryFolder;
                                Locks.ClientInstanceInventoryLock.ExitReadLock();
                            }
                            catch (Exception ex)
                            {
                                Feedback(
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.ERROR_UPDATING_INVENTORY), ex.PrettyPrint());
                            }

                            // Now save the caches.
                            SaveInventoryCache.Invoke();

                            // Bind to the inventory store notifications if enabled.
                            if (Client.Inventory.Store != null)
                                switch (
                                    corradeConfiguration.Groups.AsParallel()
                                        .Any(
                                            o => o.NotificationMask.IsMaskFlagSet(Configuration.Notifications.Store))
                                )
                                {
                                    case true:
                                        Client.Inventory.Store.InventoryObjectAdded += HandleInventoryObjectAdded;
                                        Client.Inventory.Store.InventoryObjectRemoved += HandleInventoryObjectRemoved;
                                        Client.Inventory.Store.InventoryObjectUpdated += HandleInventoryObjectUpdated;
                                        break;

                                    default:
                                        Client.Inventory.Store.InventoryObjectAdded -= HandleInventoryObjectAdded;
                                        Client.Inventory.Store.InventoryObjectRemoved -= HandleInventoryObjectRemoved;
                                        Client.Inventory.Store.InventoryObjectUpdated -= HandleInventoryObjectUpdated;
                                        break;
                                }
                        });

                    // Request the mute list.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                        () =>
                        {
                            var mutes = Enumerable.Empty<MuteEntry>();
                            if (!Services.GetMutes(Client, corradeConfiguration.ServicesTimeout, ref mutes))
                                return;
                            Cache.MuteCache.UnionWith(mutes.OfType<Cache.MuteEntry>());
                        });

                    // Set current group to land group.
                    if (corradeConfiguration.AutoActivateGroup)
                        ActivateCurrentLandGroupTimer.Change(corradeConfiguration.AutoActivateGroupDelay, 0);

                    // Apply settings.
                    Client.Self.SetHeightWidth(ushort.MaxValue, ushort.MaxValue);
                    Client.Self.Movement.Camera.Far = corradeConfiguration.Range;

                    // Set the camera on the avatar.
                    Client.Self.Movement.Camera.LookAt(
                        Client.Self.SimPosition,
                        Client.Self.SimPosition
                    );

                    // Retrieve instant messages.
                    Locks.ClientInstanceSelfLock.EnterReadLock();
                    Client.Self.RetrieveInstantMessages();
                    Locks.ClientInstanceSelfLock.ExitReadLock();
                    break;

                case LoginStatus.Failed:
                    Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.LOGIN_FAILED),
                        e.FailReason,
                        e.Message);
                    ConnectionSemaphores['l'].Set();
                    break;

                case LoginStatus.ConnectingToLogin:
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.CONNECTING_TO_LOGIN_SERVER));
                    break;

                case LoginStatus.Redirecting:
                    Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.REDIRECTING));
                    break;

                case LoginStatus.ReadingResponse:
                    Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.READING_RESPONSE));
                    break;

                case LoginStatus.ConnectingToSim:
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.CONNECTING_TO_SIMULATOR));
                    break;
            }
        }

        private static void HandleFriendOnlineStatus(object sender, FriendInfoEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Friendship, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleFriendRightsUpdate(object sender, FriendInfoEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Friendship, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleFriendShipResponse(object sender, FriendshipResponseEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Friendship, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleFriendshipOffered(object sender, FriendshipOfferedEventArgs e)
        {
            // Send friendship notifications
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Friendship, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleTeleportProgress(object sender, TeleportEventArgs e)
        {
            // Send teleport notifications
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Teleport, e),
                corradeConfiguration.MaximumNotificationThreads);

            switch (e.Status)
            {
                case TeleportStatus.Finished:
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.TELEPORT_SUCCEEDED));
                    // Set current group to land group.
                    if (corradeConfiguration.AutoActivateGroup)
                        ActivateCurrentLandGroupTimer.Change(corradeConfiguration.AutoActivateGroupDelay, 0);
                    break;

                case TeleportStatus.Failed:
                    Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.TELEPORT_FAILED));
                    break;
            }
        }

        private static void HandleSelfIM(object sender, InstantMessageEventArgs args)
        {
            // Check if message is from muted agent and ignore it.
            if (Cache.MuteCache.Any(o => o.ID.Equals(args.IM.FromAgentID)))
                return;
            var fullName =
                new List<string>(wasOpenMetaverse.Helpers.GetAvatarNames(args.IM.FromAgentName));
            // Process dialog messages.
            switch (args.IM.Dialog)
            {
                // Send typing notification.
                case InstantMessageDialog.StartTyping:
                case InstantMessageDialog.StopTyping:
                    // Do not process invalid avatars.
                    if (!fullName.Any())
                        return;
                    // Add the agent to the cache.
                    Cache.AddAgent(fullName.First(), fullName.Last(), args.IM.FromAgentID);
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.Typing, new TypingEventArgs
                        {
                            Action = !args.IM.Dialog.Equals(
                                InstantMessageDialog.StartTyping)
                                ? Enumerations.Action.STOP
                                : Enumerations.Action.START,
                            AgentUUID = args.IM.FromAgentID,
                            FirstName = fullName.First(),
                            LastName = fullName.Last(),
                            Entity = Enumerations.Entity.MESSAGE
                        }),
                        corradeConfiguration.MaximumNotificationThreads);
                    return;

                case InstantMessageDialog.FriendshipOffered:
                    // Do not process invalid avatars.
                    if (!fullName.Any())
                        return;
                    // Add the agent to the cache.
                    Cache.AddAgent(fullName.First(), fullName.Last(), args.IM.FromAgentID);
                    // Accept friendships only from masters (for the time being)
                    if (
                        !corradeConfiguration.Masters.AsParallel().Any(
                            o =>
                                string.Equals(fullName.First(), o.FirstName, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(fullName.Last(), o.LastName, StringComparison.OrdinalIgnoreCase)))
                        return;
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.ACCEPTED_FRIENDSHIP),
                        args.IM.FromAgentName);
                    Client.Friends.AcceptFriendship(args.IM.FromAgentID, args.IM.IMSessionID);
                    break;

                case InstantMessageDialog.TaskInventoryAccepted:
                case InstantMessageDialog.TaskInventoryDeclined:
                case InstantMessageDialog.InventoryAccepted:
                case InstantMessageDialog.InventoryDeclined:
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.Inventory, args),
                        corradeConfiguration.MaximumNotificationThreads);
                    return;

                case InstantMessageDialog.MessageBox:
                    // Not used.
                    return;

                case InstantMessageDialog.RequestTeleport:
                    // Do not process invalid avatars.
                    if (!fullName.Any())
                        return;
                    // Add the agent to the cache.
                    Cache.AddAgent(fullName.First(), fullName.Last(), args.IM.FromAgentID);
                    // Handle RLV: acccepttp
                    if (corradeConfiguration.EnableRLV)
                    {
                        var succeeded = false;
                        Parallel.ForEach(RLVRules, (o, s) =>
                        {
                            if (!o.Behaviour.Equals(Reflection.GetNameFromEnumValue(RLV.RLVBehaviour.ACCEPTTP)))
                                return;

                            UUID agentUUID;
                            if (!string.IsNullOrEmpty(o.Option) &&
                                (!UUID.TryParse(o.Option, out agentUUID) || !args.IM.FromAgentID.Equals(agentUUID)))
                                return;

                            if (wasOpenMetaverse.Helpers.IsSecondLife(Client) && !TimedTeleportThrottle.IsSafe)
                            {
                                // or fail and append the fail message.
                                Feedback(
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.TELEPORT_THROTTLED));
                                return;
                            }

                            Locks.ClientInstanceSelfLock.EnterWriteLock();
                            Client.Self.TeleportLureRespond(args.IM.FromAgentID, args.IM.IMSessionID, true);
                            Locks.ClientInstanceSelfLock.ExitWriteLock();
                            succeeded = true;
                            s.Break();
                        });

                        if (succeeded)
                            return;
                    }

                    // Store teleport lure.
                    lock (TeleportLuresLock)
                    {
                        TeleportLures.Add(args.IM.IMSessionID, new TeleportLure
                        {
                            Agent = new Agent
                            {
                                FirstName = fullName.First(),
                                LastName = fullName.Last(),
                                UUID = args.IM.FromAgentID
                            },
                            Session = args.IM.IMSessionID
                        });
                    }
                    // Send teleport lure notification.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.TeleportLure, args),
                        corradeConfiguration.MaximumNotificationThreads);
                    // If we got a teleport request from a master, then accept it (for the moment).
                    Locks.ClientInstanceConfigurationLock.EnterReadLock();
                    if (
                        !corradeConfiguration.Masters.AsParallel()
                            .Any(
                                o =>
                                    string.Equals(fullName.First(), o.FirstName,
                                        StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(fullName.Last(), o.LastName,
                                        StringComparison.OrdinalIgnoreCase)))
                        return;
                    Locks.ClientInstanceConfigurationLock.ExitReadLock();
                    if (wasOpenMetaverse.Helpers.IsSecondLife(Client) && !TimedTeleportThrottle.IsSafe)
                    {
                        // or fail and append the fail message.
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.TELEPORT_THROTTLED));
                        return;
                    }
                    Locks.ClientInstanceSelfLock.EnterWriteLock();
                    if (Client.Self.Movement.SitOnGround || !Client.Self.SittingOn.Equals(0))
                        Client.Self.Stand();
                    // stop all non-built-in animations
                    Client.Self.SignaledAnimations.Copy()
                        .Keys.AsParallel()
                        .Where(o => !wasOpenMetaverse.Helpers.LindenAnimations.Contains(o))
                        .ForAll(o => { Client.Self.AnimationStop(o, true); });
                    Client.Self.TeleportLureRespond(args.IM.FromAgentID, args.IM.IMSessionID, true);
                    Locks.ClientInstanceSelfLock.ExitWriteLock();
                    return;
                // Group invitations received
                case InstantMessageDialog.GroupInvitation:
                    var inviteGroup = new Group();
                    if (
                        !Services.RequestGroup(Client, args.IM.FromAgentID, corradeConfiguration.ServicesTimeout,
                            ref inviteGroup))
                        return;
                    // Add the group to the cache.
                    Cache.AddGroup(inviteGroup.Name, inviteGroup.ID);
                    var inviteGroupAgent = UUID.Zero;
                    if (!fullName.Any() ||
                        !Resolvers.AgentNameToUUID(Client, fullName.First(), fullName.Last(),
                            corradeConfiguration.ServicesTimeout,
                            corradeConfiguration.DataTimeout,
                            new DecayingAlarm(corradeConfiguration.DataDecayType),
                            ref inviteGroupAgent))
                        return;
                    // Add the group invite - have to track them manually.
                    lock (GroupInvitesLock)
                    {
                        GroupInvites.Add(args.IM.IMSessionID, new GroupInvite
                        {
                            Agent = new Agent
                            {
                                FirstName = fullName.First(),
                                LastName = fullName.Last(),
                                UUID = inviteGroupAgent
                            },
                            Group = inviteGroup.Name,
                            ID = inviteGroup.ID,
                            Session = args.IM.IMSessionID,
                            Fee = inviteGroup.MembershipFee
                        });
                    }
                    // Send group invitation notification.
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.GroupInvite, args),
                        corradeConfiguration.MaximumNotificationThreads);
                    // If a master sends it, then accept.
                    Locks.ClientInstanceConfigurationLock.EnterReadLock();
                    if (
                        !corradeConfiguration.Masters.AsParallel()
                            .Any(
                                o =>
                                    string.Equals(fullName.First(), o.FirstName,
                                        StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(fullName.Last(), o.LastName,
                                        StringComparison.OrdinalIgnoreCase)))
                        return;
                    Locks.ClientInstanceConfigurationLock.ExitReadLock();
                    Locks.ClientInstanceSelfLock.EnterWriteLock();
                    Client.Self.GroupInviteRespond(inviteGroup.ID, args.IM.IMSessionID, true);
                    Locks.ClientInstanceSelfLock.ExitWriteLock();
                    return;
                // Notice received.
                case InstantMessageDialog.GroupNotice:
                    var noticeGroup = new Group();
                    if (
                        !Services.RequestGroup(Client,
                            args.IM.BinaryBucket.Length >= 18 ? new UUID(args.IM.BinaryBucket, 2) : args.IM.FromAgentID,
                            corradeConfiguration.ServicesTimeout, ref noticeGroup))
                        return;
                    // Add the group to the cache.
                    Cache.AddGroup(noticeGroup.Name, noticeGroup.ID);
                    var noticeGroupAgent = UUID.Zero;
                    if (
                        !Resolvers.AgentNameToUUID(Client, fullName.First(), fullName.Last(),
                            corradeConfiguration.ServicesTimeout,
                            corradeConfiguration.DataTimeout,
                            new DecayingAlarm(corradeConfiguration.DataDecayType),
                            ref noticeGroupAgent))
                        return;
                    // message contains an attachment
                    bool noticeAttachment;
                    var noticeAssetType = AssetType.Unknown;
                    var noticeFolder = UUID.Zero;
                    switch (args.IM.BinaryBucket.Length > 18 && !args.IM.BinaryBucket[0].Equals(0))
                    {
                        case true:
                            noticeAssetType = (AssetType) args.IM.BinaryBucket[1];
                            noticeFolder = Client.Inventory.FindFolderForType(noticeAssetType);
                            noticeAttachment = true;
                            break;

                        default:
                            noticeAttachment = false;
                            break;
                    }
                    // get the subject and the message
                    var noticeSubject = string.Empty;
                    var noticeMessage = string.Empty;
                    var noticeData = args.IM.Message.Split('|');
                    if (noticeData.Length > 0 && !string.IsNullOrEmpty(noticeData[0]))
                        noticeSubject = noticeData[0];
                    if (noticeData.Length > 1 && !string.IsNullOrEmpty(noticeData[1]))
                        noticeMessage = noticeData[1];
                    lock (GroupNoticeLock)
                    {
                        GroupNotices.Add(new GroupNotice
                        {
                            Agent = new Agent
                            {
                                FirstName = fullName.First(),
                                LastName = fullName.Last(),
                                UUID = noticeGroupAgent
                            },
                            Asset = noticeAssetType,
                            Attachment = noticeAttachment,
                            Folder = noticeFolder,
                            Group = noticeGroup,
                            Message = noticeMessage,
                            Subject = noticeSubject,
                            Session = args.IM.IMSessionID
                        });
                    }
                    CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                        () => SendNotification(Configuration.Notifications.GroupNotice, args),
                        corradeConfiguration.MaximumNotificationThreads);
                    return;

                case InstantMessageDialog.SessionSend:
                case InstantMessageDialog.MessageFromAgent:
                    // Check if this is a group message.
                    // Note that this is a lousy way of doing it but libomv does not properly set the GroupIM field
                    // such that the only way to determine if we have a group message is to check that the UUID
                    // of the session is actually the UUID of a current group. Furthermore, what's worse is that
                    // group mesages can appear both through SessionSend and from MessageFromAgent. Hence the problem.
                    var currentGroups = Enumerable.Empty<UUID>();
                    if (
                        !Services.GetCurrentGroups(Client, corradeConfiguration.ServicesTimeout,
                            ref currentGroups))
                        return;
                    var messageGroups = new HashSet<UUID>(currentGroups);

                    // Check if this is a group message.
                    switch (messageGroups.Contains(args.IM.IMSessionID))
                    {
                        case true:
                            var messageGroup =
                                corradeConfiguration.Groups.AsParallel()
                                    .FirstOrDefault(p => p.UUID.Equals(args.IM.IMSessionID));
                            if (messageGroup != null && !messageGroup.Equals(default(Configuration.Group)))
                            {
                                // Add the group to the cache.
                                Cache.AddGroup(messageGroup.Name, messageGroup.UUID);
                                // Add the agent to the cache.
                                Cache.AddAgent(fullName.First(), fullName.Last(), args.IM.FromAgentID);
                                // Send group notice notifications.
                                CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                                    () =>
                                        SendNotification(Configuration.Notifications.GroupMessage,
                                            new GroupMessageEventArgs
                                            {
                                                AgentUUID = args.IM.FromAgentID,
                                                FirstName = fullName.First(),
                                                LastName = fullName.Last(),
                                                GroupName = messageGroup.Name,
                                                GroupUUID = messageGroup.UUID,
                                                Message = args.IM.Message
                                            }),
                                    corradeConfiguration.MaximumNotificationThreads);
                                // Log group messages
                                corradeConfiguration.Groups.AsParallel().Where(
                                    o =>
                                        messageGroup.UUID.Equals(o.UUID) &&
                                        o.ChatLogEnabled).ForAll(o =>
                                {
                                    // Attempt to write to log file,
                                    CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(
                                        () =>
                                        {
                                            try
                                            {
                                                // Create path to group chat log.
                                                FileInfo fileInfo = new FileInfo(o.ChatLog);

                                                if (!fileInfo.Exists)
                                                    Directory.CreateDirectory(fileInfo.Directory.FullName);

                                                lock (GroupLogFileLock)
                                                {
                                                    using (
                                                        var fileStream = new FileStream(o.ChatLog,
                                                            FileMode.Append,
                                                            FileAccess.Write, FileShare.None, 16384, true))
                                                    {
                                                        using (
                                                            var logWriter = new StreamWriter(fileStream,
                                                                Encoding.UTF8))
                                                        {
                                                            logWriter.WriteLine(
                                                                CORRADE_CONSTANTS
                                                                    .GROUP_MESSAGE_LOG_MESSAGE_FORMAT,
                                                                DateTime.Now.ToString(
                                                                    CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                                    Utils.EnUsCulture.DateTimeFormat),
                                                                fullName.First(),
                                                                fullName.Last(),
                                                                args.IM.Message);
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                // or fail and append the fail message.
                                                Feedback(
                                                    Reflection.GetDescriptionFromEnumValue(
                                                        Enumerations.ConsoleMessage
                                                            .COULD_NOT_WRITE_TO_GROUP_CHAT_LOG_FILE),
                                                    ex.PrettyPrint());
                                            }
                                        }, corradeConfiguration.MaximumLogThreads,
                                        corradeConfiguration.ServicesTimeout);
                                });
                            }
                            return;
                    }
                    // Check if this is a conference message.
                    switch (
                        args.IM.Dialog == InstantMessageDialog.SessionSend &&
                        !messageGroups.Contains(args.IM.IMSessionID) ||
                        args.IM.Dialog == InstantMessageDialog.MessageFromAgent && args.IM.BinaryBucket.Length > 1)
                    {
                        case true:
                            // Join the chat if not yet joined
                            Locks.ClientInstanceSelfLock.EnterWriteLock();
                            if (!Client.Self.GroupChatSessions.ContainsKey(args.IM.IMSessionID))
                                Client.Self.ChatterBoxAcceptInvite(args.IM.IMSessionID);
                            Locks.ClientInstanceSelfLock.ExitWriteLock();
                            var conferenceName = Utils.BytesToString(args.IM.BinaryBucket);
                            // Add the conference to the list of conferences.
                            lock (ConferencesLock)
                            {
                                if (!Conferences.AsParallel()
                                    .Any(
                                        o =>
                                            o.Name.Equals(conferenceName, StringComparison.Ordinal) &&
                                            o.Session.Equals(args.IM.IMSessionID)))
                                    Conferences.Add(new Conference
                                    {
                                        Name = conferenceName,
                                        Session = args.IM.IMSessionID,
                                        Restored = false
                                    });
                            }
                            // Save the conference state.
                            SaveConferenceState.Invoke();
                            // Add the agent to the cache.
                            Cache.AddAgent(fullName.First(), fullName.Last(), args.IM.FromAgentID);
                            // Send conference message notification.
                            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                                () => SendNotification(Configuration.Notifications.Conference, args),
                                corradeConfiguration.MaximumNotificationThreads);
                            // Log conference messages,
                            if (corradeConfiguration.ConferenceMessageLogEnabled)
                                CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(() =>
                                {
                                    try
                                    {
                                        lock (ConferenceMessageLogFileLock)
                                        {
                                            Directory.CreateDirectory(
                                                corradeConfiguration.ConferenceMessageLogDirectory);

                                            var path =
                                                $"{Path.Combine(corradeConfiguration.ConferenceMessageLogDirectory, conferenceName)}.{CORRADE_CONSTANTS.LOG_FILE_EXTENSION}";
                                            using (
                                                var fileStream =
                                                    new FileStream(path, FileMode.Append,
                                                        FileAccess.Write, FileShare.None, 16384, true))
                                            {
                                                using (
                                                    var logWriter = new StreamWriter(fileStream,
                                                        Encoding.UTF8))
                                                {
                                                    logWriter.WriteLine(
                                                        CORRADE_CONSTANTS.CONFERENCE_MESSAGE_LOG_MESSAGE_FORMAT,
                                                        DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                            Utils.EnUsCulture.DateTimeFormat),
                                                        fullName.First(),
                                                        fullName.Last(),
                                                        args.IM.Message);
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // or fail and append the fail message.
                                        Feedback(
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ConsoleMessage
                                                    .COULD_NOT_WRITE_TO_CONFERENCE_MESSAGE_LOG_FILE),
                                            ex.PrettyPrint());
                                    }
                                }, corradeConfiguration.MaximumLogThreads, corradeConfiguration.ServicesTimeout);
                            return;
                    }
                    // Check if this is an instant message.
                    switch (!args.IM.ToAgentID.Equals(Client.Self.AgentID))
                    {
                        case false:
                            // Add the agent to the cache.
                            Cache.AddAgent(fullName.First(), fullName.Last(), args.IM.FromAgentID);
                            // Handle RLV: getblacklist
                            if (corradeConfiguration.EnableRLV &&
                                string.Equals(args.IM.Message, $"@{RLVBehaviours.getblacklist}"))
                            {
                                var succeeded = false;
                                Parallel.ForEach(RLVRules, (o, s) =>
                                {
                                    if (!o.Behaviour.Equals(
                                        Reflection.GetNameFromEnumValue(RLV.RLVBehaviour.GETBLACKLIST)))
                                        return;

                                    Locks.ClientInstanceSelfLock.EnterWriteLock();
                                    Client.Self.InstantMessage(args.IM.FromAgentID,
                                        string.Join(@",", corradeConfiguration.RLVBlacklist));
                                    Locks.ClientInstanceSelfLock.ExitWriteLock();
                                    succeeded = true;
                                    s.Break();
                                });

                                if (succeeded)
                                    return;
                            }
                            // Send instant message notification.
                            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                                () => SendNotification(Configuration.Notifications.InstantMessage, args),
                                corradeConfiguration.MaximumNotificationThreads);
                            // Check if we were ejected.
                            var groupUUID = UUID.Zero;
                            if (
                                Resolvers.GroupNameToUUID(
                                    Client,
                                    CORRADE_CONSTANTS.EjectedFromGroupRegEx.Match(args.IM.Message).Groups[1].Value,
                                    corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                                    new DecayingAlarm(corradeConfiguration.DataDecayType),
                                    ref groupUUID))
                                Cache.CurrentGroupsCache.Remove(groupUUID);

                            // Log instant messages.
                            if (corradeConfiguration.InstantMessageLogEnabled)
                                CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(() =>
                                {
                                    try
                                    {
                                        lock (InstantMessageLogFileLock)
                                        {
                                            Directory.CreateDirectory(corradeConfiguration.InstantMessageLogDirectory);

                                            var path =
                                                $"{Path.Combine(corradeConfiguration.InstantMessageLogDirectory, args.IM.FromAgentName)}.{CORRADE_CONSTANTS.LOG_FILE_EXTENSION}";
                                            using (
                                                var fileStream =
                                                    new FileStream(path, FileMode.Append,
                                                        FileAccess.Write, FileShare.None, 16384, true))
                                            {
                                                using (
                                                    var logWriter = new StreamWriter(fileStream,
                                                        Encoding.UTF8))
                                                {
                                                    logWriter.WriteLine(
                                                        CORRADE_CONSTANTS.INSTANT_MESSAGE_LOG_MESSAGE_FORMAT,
                                                        DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                            Utils.EnUsCulture.DateTimeFormat),
                                                        fullName.First(),
                                                        fullName.Last(),
                                                        args.IM.Message);
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // or fail and append the fail message.
                                        Feedback(
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ConsoleMessage
                                                    .COULD_NOT_WRITE_TO_INSTANT_MESSAGE_LOG_FILE),
                                            ex.PrettyPrint());
                                    }
                                }, corradeConfiguration.MaximumLogThreads, corradeConfiguration.ServicesTimeout);
                            return;
                    }
                    // Check if this is a region message.
                    switch (!args.IM.IMSessionID.Equals(UUID.Zero))
                    {
                        case false:
                            // Add the agent to the cache.
                            Cache.AddAgent(fullName.First(), fullName.Last(), args.IM.FromAgentID);
                            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                                () => SendNotification(Configuration.Notifications.RegionMessage, args),
                                corradeConfiguration.MaximumNotificationThreads);
                            // Log region messages,
                            if (corradeConfiguration.RegionMessageLogEnabled)
                                CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(() =>
                                {
                                    try
                                    {
                                        lock (RegionLogFileLock)
                                        {
                                            Directory.CreateDirectory(corradeConfiguration.RegionMessageLogDirectory);

                                            var path =
                                                $"{Path.Combine(corradeConfiguration.RegionMessageLogDirectory, Client.Network.CurrentSim.Name)}.{CORRADE_CONSTANTS.LOG_FILE_EXTENSION}";
                                            using (
                                                var fileStream =
                                                    new FileStream(path, FileMode.Append,
                                                        FileAccess.Write, FileShare.None, 16384, true))
                                            {
                                                using (
                                                    var logWriter = new StreamWriter(fileStream, Encoding.UTF8)
                                                )
                                                {
                                                    logWriter.WriteLine(
                                                        CORRADE_CONSTANTS.REGION_MESSAGE_LOG_MESSAGE_FORMAT,
                                                        DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                            Utils.EnUsCulture.DateTimeFormat),
                                                        fullName.First(),
                                                        fullName.Last(),
                                                        args.IM.Message);
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // or fail and append the fail message.
                                        Feedback(
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ConsoleMessage
                                                    .COULD_NOT_WRITE_TO_REGION_MESSAGE_LOG_FILE),
                                            ex.PrettyPrint());
                                    }
                                }, corradeConfiguration.MaximumLogThreads, corradeConfiguration.ServicesTimeout);
                            return;
                    }
                    break;
            }

            // We are now in a region of code where the message is an IM sent by an object.
            // Check if this is not a Corrade command and send an object IM notification.
            if (!Helpers.Utilities.IsCorradeCommand(args.IM.Message))
            {
                CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                    () => SendNotification(Configuration.Notifications.ObjectInstantMessage, args),
                    corradeConfiguration.MaximumNotificationThreads);

                // Log object instant messages.
                if (corradeConfiguration.InstantMessageLogEnabled)
                    CorradeThreadPool[Threading.Enumerations.ThreadType.LOG].SpawnSequential(() =>
                    {
                        try
                        {
                            lock (InstantMessageLogFileLock)
                            {
                                Directory.CreateDirectory(corradeConfiguration.InstantMessageLogDirectory);

                                var path =
                                    $"{Path.Combine(corradeConfiguration.InstantMessageLogDirectory, args.IM.FromAgentID.ToString())}.{CORRADE_CONSTANTS.LOG_FILE_EXTENSION}";
                                using (
                                    var fileStream =
                                        new FileStream(path, FileMode.Append,
                                            FileAccess.Write, FileShare.None, 16384, true))
                                {
                                    using (
                                        var logWriter = new StreamWriter(fileStream,
                                            Encoding.UTF8))
                                    {
                                        logWriter.WriteLine(
                                            CORRADE_CONSTANTS.INSTANT_MESSAGE_LOG_MESSAGE_FORMAT,
                                            DateTime.Now.ToString(CORRADE_CONSTANTS.DATE_TIME_STAMP,
                                                Utils.EnUsCulture.DateTimeFormat),
                                            args.IM.FromAgentName,
                                            $"({args.IM.FromAgentID})",
                                            args.IM.Message);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // or fail and append the fail message.
                            Feedback(
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage
                                        .COULD_NOT_WRITE_TO_INSTANT_MESSAGE_LOG_FILE),
                                ex.PrettyPrint());
                        }
                    }, corradeConfiguration.MaximumLogThreads, corradeConfiguration.ServicesTimeout);
                return;
            }

            // If the group was not set properly, then bail.
            var commandGroup = GetCorradeGroupFromMessage(args.IM.Message, corradeConfiguration);
            if (commandGroup == null || commandGroup.Equals(default(Configuration.Group)))
            {
                // Log commands without a valid group.
                Feedback(args.IM.FromAgentID.ToString(),
                    Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.UNKNOWN_GROUP));
                return;
            }

            // Otherwise process the command.
            CorradeThreadPool[Threading.Enumerations.ThreadType.COMMAND].Spawn(
                () =>
                    HandleCorradeCommand(args.IM.Message, args.IM.FromAgentName, args.IM.FromAgentID.ToString(),
                        commandGroup),
                corradeConfiguration.MaximumCommandThreads, commandGroup.UUID,
                corradeConfiguration.SchedulerExpiration);
        }

        public static Dictionary<string, string> HandleCorradeCommand(string message, string sender, string identifier,
            Configuration.Group commandGroup)
        {
            // Get password.
            var password =
                wasInput(KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.PASSWORD)),
                    message));

            // Authenticate the request against the group password.
            if (!Authenticate(commandGroup.UUID, password))
            {
                Feedback(commandGroup.Name,
                    Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.ACCESS_DENIED));
                return null;
            }

            /*
             * OpenSim sends the primitive UUID through args.IM.FromAgentID while Second Life properly sends
             * the agent UUID - which just shows how crap and non-compliant OpenSim really is. This tries to
             * resolve args.IM.FromAgentID to a name, which is what Second Life does, otherwise it just sets
             * the name to the name of the primitive sending the message.
             */
            if (wasOpenMetaverse.Helpers.IsSecondLife(Client))
            {
                UUID fromAgentID;
                if (UUID.TryParse(identifier, out fromAgentID))
                    if (
                        !Resolvers.AgentUUIDToName(Client, fromAgentID, corradeConfiguration.ServicesTimeout,
                            ref sender))
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.AGENT_NOT_FOUND),
                            fromAgentID.ToString());
                        return null;
                    }
            }

            // Log the command.
            Feedback(string.Format(Utils.EnUsCulture, "{0} : {1} ({2}) : {3}", commandGroup.Name, sender, identifier,
                KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND)), message)));

            // Horde execute.
            var hordeCommand = wasInput(
                KeyValue.Get(
                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.HORDE)), message));
            if (!string.IsNullOrEmpty(hordeCommand))
            {
                // Remove horde key.
                message = KeyValue.Delete(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.HORDE)), message);

                // Get context if it was specified.
                var contextHorde = wasInput(
                    KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.CONTEXT)), hordeCommand));

                var contextPeers = new Dictionary<Configuration.HordePeer, uint>();
                switch (!string.IsNullOrEmpty(contextHorde))
                {
                    case true:
                        var dataContext =
                            CSV.ToKeyValue(contextHorde)
                                .AsParallel()
                                .GroupBy(o => o.Key)
                                .Select(o => o.FirstOrDefault())
                                .ToDictionary(o => wasInput(o.Key), o => wasInput(o.Value));
                        if (contextHorde.Any())
                            foreach (var peer in HordeQueryStats()
                                .Concat(new[]
                                {
                                    new Configuration.HordePeer
                                    {
                                        Context = new Configuration.HordePeerContext
                                        {
                                            Contribution = corradeConfiguration.HordeCommandContribution,
                                            Load = 100 * GroupWorkers.Values.OfType<int>().Sum()
                                                   / (int) corradeConfiguration.Groups.Sum(o => o.Workers),
                                            Name = Client.Self.Name,
                                            Region = Client.Network.CurrentSim.Name,
                                            Version = CORRADE_CONSTANTS.CORRADE_VERSION
                                        }
                                    }
                                })
                                .AsParallel()
                                .Select(o => new
                                {
                                    Context = CSV.ToKeyValue(
                                            CSV.FromEnumerable(
                                                o.Context.GetStructuredData(CSV.FromEnumerable(dataContext.Keys))))
                                        .AsParallel()
                                        .GroupBy(p => p.Key)
                                        .Select(p => p.FirstOrDefault())
                                        .ToDictionary(p => wasInput(p.Key), p => wasInput(p.Value)),
                                    Peer = o
                                })
                                .Where(o => o.Context.ContentEquals(dataContext))
                                .ToDictionary(o => o.Peer, o => o.Peer.Context.Contribution))
                                contextPeers.Add(peer.Key, peer.Value);
                        break;

                    default:
                        foreach (var peer in HordeQueryStats()
                            .Concat(new[]
                            {
                                new Configuration.HordePeer
                                {
                                    Context = new Configuration.HordePeerContext
                                    {
                                        Contribution = corradeConfiguration.HordeCommandContribution,
                                        Load = 100 * GroupWorkers.Values.OfType<int>().Sum()
                                               / (int) corradeConfiguration.Groups.Sum(o => o.Workers),
                                        Name = Client.Self.Name,
                                        Region = Client.Network.CurrentSim.Name,
                                        Version = CORRADE_CONSTANTS.CORRADE_VERSION
                                    }
                                }
                            })
                            .ToDictionary(o => o, o => o.Context.Contribution))
                            contextPeers.Add(peer.Key, peer.Value);
                        break;
                }

                var callbackURL = wasInput(KeyValue.Get(
                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.CALLBACK)), message));

                var orderedContextPeers = contextPeers.OrderBy(o => o.Value);
                // No peers matched context so return a script error.
                if (!orderedContextPeers.Any())
                {
                    if (!string.IsNullOrEmpty(callbackURL))
                        CallbackQueue.Enqueue(new CallbackQueueElement
                        {
                            GroupUUID = commandGroup.UUID,
                            URL = callbackURL,
                            message = new Dictionary<string, string>
                            {
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND)),
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.HORDE))
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                    wasInput(KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                        message))
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.SUCCESS)),
                                    false.ToString()
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.STATUS)),
                                    Reflection.GetAttributeFromEnumValue<StatusAttribute>(
                                        Enumerations.ScriptError.NO_PEERS_MATCHING_CONTEXT).Status.ToString()
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.ERROR)),
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ScriptError.NO_PEERS_MATCHING_CONTEXT)
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.TIME)),
                                    DateTime.UtcNow.ToString(wasOpenMetaverse.Constants.LSL.DATE_TIME_STAMP)
                                }
                            }
                        });

                    return null;
                }

                var unison = false;
                var eligiblePeers = new Stack<Configuration.HordePeer>();
                switch (Reflection.GetEnumValueFromName<Enumerations.HordeBalance>(wasInput(
                    KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.BALANCE)),
                        hordeCommand))))
                {
                    case Enumerations.HordeBalance.UNISON:
                        unison = true;
                        foreach (var peer in orderedContextPeers)
                            eligiblePeers.Push(peer.Key);
                        break;

                    case Enumerations.HordeBalance.WEIGHT:
                        var threshold = CorradeRandom.Next(contextPeers.Count * 100);
                        foreach (var peer in orderedContextPeers)
                        {
                            if (peer.Value < threshold)
                            {
                                eligiblePeers.Push(peer.Key);
                                threshold += (int) peer.Value;
                                continue;
                            }
                            eligiblePeers.Push(peer.Key);
                            break;
                        }
                        break;

                    default:

                        if (!string.IsNullOrEmpty(callbackURL))
                            CallbackQueue.Enqueue(new CallbackQueueElement
                            {
                                GroupUUID = commandGroup.UUID,
                                URL = callbackURL,
                                message = new Dictionary<string, string>
                                {
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND)),
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.HORDE))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                        wasInput(KeyValue.Get(
                                            wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                            message))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.STATUS)),
                                        Reflection.GetAttributeFromEnumValue<StatusAttribute>(
                                            Enumerations.ScriptError.UNKNOWN_HORDE_BALANCER).Status.ToString()
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.ERROR)),
                                        Reflection.GetDescriptionFromEnumValue(
                                            Enumerations.ScriptError.UNKNOWN_HORDE_BALANCER)
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.TIME)),
                                        DateTime.UtcNow.ToString(wasOpenMetaverse.Constants.LSL.DATE_TIME_STAMP)
                                    }
                                }
                            });
                        return null;
                }

                do
                {
                    var peer = eligiblePeers.Pop();

                    // If the selected peer is not this Corrade, then distribute the command to the selected peer.
                    if (peer.Context.Name.Equals(Client.Self.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        // Process the command here.
                        HandleCorradeCommand(message, sender, identifier, commandGroup);

                        if (!string.IsNullOrEmpty(callbackURL))
                            CallbackQueue.Enqueue(new CallbackQueueElement
                            {
                                GroupUUID = commandGroup.UUID,
                                URL = callbackURL,
                                message = new Dictionary<string, string>
                                {
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND)),
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.HORDE))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                        wasInput(KeyValue.Get(
                                            wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                            message))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.SUCCESS)),
                                        true.ToString()
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.DATA)),
                                        wasOutput(CSV.FromEnumerable(peer.Context.GetStructuredData()))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.TIME)),
                                        DateTime.UtcNow.ToString(wasOpenMetaverse.Constants.LSL.DATE_TIME_STAMP)
                                    }
                                }
                            });

                        if (!unison)
                            break;

                        continue;
                    }

                    // Synchronize the group.
                    byte[] groupSynchronizationResult = null;
                    using (var memoryStream = new MemoryStream())
                    {
                        XmlSerializerCache.Serialize(memoryStream, commandGroup);
                        memoryStream.Position = 0;
                        groupSynchronizationResult = HordeHTTPClients[peer.URL].PUT(
                            $"{peer.URL.TrimEnd('/')}/command/push/{commandGroup.UUID}",
                            memoryStream).Result;
                    }

                    // If the group could not be synchronized then return a script error.
                    if (groupSynchronizationResult == null)
                    {
                        if (!string.IsNullOrEmpty(callbackURL))
                            CallbackQueue.Enqueue(new CallbackQueueElement
                            {
                                GroupUUID = commandGroup.UUID,
                                URL = callbackURL,
                                message = new Dictionary<string, string>
                                {
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND)),
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.HORDE))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                        wasInput(KeyValue.Get(
                                            wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                            message))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.SUCCESS)),
                                        false.ToString()
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.DATA)),
                                        wasOutput(CSV.FromEnumerable(peer.Context.GetStructuredData()))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.STATUS)),
                                        Reflection.GetAttributeFromEnumValue<StatusAttribute>(
                                            Enumerations.ScriptError.GROUP_SYNCHRONIZATION_FAILED).Status.ToString()
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.ERROR)),
                                        Reflection.GetDescriptionFromEnumValue(
                                            Enumerations.ScriptError.GROUP_SYNCHRONIZATION_FAILED)
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.TIME)),
                                        DateTime.UtcNow.ToString(wasOpenMetaverse.Constants.LSL.DATE_TIME_STAMP)
                                    }
                                }
                            });
                        continue;
                    }

                    // Attempt to deliver the command to a horde peer.
                    if (HordeHTTPClients[peer.URL].POST(peer.URL, message).Result == null)
                    {
                        if (!string.IsNullOrEmpty(callbackURL))
                            CallbackQueue.Enqueue(new CallbackQueueElement
                            {
                                GroupUUID = commandGroup.UUID,
                                URL = callbackURL,
                                message = new Dictionary<string, string>
                                {
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND)),
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.HORDE))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                        wasInput(KeyValue.Get(
                                            wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                            message))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.SUCCESS)),
                                        false.ToString()
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.STATUS)),
                                        Reflection.GetAttributeFromEnumValue<StatusAttribute>(
                                            Enumerations.ScriptError.EXECUTION_RETURNED_NO_RESULT).Status.ToString()
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.ERROR)),
                                        Reflection.GetDescriptionFromEnumValue(
                                            Enumerations.ScriptError.EXECUTION_RETURNED_NO_RESULT)
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.DATA)),
                                        wasOutput(CSV.FromEnumerable(peer.Context.GetStructuredData()))
                                    },
                                    {
                                        wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.TIME)),
                                        DateTime.UtcNow.ToString(wasOpenMetaverse.Constants.LSL.DATE_TIME_STAMP)
                                    }
                                }
                            });
                        continue;
                    }

                    if (!string.IsNullOrEmpty(callbackURL))
                        CallbackQueue.Enqueue(new CallbackQueueElement
                        {
                            GroupUUID = commandGroup.UUID,
                            URL = callbackURL,
                            message = new Dictionary<string, string>
                            {
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND)),
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.HORDE))
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                    wasInput(KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.GROUP)),
                                        message))
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.SUCCESS)),
                                    true.ToString()
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.DATA)),
                                    wasOutput(CSV.FromEnumerable(peer.Context.GetStructuredData()))
                                },
                                {
                                    wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.TIME)),
                                    DateTime.UtcNow.ToString(wasOpenMetaverse.Constants.LSL.DATE_TIME_STAMP)
                                }
                            }
                        });

                    if (!unison)
                        return null;
                } while (!eligiblePeers.Count.Equals(0));

                return null;
            }

            // Censor password.
            message = KeyValue.Set(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.PASSWORD)),
                CORRADE_CONSTANTS.PASSWORD_CENSOR, message);

            // Initialize workers for the group if they are not set.
            lock (GroupWorkersLock)
            {
                if (!GroupWorkers.Contains(commandGroup.Name))
                    GroupWorkers.Add(commandGroup.Name, 0u);
            }

            var configuredGroup = corradeConfiguration.Groups.AsParallel().FirstOrDefault(
                o => commandGroup.UUID.Equals(o.UUID));
            if (configuredGroup == null || configuredGroup.Equals(default(Configuration.Group)))
            {
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.UNKNOWN_GROUP),
                    commandGroup.Name);
                return null;
            }

            // Check if the workers have not been exceeded.
            uint currentWorkers;
            lock (GroupWorkersLock)
            {
                currentWorkers = (uint) GroupWorkers[commandGroup.Name];
            }

            // Refuse to proceed if the workers have been exceeded.
            if (currentWorkers > configuredGroup.Workers)
            {
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.WORKERS_EXCEEDED),
                    commandGroup.Name);
                return null;
            }

            // Increment the group workers.
            lock (GroupWorkersLock)
            {
                GroupWorkers[commandGroup.Name] = (uint) GroupWorkers[commandGroup.Name] + 1;
            }
            // Perform the command.
            var result = ProcessCommand(new CorradeCommandParameters
            {
                Message = message,
                Sender = sender,
                Identifier = identifier,
                Group = commandGroup
            });
            // Decrement the group workers.
            lock (GroupWorkersLock)
            {
                GroupWorkers[commandGroup.Name] = (uint) GroupWorkers[commandGroup.Name] - 1;
            }
            // do not send a callback if the callback queue is saturated
            if (CallbackQueue.Count >= corradeConfiguration.CallbackQueueLength)
            {
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.CALLBACK_THROTTLED));
                return result;
            }
            // send callback if registered
            var url =
                wasInput(KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.CALLBACK)),
                    message));
            // if no url was provided, do not send the callback
            if (!string.IsNullOrEmpty(url))
                CallbackQueue.Enqueue(new CallbackQueueElement
                {
                    GroupUUID = commandGroup.UUID,
                    URL = url,
                    message = result
                });
            return result;
        }

        /// <summary>
        ///     This function is responsible for processing commands.
        /// </summary>
        /// <param name="corradeCommandParameters">the command parameters</param>
        /// <returns>a dictionary of key-value pairs representing the results of the command</returns>
        public static Dictionary<string, string> ProcessCommand(
            CorradeCommandParameters corradeCommandParameters)
        {
            var result = new Dictionary<string, string>
            {
                // add the command group to the response.
                {Reflection.GetNameFromEnumValue(ScriptKeys.GROUP), corradeCommandParameters.Group.Name}
            };

            // retrieve the command from the message.
            var command =
                wasInput(KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND)),
                    corradeCommandParameters.Message));
            if (!string.IsNullOrEmpty(command))
                result.Add(Reflection.GetNameFromEnumValue(ScriptKeys.COMMAND), command);

            // execute command, sift data and check for errors
            var success = false;
            try
            {
                // Find command.
                var scriptKey = Reflection.GetEnumValueFromName<ScriptKeys>(command);
                if (scriptKey.Equals(default(ScriptKeys)))
                    throw new ScriptException(Enumerations.ScriptError.COMMAND_NOT_FOUND);
                var execute =
                    Reflection.GetAttributeFromEnumValue<CorradeCommandAttribute>(scriptKey);

                // Execute the command.
                try
                {
                    Interlocked.Increment(ref CorradeHeartbeat.ExecutingCommands);
                    execute.CorradeCommand.Invoke(corradeCommandParameters, result);
                    Interlocked.Increment(ref CorradeHeartbeat.ProcessedCommands);
                    // Sifting was requested so apply the filters in order.
                    var sift =
                        wasInput(KeyValue.Get(wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.SIFT)),
                            corradeCommandParameters.Message));
                    string data;
                    if (result.TryGetValue(Reflection.GetNameFromEnumValue(ResultKeys.DATA), out data) &&
                        !string.IsNullOrEmpty(sift))
                        foreach (var kvp in CSV.ToKeyValue(sift)
                            .AsParallel()
                            .GroupBy(o => o.Key)
                            .Select(o => o.FirstOrDefault())
                            .ToDictionary(o => wasInput(o.Key), o => wasInput(o.Value)))
                        {
                            switch (Reflection.GetEnumValueFromName<Sift>(kvp.Key))
                            {
                                case Sift.TAKE:
                                    // Take a specified amount from the results if requested.
                                    uint take;
                                    if (!string.IsNullOrEmpty(data) &&
                                        uint.TryParse(kvp.Value, NumberStyles.Integer, Utils.EnUsCulture,
                                            out take))
                                        data = CSV.FromEnumerable(CSV.ToEnumerable(data).Take((int) take));
                                    break;

                                case Sift.SKIP:
                                    // Skip a number of elements if requested.
                                    uint skip;
                                    if (!string.IsNullOrEmpty(data) &&
                                        uint.TryParse(kvp.Value, NumberStyles.Integer, Utils.EnUsCulture,
                                            out skip))
                                        data = CSV.FromEnumerable(CSV.ToEnumerable(data).Skip((int) skip));
                                    break;

                                case Sift.EACH:
                                    // Return a stride in case it was requested.
                                    uint each;
                                    if (!string.IsNullOrEmpty(data) &&
                                        uint.TryParse(kvp.Value, NumberStyles.Integer, Utils.EnUsCulture,
                                            out each))
                                        data = CSV.FromEnumerable(CSV.ToEnumerable(data)
                                            .Where((e, i) => i % each == 0));
                                    break;

                                case Sift.MATCH:
                                    // Match the results if requested.
                                    if (!string.IsNullOrEmpty(data) && !string.IsNullOrEmpty(kvp.Value))
                                        data =
                                            CSV.FromEnumerable(new Regex(kvp.Value, RegexOptions.Compiled).Matches(data)
                                                .AsParallel()
                                                .Cast<Match>()
                                                .Select(m => m.Groups).SelectMany(
                                                    matchGroups => Enumerable.Range(0, matchGroups.Count).Skip(1),
                                                    (matchGroups, i) => new
                                                    {
                                                        matchGroups,
                                                        i
                                                    })
                                                .SelectMany(
                                                    t => Enumerable.Range(0, t.matchGroups[t.i].Captures.Count),
                                                    (t, j) => t.matchGroups[t.i].Captures[j].Value));
                                    break;

                                case Sift.COUNT:
                                    if (!string.IsNullOrEmpty(data) && !string.IsNullOrEmpty(kvp.Value))
                                    {
                                        var criteria = new Regex(kvp.Value, RegexOptions.Compiled);
                                        data = CSV.ToEnumerable(data).Count(o => criteria.IsMatch(o))
                                            .ToString(Utils.EnUsCulture);
                                    }
                                    break;

                                case Sift.JS:
                                    if (!string.IsNullOrEmpty(data))
                                        data = new Engine()
                                            .SetValue("data", data)
                                            .Execute(kvp.Value)
                                            .ToString();
                                    break;

                                default:
                                    throw new ScriptException(Enumerations.ScriptError.UNKNOWN_SIFT);
                            }
                            switch (!string.IsNullOrEmpty(data))
                            {
                                case true:
                                    result[Reflection.GetNameFromEnumValue(ResultKeys.DATA)] = data;
                                    break;

                                default:
                                    result.Remove(Reflection.GetNameFromEnumValue(ResultKeys.DATA));
                                    break;
                            }
                        }

                    success = true;
                }
                catch (ScriptException sx)
                {
                    // we have a script error so return a status as well
                    result.Add(Reflection.GetNameFromEnumValue(ResultKeys.ERROR), sx.Message);
                    result.Add(Reflection.GetNameFromEnumValue(ResultKeys.STATUS),
                        sx.Status.ToString());
                }
                finally
                {
                    Interlocked.Decrement(ref CorradeHeartbeat.ExecutingCommands);
                }
            }
            catch (Exception ex)
            {
                // we have a generic exception so return the message
                result.Add(Reflection.GetNameFromEnumValue(ResultKeys.ERROR), ex.Message);
                Feedback(Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.CORRADE_COMMAND_ERROR),
                    ex.PrettyPrint());
            }

            // add the final success status
            result.Add(Reflection.GetNameFromEnumValue(ResultKeys.SUCCESS),
                success.ToString(Utils.EnUsCulture));

            // add the time stamp
            result.Add(Reflection.GetNameFromEnumValue(ResultKeys.TIME),
                DateTime.UtcNow.ToString(wasOpenMetaverse.Constants.LSL.DATE_TIME_STAMP));

            // build afterburn
            var AfterBurnLock = new object();
            // remove keys that are script keys, result keys or invalid key-value pairs
            KeyValue.Decode(corradeCommandParameters.Message)
                .ToDictionary(o => wasInput(o.Key), o => wasInput(o.Value))
                .AsParallel()
                .Where(
                    o =>
                        !string.IsNullOrEmpty(o.Key) && !CorradeResultKeys.Contains(o.Key) &&
                        !CorradeScriptKeys.Contains(o.Key) && !string.IsNullOrEmpty(o.Value))
                .ForAll(o =>
                {
                    lock (AfterBurnLock)
                    {
                        result.Add(o.Key, o.Value);
                    }
                });
            return result;
        }

        private static void HandleTerseObjectUpdate(object sender, TerseObjectUpdateEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.TerseUpdates, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleRadarObjects(object sender, SimChangedEventArgs e)
        {
            lock (RadarObjectsLock)
            {
                if (RadarObjects.Any())
                    RadarObjects.Clear();
            }
        }

        private static void HandleSimChanged(object sender, SimChangedEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.RegionCrossed, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleMoneyBalance(object sender, BalanceEventArgs e)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Balance, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void HandleMoneyBalance(object sender, MoneyBalanceReplyEventArgs e)
        {
            // Do not trigger economy for unknown transaction types.
            if (((MoneyTransactionType)
                e.TransactionInfo.TransactionType).Equals(MoneyTransactionType.None))
                return;

            CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                () => SendNotification(Configuration.Notifications.Economy, e),
                corradeConfiguration.MaximumNotificationThreads);
        }

        private static void UpdateDynamicConfiguration(Configuration configuration, bool firstRun = false)
        {
            // Send message that we are updating the configuration.
            Feedback(
                Reflection.GetDescriptionFromEnumValue(
                    Enumerations.ConsoleMessage.UPDATING_CORRADE_CONFIGURATION));

            // Check TOS
            if (!corradeConfiguration.TOSAccepted)
            {
                Feedback(Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.TOS_NOT_ACCEPTED));
                Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
            }

            // Replace the start locations queue.
            StartLocationQueue.Clear();
            foreach (var location in corradeConfiguration.StartLocations)
                StartLocationQueue.Enqueue(location);

            // Setup heartbeat log timer.
            CorradeHeartBeatLogTimer.Change(TimeSpan.FromMilliseconds(configuration.HeartbeatLogInterval),
                TimeSpan.FromMilliseconds(configuration.HeartbeatLogInterval));

            // Set the content type based on chosen output filers
            switch (configuration.OutputFilters.LastOrDefault())
            {
                case Configuration.Filter.RFC1738:
                    CorradePOSTMediaType = CORRADE_CONSTANTS.CONTENT_TYPE.WWW_FORM_URLENCODED;
                    break;

                default:
                    CorradePOSTMediaType = CORRADE_CONSTANTS.CONTENT_TYPE.TEXT_PLAIN;
                    break;
            }

            // Setup per-group HTTP clients.
            configuration.Groups.AsParallel().ForAll(o =>
            {
                // Create cookie containers for new groups.
                lock (GroupCookieContainersLock)
                {
                    if (!GroupCookieContainers.ContainsKey(o.UUID))
                        GroupCookieContainers.Add(o.UUID, new CookieContainer());
                }
                lock (GroupHTTPClientsLock)
                {
                    if (!GroupHTTPClients.ContainsKey(o.UUID))
                        GroupHTTPClients.Add(o.UUID, new wasHTTPClient
                        (CORRADE_CONSTANTS.USER_AGENT, GroupCookieContainers[o.UUID], CorradePOSTMediaType,
                            configuration.ServicesTimeout));
                }
            });

            // Remove HTTP clients from groups that are not configured.
            var HTTPClientKeys = new List<UUID>();
            lock (GroupHTTPClientsLock)
            {
                HTTPClientKeys.AddRange(GroupHTTPClients.Keys);
            }

            HTTPClientKeys.AsParallel().Where(o => !configuration.Groups.Any(p => p.UUID.Equals(o)))
                .AsParallel().ForAll(
                    o =>
                    {
                        lock (GroupHTTPClientsLock)
                        {
                            GroupHTTPClients.Remove(o);
                        }
                    });

            // Setup horde synchronization if enabled.
            switch (configuration.EnableHorde)
            {
                case true:
                    // Setup HTTP clients.
                    lock (HordeHTTPClientsLock)
                    {
                        HordeHTTPClients.Clear();
                    }
                    configuration.HordePeers.AsParallel().ForAll(o =>
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.Add(o.URL, new wasHTTPClient
                            (CORRADE_CONSTANTS.USER_AGENT, new CookieContainer(), @"text/plain",
                                new AuthenticationHeaderValue(@"Basic",
                                    Convert.ToBase64String(
                                        Encoding.ASCII.GetBytes($"{o.Username}:{o.Password}"))),
                                new Dictionary<string, string>
                                {
                                    {
                                        CORRADE_CONSTANTS.HORDE_SHARED_SECRET_HEADER,
                                        Convert.ToBase64String(
                                            Encoding.UTF8.GetBytes(o.SharedSecret))
                                    }
                                },
                                configuration.ServicesTimeout));
                        }
                    });
                    // Bind to horde synchronization changes.
                    switch (
                        configuration.HordePeers.AsParallel()
                            .Any(
                                o => o.SynchronizationMask.IsMaskFlagSet(Configuration.HordeDataSynchronization.Agent)))
                    {
                        case true:
                            Cache.ObservableAgentCache.CollectionChanged -= HandleAgentCacheChanged;
                            Cache.ObservableAgentCache.CollectionChanged += HandleAgentCacheChanged;
                            break;

                        default:
                            Cache.ObservableAgentCache.CollectionChanged -= HandleAgentCacheChanged;
                            break;
                    }
                    switch (
                        configuration.HordePeers.AsParallel()
                            .Any(
                                o => o.SynchronizationMask.IsMaskFlagSet(Configuration.HordeDataSynchronization.Region))
                    )
                    {
                        case true:
                            Cache.ObservableRegionCache.CollectionChanged -= HandleRegionCacheChanged;
                            Cache.ObservableRegionCache.CollectionChanged += HandleRegionCacheChanged;
                            break;

                        default:
                            Cache.ObservableRegionCache.CollectionChanged -= HandleRegionCacheChanged;
                            break;
                    }
                    switch (
                        configuration.HordePeers.AsParallel()
                            .Any(
                                o => o.SynchronizationMask.IsMaskFlagSet(Configuration.HordeDataSynchronization.Group)))
                    {
                        case true:
                            Cache.ObservableGroupCache.CollectionChanged -= HandleGroupCacheChanged;
                            Cache.ObservableGroupCache.CollectionChanged += HandleGroupCacheChanged;
                            break;

                        default:
                            Cache.ObservableGroupCache.CollectionChanged -= HandleGroupCacheChanged;
                            break;
                    }
                    switch (
                        configuration.HordePeers.AsParallel()
                            .Any(
                                o => o.SynchronizationMask.IsMaskFlagSet(Configuration.HordeDataSynchronization.Mute)))
                    {
                        case true:
                            Cache.ObservableMuteCache.CollectionChanged -= HandleMuteCacheChanged;
                            Cache.ObservableMuteCache.CollectionChanged += HandleMuteCacheChanged;
                            break;

                        default:
                            Cache.ObservableMuteCache.CollectionChanged -= HandleMuteCacheChanged;
                            break;
                    }
                    break;

                default:
                    // Remove HTTP clients.
                    lock (HordeHTTPClientsLock)
                    {
                        HordeHTTPClients.Clear();
                    }
                    Cache.ObservableAgentCache.CollectionChanged -= HandleAgentCacheChanged;
                    Cache.ObservableRegionCache.CollectionChanged -= HandleRegionCacheChanged;
                    Cache.ObservableGroupCache.CollectionChanged -= HandleGroupCacheChanged;
                    Cache.ObservableMuteCache.CollectionChanged -= HandleMuteCacheChanged;
                    break;
            }

            // Enable the group scheduling timer if permissions were granted to groups.
            switch (configuration.Groups.AsParallel()
                .Any(
                    o => o.PermissionMask.IsMaskFlagSet(Configuration.Permissions.Schedule) &&
                         !o.Schedules.Equals(0)))
            {
                case true:
                    // Start the group schedules timer.
                    GroupSchedulesTimer.Change(TimeSpan.FromMilliseconds(configuration.SchedulesResolution),
                        TimeSpan.FromMilliseconds(configuration.SchedulesResolution));
                    break;

                default:
                    GroupSchedulesTimer.Stop();
                    break;
            }

            // Enable SIML in case it was enabled in the configuration file.
            try
            {
                switch (configuration.EnableSIML)
                {
                    case true:
                        lock (SIMLBotLock)
                        {
                            SynBotTimer.Stop();
                            SynBot = new SimlBot();
                            SynBotUser = SynBot.MainUser;

                            SynBot.Learning += HandleSynBotLearning;
                            SynBot.Memorizing += HandleSynBotMemorizing;
                            SynBotUser.EmotionChanged += HandleSynBotUserEmotionChanged;
                            LoadChatBotFiles.BeginInvoke(
                                o => { SynBotTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)); }, null);
                        }
                        break;

                    default:
                        lock (SIMLBotLock)
                        {
                            SynBotTimer.Stop();

                            SynBot.Learning -= HandleSynBotLearning;
                            SynBot.Memorizing -= HandleSynBotMemorizing;
                            SynBotUser.EmotionChanged -= HandleSynBotUserEmotionChanged;
                            if (!string.IsNullOrEmpty(SIMLBotConfigurationWatcher.Path))
                                SIMLBotConfigurationWatcher.EnableRaisingEvents = false;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SETTING_UP_SIML_CONFIGURATION_WATCHER),
                    ex.PrettyPrint());
            }

            // Dynamically disable or enable notifications.
            Reflection.GetEnumValues<Configuration.Notifications>().AsParallel().ForAll(o =>
            {
                var enabled = configuration.Groups.AsParallel().Any(
                    p => p.NotificationMask.IsMaskFlagSet(o));
                switch (o)
                {
                    case Configuration.Notifications.Sound:
                        switch (enabled)
                        {
                            case true:
                                Client.Sound.SoundTrigger += HandleSoundTrigger;
                                Client.Sound.AttachedSound += HandleAttachedSound;
                                Client.Sound.AttachedSoundGainChange += HandleAttachedSoundGain;
                                break;

                            default:
                                Client.Sound.SoundTrigger -= HandleSoundTrigger;
                                Client.Sound.AttachedSound -= HandleAttachedSound;
                                Client.Sound.AttachedSoundGainChange -= HandleAttachedSoundGain;
                                break;
                        }
                        break;

                    case Configuration.Notifications.AnimationsChanged:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.AnimationsChanged += HandleAnimationsChanged;
                                break;

                            default:
                                Client.Self.AnimationsChanged -= HandleAnimationsChanged;
                                break;
                        }
                        break;

                    case Configuration.Notifications.Feed:
                        switch (enabled)
                        {
                            case true:
                                // Start the group feed thread.
                                GroupFeedsTimer.Change(
                                    TimeSpan.FromMilliseconds(corradeConfiguration.FeedsUpdateInterval),
                                    TimeSpan.FromMilliseconds(corradeConfiguration.FeedsUpdateInterval));
                                break;

                            default:
                                // Stop the group feed thread.
                                GroupFeedsTimer.Stop();
                                break;
                        }
                        break;

                    case Configuration.Notifications.Friendship:
                        switch (enabled)
                        {
                            case true:
                                Client.Friends.FriendshipOffered += HandleFriendshipOffered;
                                Client.Friends.FriendshipResponse += HandleFriendShipResponse;
                                Client.Friends.FriendOnline += HandleFriendOnlineStatus;
                                Client.Friends.FriendOffline += HandleFriendOnlineStatus;
                                Client.Friends.FriendRightsUpdate += HandleFriendRightsUpdate;
                                break;

                            default:
                                Client.Friends.FriendshipOffered -= HandleFriendshipOffered;
                                Client.Friends.FriendshipResponse -= HandleFriendShipResponse;
                                Client.Friends.FriendOnline -= HandleFriendOnlineStatus;
                                Client.Friends.FriendOffline -= HandleFriendOnlineStatus;
                                Client.Friends.FriendRightsUpdate -= HandleFriendRightsUpdate;
                                break;
                        }
                        break;

                    case Configuration.Notifications.ScriptPermission:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.ScriptQuestion += HandleScriptQuestion;
                                break;

                            default:
                                Client.Self.ScriptQuestion -= HandleScriptQuestion;
                                break;
                        }
                        break;

                    case Configuration.Notifications.AlertMessage:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.AlertMessage += HandleAlertMessage;
                                break;

                            default:
                                Client.Self.AlertMessage -= HandleAlertMessage;
                                break;
                        }
                        break;

                    case Configuration.Notifications.Balance:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.MoneyBalance += HandleMoneyBalance;
                                break;

                            default:
                                Client.Self.MoneyBalance -= HandleMoneyBalance;
                                break;
                        }
                        break;

                    case Configuration.Notifications.Economy:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.MoneyBalanceReply += HandleMoneyBalance;
                                break;

                            default:
                                Client.Self.MoneyBalanceReply -= HandleMoneyBalance;
                                break;
                        }
                        break;

                    case Configuration.Notifications.ScriptDialog:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.ScriptDialog += HandleScriptDialog;
                                break;

                            default:
                                Client.Self.ScriptDialog -= HandleScriptDialog;
                                break;
                        }
                        break;

                    case Configuration.Notifications.SitChanged:
                        switch (enabled)
                        {
                            case true:
                                Client.Objects.AvatarSitChanged += HandleAvatarSitChanged;
                                break;

                            default:
                                Client.Objects.AvatarSitChanged -= HandleAvatarSitChanged;
                                break;
                        }
                        break;

                    case Configuration.Notifications.TerseUpdates:
                        switch (enabled)
                        {
                            case true:
                                Client.Objects.TerseObjectUpdate += HandleTerseObjectUpdate;
                                break;

                            default:
                                Client.Objects.TerseObjectUpdate -= HandleTerseObjectUpdate;
                                break;
                        }
                        break;

                    case Configuration.Notifications.ViewerEffect:
                        switch (enabled)
                        {
                            case true:
                                Client.Avatars.ViewerEffect += HandleViewerEffect;
                                Client.Avatars.ViewerEffectPointAt += HandleViewerEffect;
                                Client.Avatars.ViewerEffectLookAt += HandleViewerEffect;
                                break;

                            default:
                                Client.Avatars.ViewerEffect -= HandleViewerEffect;
                                Client.Avatars.ViewerEffectPointAt -= HandleViewerEffect;
                                Client.Avatars.ViewerEffectLookAt -= HandleViewerEffect;
                                break;
                        }
                        break;

                    case Configuration.Notifications.MeanCollision:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.MeanCollision += HandleMeanCollision;
                                break;

                            default:
                                Client.Self.MeanCollision -= HandleMeanCollision;
                                break;
                        }
                        break;

                    case Configuration.Notifications.RegionCrossed:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.RegionCrossed += HandleRegionCrossed;
                                Client.Network.SimChanged += HandleSimChanged;
                                break;

                            default:
                                Client.Self.RegionCrossed -= HandleRegionCrossed;
                                Client.Network.SimChanged -= HandleSimChanged;
                                break;
                        }
                        break;

                    case Configuration.Notifications.LoadURL:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.LoadURL += HandleLoadURL;
                                break;

                            default:
                                Client.Self.LoadURL -= HandleLoadURL;
                                break;
                        }
                        break;

                    case Configuration.Notifications.ScriptControl:
                        switch (enabled)
                        {
                            case true:
                                Client.Self.ScriptControlChange += HandleScriptControlChange;
                                break;

                            default:
                                Client.Self.ScriptControlChange -= HandleScriptControlChange;
                                break;
                        }
                        break;

                    case Configuration.Notifications.Store:
                        if (Client.Inventory.Store != null)
                            switch (enabled)
                            {
                                case true:
                                    Client.Inventory.Store.InventoryObjectAdded += HandleInventoryObjectAdded;
                                    Client.Inventory.Store.InventoryObjectRemoved += HandleInventoryObjectRemoved;
                                    Client.Inventory.Store.InventoryObjectUpdated += HandleInventoryObjectUpdated;
                                    break;

                                default:
                                    Client.Inventory.Store.InventoryObjectAdded -= HandleInventoryObjectAdded;
                                    Client.Inventory.Store.InventoryObjectRemoved -= HandleInventoryObjectRemoved;
                                    Client.Inventory.Store.InventoryObjectUpdated -= HandleInventoryObjectUpdated;
                                    break;
                            }
                        break;
                }
            });

            // Depending on whether groups have bound to the viewer effects notification,
            // start or stop the viwer effect expiration thread.
            switch (
                configuration.Groups.AsParallel()
                    .Any(o => o.NotificationMask.IsMaskFlagSet(Configuration.Notifications.ViewerEffect)))
            {
                case true:
                    // Start sphere and beam effect expiration thread
                    EffectsExpirationTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                    break;

                default:
                    // Stop the effects expiration thread.
                    EffectsExpirationTimer.Stop();
                    break;
            }

            // Depending on whether any group has bound either the avatar radar notification,
            // or the primitive radar notification, install or uinstall the listeners.
            switch (
                configuration.Groups.AsParallel().Any(
                    o =>
                        o.NotificationMask.IsMaskFlagSet(Configuration.Notifications.RadarAvatars) ||
                        o.NotificationMask.IsMaskFlagSet(Configuration.Notifications.RadarPrimitives)))
            {
                case true:
                    Client.Network.SimChanged += HandleRadarObjects;
                    Client.Objects.AvatarUpdate += HandleAvatarUpdate;
                    Client.Objects.ObjectUpdate += HandleObjectUpdate;
                    Client.Objects.KillObject += HandleKillObject;
                    break;

                default:
                    Client.Network.SimChanged -= HandleRadarObjects;
                    Client.Objects.AvatarUpdate -= HandleAvatarUpdate;
                    Client.Objects.ObjectUpdate -= HandleObjectUpdate;
                    Client.Objects.KillObject -= HandleKillObject;
                    break;
            }

            // Enable the TCP notifications server in case it was enabled in the Configuration.
            switch (configuration.EnableTCPNotificationsServer)
            {
                case true:
                    // Don't start if the TCP notifications server is already started.
                    if (TCPNotificationsThread != null)
                        break;
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.STARTING_TCP_NOTIFICATIONS_SERVER));
                    runTCPNotificationsServer = true;
                    // Start the TCP notifications server.
                    TCPNotificationsThread = new Thread(ProcessTCPNotifications);
                    TCPNotificationsThread.IsBackground = true;
                    TCPNotificationsThread.Start();
                    break;

                default:
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.STOPPING_TCP_NOTIFICATIONS_SERVER));
                    runTCPNotificationsServer = false;
                    try
                    {
                        if (TCPNotificationsThread != null)
                        {
                            TCPListener.Stop();
                            if (
                                TCPNotificationsThread.ThreadState.Equals(ThreadState.Running) ||
                                TCPNotificationsThread.ThreadState.Equals(ThreadState.WaitSleepJoin))
                                if (!TCPNotificationsThread.Join(1000))
                                {
                                    TCPNotificationsThread.Abort();
                                    TCPNotificationsThread.Join();
                                }
                        }
                    }
                    catch (Exception)
                    {
                        /* We are going down and we do not care. */
                    }
                    finally
                    {
                        TCPNotificationsThread = null;
                    }
                    break;
            }

            // Enable the Nucleus server in case it is supported and it was enabled in the Configuration.
            switch (HttpListener.IsSupported)
            {
                case true:
                    switch (corradeConfiguration.EnableNucleusServer)
                    {
                        case true:
                            // If this is a first run request, then just break out.
                            if (firstRun)
                            {
                                Feedback(
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.STARTING_NUCLEUS_SERVER),
                                    corradeConfiguration.NucleusServerPrefix);
                                break;
                            }

                            // Don't start if the HTTP server is already started.
                            if (NucleusHTTPServer != null && NucleusHTTPServer.IsRunning)
                                try
                                {
                                    NucleusHTTPServer.Stop((int) corradeConfiguration.ServicesTimeout);
                                }
                                catch (Exception ex)
                                {
                                    Feedback(Reflection.GetDescriptionFromEnumValue(
                                            Enumerations.ConsoleMessage.NUCLEUS_SERVER_ERROR),
                                        ex.PrettyPrint());
                                }
                            Feedback(
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.STARTING_NUCLEUS_SERVER),
                                corradeConfiguration.NucleusServerPrefix);
                            NucleusHTTPServer = new NucleusHTTPServer();
                            try
                            {
                                // Enable basic authentication.
                                NucleusHTTPServer.AuthenticationSchemes = AuthenticationSchemes.Basic;
                                // Start the server.
                                NucleusHTTPServer.Start(new List<string> {corradeConfiguration.NucleusServerPrefix});
                            }
                            catch (Exception ex)
                            {
                                Feedback(
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.NUCLEUS_SERVER_ERROR), ex.PrettyPrint());
                            }
                            break;

                        default:
                            if (NucleusHTTPServer == null || !NucleusHTTPServer.IsRunning)
                                break;
                            Feedback(
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.STOPPING_NUCLEUS_SERVER));
                            try
                            {
                                NucleusHTTPServer.Stop((int) corradeConfiguration.ServicesTimeout);
                            }
                            catch (Exception ex)
                            {
                                Feedback(Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.NUCLEUS_SERVER_ERROR), ex.PrettyPrint());
                            }
                            break;
                    }
                    break;

                default:
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.NUCLEUS_SERVER_ERROR),
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.HTTP_SERVER_NOT_SUPPORTED));
                    break;
            }

            // Enable the HTTP server in case it is supported and it was enabled in the Configuration.
            switch (HttpListener.IsSupported)
            {
                case true:
                    switch (configuration.EnableHTTPServer)
                    {
                        case true:
                            // Don't start if the HTTP server is already started.
                            if (CorradeHTTPServer != null && CorradeHTTPServer.IsRunning)
                                try
                                {
                                    CorradeHTTPServer.Stop((int) corradeConfiguration.ServicesTimeout);
                                }
                                catch (Exception ex)
                                {
                                    Feedback(Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.HTTP_SERVER_ERROR), ex.PrettyPrint());
                                }
                            Feedback(
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.STARTING_HTTP_SERVER),
                                corradeConfiguration.HTTPServerPrefix);

                            CorradeHTTPServer = new CorradeHTTPServer
                            {
                                AuthenticationSchemes = AuthenticationSchemes.Anonymous | AuthenticationSchemes.Basic
                            };
                            try
                            {
                                CorradeHTTPServer.Start(new List<string> {corradeConfiguration.HTTPServerPrefix});
                            }
                            catch (Exception ex)
                            {
                                Feedback(
                                    Reflection.GetDescriptionFromEnumValue(
                                        Enumerations.ConsoleMessage.HTTP_SERVER_ERROR),
                                    ex.PrettyPrint());
                            }
                            break;

                        default:
                            if (CorradeHTTPServer == null || !CorradeHTTPServer.IsRunning)
                                break;
                            Feedback(
                                Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.STOPPING_HTTP_SERVER));
                            try
                            {
                                CorradeHTTPServer?.Stop((int) corradeConfiguration.ServicesTimeout);
                            }
                            catch (Exception ex)
                            {
                                Feedback(Reflection.GetDescriptionFromEnumValue(
                                    Enumerations.ConsoleMessage.HTTP_SERVER_ERROR), ex.PrettyPrint());
                            }
                            break;
                    }
                    break;

                default:
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.HTTP_SERVER_ERROR),
                        Reflection.GetDescriptionFromEnumValue(
                            Enumerations.ConsoleMessage.HTTP_SERVER_NOT_SUPPORTED));
                    break;
            }

            // Apply settings to the instance.
            Client.Settings.LOGIN_TIMEOUT = (int) configuration.ServicesTimeout;
            Client.Settings.LOGOUT_TIMEOUT = (int) configuration.ServicesTimeout;
            Client.Settings.SIMULATOR_TIMEOUT = (int) configuration.ServicesTimeout;
            Client.Settings.CAPS_TIMEOUT = (int) configuration.ServicesTimeout;
            Client.Settings.MAP_REQUEST_TIMEOUT = (int) configuration.ServicesTimeout;
            Client.Settings.TRANSFER_TIMEOUT = (int) configuration.ServicesTimeout;
            Client.Settings.TELEPORT_TIMEOUT = (int) configuration.ServicesTimeout;
            Settings.MAX_HTTP_CONNECTIONS = (int) configuration.ConnectionLimit;

            // Network Settings
            // Set the outgoing IP address if specified in the configuration file.
            if (!string.IsNullOrEmpty(corradeConfiguration.BindIPAddress))
                try
                {
                    Settings.BIND_ADDR = IPAddress.Parse(corradeConfiguration.BindIPAddress);
                }
                catch (Exception ex)
                {
                    Feedback(
                        Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.UNKNOWN_IP_ADDRESS),
                        ex.PrettyPrint());
                    Environment.Exit(corradeConfiguration.ExitCodeAbnormal);
                }

            // ServicePoint settings.
            ServicePointManager.DefaultConnectionLimit = (int) configuration.ConnectionLimit;
            ServicePointManager.UseNagleAlgorithm = configuration.UseNaggle;
            ServicePointManager.Expect100Continue = configuration.UseExpect100Continue;
            ServicePointManager.MaxServicePointIdleTime = (int) configuration.ConnectionIdleTime;
            // Do not use SSLv3 - POODLE
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 |
                                                   SecurityProtocolType.Tls12;

            // Throttles.
            Client.Throttle.Total = configuration.ThrottleTotal;
            Client.Throttle.Land = configuration.ThrottleLand;
            Client.Throttle.Task = configuration.ThrottleTask;
            Client.Throttle.Texture = configuration.ThrottleTexture;
            Client.Throttle.Wind = configuration.ThrottleWind;
            Client.Throttle.Resend = configuration.ThrottleResend;
            Client.Throttle.Asset = configuration.ThrottleAsset;
            Client.Throttle.Cloud = configuration.ThrottleCloud;

            // Client identification tag.
            Client.Settings.CLIENT_IDENTIFICATION_TAG = configuration.ClientIdentificationTag;

            // Cache settings.
            Directory.CreateDirectory(Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY,
                CORRADE_CONSTANTS.ASSET_CACHE_DIRECTORY));
            Client.Settings.ASSET_CACHE_DIR = Path.Combine(CORRADE_CONSTANTS.CACHE_DIRECTORY,
                CORRADE_CONSTANTS.ASSET_CACHE_DIRECTORY);
            Client.Assets.Cache.AutoPruneInterval = corradeConfiguration.CacheAutoPruneInterval;
            Client.Assets.Cache.AutoPruneEnabled = corradeConfiguration.CacheEnableAutoPrune;

            // Multiple simulator connections.
            Client.Settings.MULTIPLE_SIMS = corradeConfiguration.MultipleSimulatorConnections;

            // Send message that the configuration has been updated.
            Feedback(
                Reflection.GetDescriptionFromEnumValue(Enumerations.ConsoleMessage.CORRADE_CONFIGURATION_UPDATED));
        }

        private static void HandleSynBotUserEmotionChanged(object sender, EmotionChangedEventArgs e)
        {
            //throw new NotImplementedException();
        }

        /// <summary>
        ///     Queries the horde for metrics.
        /// </summary>
        /// <returns>Horde peers with metrics.</returns>
        private static IEnumerable<Configuration.HordePeer> HordeQueryStats()
        {
            foreach (var o in HordeHTTPClients
                .AsParallel()
                .Select(o => new
                {
                    Peer = corradeConfiguration.HordePeers.SingleOrDefault(
                        p => string.Equals(o.Key, p.URL, StringComparison.OrdinalIgnoreCase)),
                    Client = o
                }).Where(
                    o => o.Peer != null))
            {
                var data = o.Client.Value.GET($"{o.Client.Key.TrimEnd('/')}/command/metrics").Result;
                if (data == null)
                    yield break;
                using (var memoryStream = new MemoryStream(data))
                {
                    o.Peer.Context = XmlSerializerCache.Deserialize<Configuration.HordePeerContext>(memoryStream);
                    yield return o.Peer;
                }
            }
        }

        public static void HordeDistributeCacheAsset(UUID assetUUID, byte[] data,
            Configuration.HordeDataSynchronizationOption option)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.HORDE].Spawn(
                () =>
                {
                    try
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.AsParallel()
                                .Where(
                                    p =>
                                    {
                                        var peer = corradeConfiguration.HordePeers.SingleOrDefault(
                                            q => string.Equals(p.Key, q.URL, StringComparison.OrdinalIgnoreCase));
                                        return peer != null && peer
                                                   .SynchronizationMask.IsMaskFlagSet(
                                                       Configuration.HordeDataSynchronization.Asset);
                                    })
                                .ForAll(async p =>
                                {
                                    switch (option)
                                    {
                                        case Configuration.HordeDataSynchronizationOption.Add:
                                            using (var memorySteam = new MemoryStream(data))
                                            {
                                                await
                                                    p.Value.PUT(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.CACHE)}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Asset)}/{assetUUID.ToString()}",
                                                        memorySteam);
                                            }
                                            break;

                                        case Configuration.HordeDataSynchronizationOption.Remove:
                                            using (var memorySteam = new MemoryStream(data))
                                            {
                                                await
                                                    p.Value.DELETE(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.CACHE)}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Asset)}/{assetUUID.ToString()}",
                                                        memorySteam);
                                            }
                                            break;
                                    }
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_DISTRIBUTE_RESOURCE),
                            Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Asset),
                            ex.PrettyPrint());
                    }
                });
        }

        private static void HandleDistributeBayes(UUID groupUUID, string data,
            Configuration.HordeDataSynchronizationOption option)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.HORDE].Spawn(
                () =>
                {
                    try
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.AsParallel()
                                .Where(
                                    p =>
                                    {
                                        var peer = corradeConfiguration.HordePeers.SingleOrDefault(
                                            q => string.Equals(p.Key, q.URL, StringComparison.OrdinalIgnoreCase));
                                        return peer != null && peer
                                                   .SynchronizationMask.IsMaskFlagSet(
                                                       Configuration.HordeDataSynchronization.Bayes);
                                    })
                                .ForAll(
                                    async p =>
                                    {
                                        switch (option)
                                        {
                                            case Configuration.HordeDataSynchronizationOption.Add:
                                                await
                                                    p.Value.PUT(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.BAYES)}/{groupUUID.ToString()}",
                                                        data);
                                                break;

                                            case Configuration.HordeDataSynchronizationOption.Remove:
                                                await
                                                    p.Value.DELETE(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.BAYES)}/{groupUUID.ToString()}",
                                                        data);
                                                break;
                                        }
                                    });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_DISTRIBUTE_RESOURCE),
                            Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Bayes),
                            ex.PrettyPrint());
                    }
                });
        }

        private static void HordeDistributeCacheGroup(Cache.Group o,
            Configuration.HordeDataSynchronizationOption option)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.HORDE].Spawn(
                () =>
                {
                    try
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.AsParallel().Where(
                                    p =>
                                    {
                                        var peer = corradeConfiguration.HordePeers.SingleOrDefault(
                                            q => string.Equals(p.Key, q.URL, StringComparison.OrdinalIgnoreCase));
                                        return peer != null && peer
                                                   .SynchronizationMask
                                                   .IsMaskFlagSet(Configuration.HordeDataSynchronization.Group);
                                    })
                                .ForAll(async p =>
                                {
                                    using (var memoryStream = new MemoryStream())
                                    {
                                        XmlSerializerCache.Serialize(memoryStream, o);
                                        memoryStream.Position = 0;
                                        switch (option)
                                        {
                                            case Configuration.HordeDataSynchronizationOption.Add:
                                                await
                                                    p.Value.PUT(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.CACHE)}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Group)}",
                                                        memoryStream);
                                                break;

                                            case Configuration.HordeDataSynchronizationOption.Remove:
                                                await
                                                    p.Value.DELETE(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.CACHE)}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Group)}",
                                                        memoryStream);
                                                break;
                                        }
                                    }
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_DISTRIBUTE_RESOURCE),
                            Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Group),
                            ex.PrettyPrint());
                    }
                });
        }

        private static void HordeDistributeCacheRegion(Cache.Region o,
            Configuration.HordeDataSynchronizationOption option)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.HORDE].Spawn(
                () =>
                {
                    try
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.AsParallel().Where(
                                    p =>
                                    {
                                        var peer = corradeConfiguration.HordePeers.SingleOrDefault(
                                            q => string.Equals(p.Key, q.URL, StringComparison.OrdinalIgnoreCase));
                                        return peer != null && peer
                                                   .SynchronizationMask
                                                   .IsMaskFlagSet(Configuration.HordeDataSynchronization.Region);
                                    })
                                .ForAll(async p =>
                                {
                                    using (var memoryStream = new MemoryStream())
                                    {
                                        XmlSerializerCache.Serialize(memoryStream, o);
                                        memoryStream.Position = 0;
                                        switch (option)
                                        {
                                            case Configuration.HordeDataSynchronizationOption.Add:
                                                await
                                                    p.Value.PUT(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.CACHE)}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Region)}",
                                                        memoryStream);
                                                break;

                                            case Configuration.HordeDataSynchronizationOption.Remove:
                                                await
                                                    p.Value.DELETE(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.CACHE)}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Region)}",
                                                        memoryStream);
                                                break;
                                        }
                                    }
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_DISTRIBUTE_RESOURCE),
                            Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Region),
                            ex.PrettyPrint());
                    }
                });
        }

        private static void HordeDistributeCacheAgent(Cache.Agent o,
            Configuration.HordeDataSynchronizationOption option)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.HORDE].Spawn(
                () =>
                {
                    try
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.AsParallel().Where(
                                    p =>
                                    {
                                        var peer = corradeConfiguration.HordePeers.SingleOrDefault(
                                            q => string.Equals(p.Key, q.URL, StringComparison.OrdinalIgnoreCase));
                                        return peer != null && peer
                                                   .SynchronizationMask
                                                   .IsMaskFlagSet(Configuration.HordeDataSynchronization.Agent);
                                    })
                                .ForAll(async p =>
                                {
                                    using (var memoryStream = new MemoryStream())
                                    {
                                        XmlSerializerCache.Serialize(memoryStream, o);
                                        memoryStream.Position = 0;
                                        switch (option)
                                        {
                                            case Configuration.HordeDataSynchronizationOption.Add:
                                                await
                                                    p.Value.PUT(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.CACHE)}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Agent)}",
                                                        memoryStream);
                                                break;

                                            case Configuration.HordeDataSynchronizationOption.Remove:
                                                await
                                                    p.Value.DELETE(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Enumerations.WebResource.CACHE)}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Agent)}",
                                                        memoryStream);
                                                break;
                                        }
                                    }
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_DISTRIBUTE_RESOURCE),
                            Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Agent),
                            ex.PrettyPrint());
                    }
                });
        }

        private static void HordeDistributeCacheMute(MuteEntry o, Configuration.HordeDataSynchronizationOption option)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.HORDE].Spawn(
                () =>
                {
                    try
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.AsParallel().Where(
                                    p =>
                                    {
                                        var peer = corradeConfiguration.HordePeers.SingleOrDefault(
                                            q => string.Equals(p.Key, q.URL, StringComparison.OrdinalIgnoreCase));
                                        return peer != null && peer
                                                   .SynchronizationMask
                                                   .IsMaskFlagSet(Configuration.HordeDataSynchronization.Mute);
                                    })
                                .ForAll(async p =>
                                {
                                    using (var memoryStream = new MemoryStream())
                                    {
                                        XmlSerializerCache.Serialize(memoryStream, o);
                                        memoryStream.Position = 0;
                                        switch (option)
                                        {
                                            case Configuration.HordeDataSynchronizationOption.Add:
                                                await
                                                    p.Value.PUT(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Mute)}",
                                                        memoryStream);
                                                break;

                                            case Configuration.HordeDataSynchronizationOption.Remove:
                                                await
                                                    p.Value.DELETE(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Mute)}",
                                                        memoryStream);
                                                break;
                                        }
                                    }
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_DISTRIBUTE_RESOURCE),
                            Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.Mute),
                            ex.PrettyPrint());
                    }
                });
        }

        private static void HordeDistributeGroupSoftBan(UUID groupUUID, UUID agentUUID,
            Configuration.HordeDataSynchronizationOption option)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.HORDE].Spawn(
                () =>
                {
                    try
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.AsParallel().Where(
                                    p =>
                                    {
                                        var peer = corradeConfiguration.HordePeers.SingleOrDefault(
                                            q => string.Equals(p.Key, q.URL, StringComparison.OrdinalIgnoreCase));
                                        return peer != null && peer
                                                   .SynchronizationMask.IsMaskFlagSet(
                                                       Configuration.HordeDataSynchronization.SoftBan);
                                    })
                                .ForAll(async p =>
                                {
                                    using (var memoryStream = new MemoryStream())
                                    {
                                        XmlSerializerCache.Serialize(memoryStream, agentUUID);
                                        memoryStream.Position = 0;
                                        switch (option)
                                        {
                                            case Configuration.HordeDataSynchronizationOption.Add:
                                                await
                                                    p.Value.PUT(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.SoftBan)}/{groupUUID.ToString()}",
                                                        memoryStream);
                                                break;

                                            case Configuration.HordeDataSynchronizationOption.Remove:
                                                await
                                                    p.Value.DELETE(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.SoftBan)}/{groupUUID.ToString()}",
                                                        memoryStream);
                                                break;
                                        }
                                    }
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_DISTRIBUTE_RESOURCE),
                            Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.SoftBan),
                            ex.PrettyPrint());
                    }
                });
        }

        private static void HordeDistributeConfigurationGroup(Configuration.Group group,
            Configuration.HordeDataSynchronizationOption option)
        {
            CorradeThreadPool[Threading.Enumerations.ThreadType.HORDE].Spawn(
                () =>
                {
                    try
                    {
                        lock (HordeHTTPClientsLock)
                        {
                            HordeHTTPClients.AsParallel().Where(
                                    p =>
                                    {
                                        var peer = corradeConfiguration.HordePeers.SingleOrDefault(
                                            q => string.Equals(p.Key, q.URL, StringComparison.OrdinalIgnoreCase));
                                        return peer != null && peer
                                                   .SynchronizationMask
                                                   .IsMaskFlagSet(Configuration.HordeDataSynchronization.User);
                                    })
                                .ForAll(async p =>
                                {
                                    using (var memoryStream = new MemoryStream())
                                    {
                                        XmlSerializerCache.Serialize(memoryStream, group);
                                        memoryStream.Position = 0;
                                        switch (option)
                                        {
                                            case Configuration.HordeDataSynchronizationOption.Add:
                                                await
                                                    p.Value.PUT(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.User)}/{group.UUID.ToString()}",
                                                        memoryStream);
                                                break;

                                            case Configuration.HordeDataSynchronizationOption.Remove:
                                                await
                                                    p.Value.DELETE(
                                                        $"{p.Key.TrimEnd('/')}/{Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.User)}/{group.UUID.ToString()}",
                                                        memoryStream);
                                                break;
                                        }
                                    }
                                });
                        }
                    }
                    catch (Exception ex)
                    {
                        Feedback(
                            Reflection.GetDescriptionFromEnumValue(
                                Enumerations.ConsoleMessage.UNABLE_TO_DISTRIBUTE_RESOURCE),
                            Reflection.GetNameFromEnumValue(Configuration.HordeDataSynchronization.User),
                            ex.PrettyPrint());
                    }
                });
        }

        private static void HandleGroupCacheChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    e.NewItems?.OfType<Cache.Group>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o => HordeDistributeCacheGroup(o, Configuration.HordeDataSynchronizationOption.Add));
                    break;

                case NotifyCollectionChangedAction.Remove:
                    e.OldItems?.OfType<Cache.Group>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o => HordeDistributeCacheGroup(o, Configuration.HordeDataSynchronizationOption.Remove));
                    break;
            }
        }

        private static void HandleRegionCacheChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    e.NewItems?.OfType<Cache.Region>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o => HordeDistributeCacheRegion(o, Configuration.HordeDataSynchronizationOption.Add));
                    break;

                case NotifyCollectionChangedAction.Remove:
                    e.OldItems?.OfType<Cache.Region>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o => HordeDistributeCacheRegion(o,
                            Configuration.HordeDataSynchronizationOption.Remove));
                    break;
            }
        }

        private static void HandleAgentCacheChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    e.NewItems?.OfType<Cache.Agent>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o => HordeDistributeCacheAgent(o, Configuration.HordeDataSynchronizationOption.Add));
                    break;

                case NotifyCollectionChangedAction.Remove:
                    e.OldItems?.OfType<Cache.Agent>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o => HordeDistributeCacheAgent(o, Configuration.HordeDataSynchronizationOption.Remove));
                    break;
            }
        }

        private static void HandleMuteCacheChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    e.NewItems?.OfType<MuteEntry>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o => HordeDistributeCacheMute(o, Configuration.HordeDataSynchronizationOption.Add));
                    break;

                case NotifyCollectionChangedAction.Remove:
                    e.OldItems?.OfType<MuteEntry>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o => HordeDistributeCacheMute(o, Configuration.HordeDataSynchronizationOption.Remove));
                    break;
            }
        }

        private static void HandleGroupMemberJoinPart(object sender, NotifyCollectionChangedEventArgs e)
        {
            var group =
                GroupMembers.FirstOrDefault(
                    o => ReferenceEquals(o.Value, sender as ObservableHashSet<UUID>));
            if (group.Equals(default(KeyValuePair<UUID, ObservableHashSet<UUID>>)))
                return;
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Add:
                    e.NewItems?.OfType<UUID>().ToList().AsParallel().ForAll(o =>
                    {
                        // Send membership notification if enabled.
                        if (corradeConfiguration.Groups.AsParallel()
                            .Any(
                                p =>
                                    p.UUID.Equals(group.Key) &&
                                    p.NotificationMask.IsMaskFlagSet(Configuration.Notifications.GroupMembership)))
                            CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                                () =>
                                {
                                    var agentName = string.Empty;
                                    var groupName = string.Empty;
                                    if (Resolvers.AgentUUIDToName(Client,
                                            o,
                                            corradeConfiguration.ServicesTimeout,
                                            ref agentName) &&
                                        Resolvers.GroupUUIDToName(Client, group.Key,
                                            corradeConfiguration.ServicesTimeout,
                                            ref groupName))
                                        CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                                            () => SendNotification(
                                                Configuration.Notifications.GroupMembership,
                                                new GroupMembershipEventArgs
                                                {
                                                    AgentName = agentName,
                                                    AgentUUID = o,
                                                    Action = Enumerations.Action.JOINED,
                                                    GroupName = groupName,
                                                    GroupUUID = group.Key
                                                }),
                                            corradeConfiguration.MaximumNotificationThreads);
                                });

                        var softBan = new SoftBan();
                        lock (GroupSoftBansLock)
                        {
                            if (GroupSoftBans.ContainsKey(group.Key))
                                softBan = GroupSoftBans[group.Key].AsParallel().FirstOrDefault(p => p.Agent.Equals(o));
                        }

                        // if the agent has been soft banned, eject them.
                        if (!softBan.Equals(default(SoftBan)))
                            CorradeThreadPool[Threading.Enumerations.ThreadType.SOFTBAN].Spawn(
                                () =>
                                {
                                    if (
                                        !Services.HasGroupPowers(Client, Client.Self.AgentID,
                                            group.Key,
                                            GroupPowers.Eject,
                                            corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                                            new DecayingAlarm(corradeConfiguration.DataDecayType)) ||
                                        !Services.HasGroupPowers(Client, Client.Self.AgentID,
                                            group.Key,
                                            GroupPowers.RemoveMember,
                                            corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                                            new DecayingAlarm(corradeConfiguration.DataDecayType)) ||
                                        !Services.HasGroupPowers(Client, Client.Self.AgentID,
                                            group.Key,
                                            GroupPowers.GroupBanAccess,
                                            corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                                            new DecayingAlarm(corradeConfiguration.DataDecayType)))
                                    {
                                        Feedback(
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ConsoleMessage.UNABLE_TO_APPLY_SOFT_BAN),
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ScriptError.NO_GROUP_POWER_FOR_COMMAND));
                                        return;
                                    }
                                    var targetGroup = new Group();
                                    if (
                                        !Services.RequestGroup(Client, group.Key,
                                            corradeConfiguration.ServicesTimeout,
                                            ref targetGroup))
                                        return;
                                    var GroupRoleMembersReplyEvent = new ManualResetEventSlim(false);
                                    var rolesMembers = new List<KeyValuePair<UUID, UUID>>();
                                    var requestUUID = UUID.Zero;
                                    EventHandler<GroupRolesMembersReplyEventArgs> GroupRoleMembersEventHandler =
                                        (s, args) =>
                                        {
                                            if (!requestUUID.Equals(args.RequestID) || !args.GroupID.Equals(group.Key))
                                                return;
                                            rolesMembers = args.RolesMembers;
                                            GroupRoleMembersReplyEvent.Set();
                                        };
                                    Client.Groups.GroupRoleMembersReply += GroupRoleMembersEventHandler;
                                    requestUUID = Client.Groups.RequestGroupRolesMembers(group.Key);
                                    if (
                                        !GroupRoleMembersReplyEvent.Wait((int) corradeConfiguration.ServicesTimeout))
                                    {
                                        Client.Groups.GroupRoleMembersReply -= GroupRoleMembersEventHandler;
                                        Feedback(
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ConsoleMessage.UNABLE_TO_APPLY_SOFT_BAN),
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ScriptError.TIMEOUT_GETTING_GROUP_ROLE_MEMBERS));
                                        return;
                                    }
                                    Client.Groups.GroupRoleMembersReply -= GroupRoleMembersEventHandler;
                                    switch (
                                        !rolesMembers.AsParallel()
                                            .Any(p => p.Key.Equals(targetGroup.OwnerRole) && p.Value.Equals(o)))
                                    {
                                        case true:
                                            rolesMembers.AsParallel().Where(
                                                    p => p.Value.Equals(o))
                                                .ForAll(
                                                    p => Client.Groups.RemoveFromRole(group.Key, p.Key,
                                                        o));
                                            break;

                                        default:
                                            Feedback(
                                                Reflection.GetDescriptionFromEnumValue(
                                                    Enumerations.ConsoleMessage.UNABLE_TO_APPLY_SOFT_BAN),
                                                Reflection.GetDescriptionFromEnumValue(
                                                    Enumerations.ScriptError.CANNOT_EJECT_OWNERS));
                                            return;
                                    }

                                    // No hard time requested so no need to ban.
                                    switch (softBan.Time.Equals(0))
                                    {
                                        case false:
                                            // Get current group bans.
                                            Dictionary<UUID, DateTime> bannedAgents = null;
                                            if (
                                                !Services.GetGroupBans(Client, group.Key,
                                                    corradeConfiguration.ServicesTimeout,
                                                    ref bannedAgents) || bannedAgents == null)
                                            {
                                                Feedback(
                                                    Reflection.GetDescriptionFromEnumValue(
                                                        Enumerations.ConsoleMessage.UNABLE_TO_APPLY_SOFT_BAN),
                                                    Reflection.GetDescriptionFromEnumValue(
                                                        Enumerations.ScriptError.COULD_NOT_RETRIEVE_GROUP_BAN_LIST));
                                                break;
                                            }

                                            // If the agent is not banned, then ban the agent.
                                            if (!bannedAgents.ContainsKey(o))
                                            {
                                                // Update the soft bans list.
                                                lock (GroupSoftBansLock)
                                                {
                                                    if (GroupSoftBans.ContainsKey(group.Key))
                                                    {
                                                        GroupSoftBans[group.Key].RemoveWhere(
                                                            p => p.Agent.Equals(softBan.Agent));
                                                        GroupSoftBans[group.Key].Add(new SoftBan
                                                        {
                                                            Agent = softBan.Agent,
                                                            FirstName = softBan.FirstName,
                                                            LastName = softBan.LastName,
                                                            Time = softBan.Time,
                                                            Note = softBan.Note,
                                                            Timestamp = softBan.Timestamp,
                                                            Last =
                                                                DateTime.UtcNow.ToString(
                                                                    CORRADE_CONSTANTS.DATE_TIME_STAMP)
                                                        });
                                                    }
                                                }

                                                // Do not re-add the group hard soft-ban in case it already exists.
                                                if (bannedAgents.ContainsKey(o))
                                                    break;

                                                if (wasOpenMetaverse.Helpers.IsSecondLife(Client) &&
                                                    bannedAgents.Count + 1 >
                                                    wasOpenMetaverse.Constants.GROUPS.MAXIMUM_GROUP_BANS)
                                                {
                                                    Feedback(
                                                        Reflection.GetDescriptionFromEnumValue(
                                                            Enumerations.ConsoleMessage.UNABLE_TO_APPLY_SOFT_BAN),
                                                        Reflection.GetDescriptionFromEnumValue(
                                                            Enumerations.ScriptError
                                                                .BAN_WOULD_EXCEED_MAXIMUM_BAN_LIST_LENGTH));
                                                    break;
                                                }

                                                // Now ban the agent.
                                                var GroupBanEvent = new ManualResetEventSlim(false);
                                                Client.Groups.RequestBanAction(group.Key,
                                                    GroupBanAction.Ban, new[] {o},
                                                    (s, a) => { GroupBanEvent.Set(); });
                                                if (
                                                    !GroupBanEvent.Wait((int) corradeConfiguration.ServicesTimeout))
                                                    Feedback(
                                                        Reflection.GetDescriptionFromEnumValue(
                                                            Enumerations.ConsoleMessage.UNABLE_TO_APPLY_SOFT_BAN),
                                                        Reflection.GetDescriptionFromEnumValue(
                                                            Enumerations.ScriptError
                                                                .TIMEOUT_MODIFYING_GROUP_BAN_LIST));
                                            }
                                            break;
                                    }

                                    // Now eject them.
                                    var GroupEjectEvent = new ManualResetEventSlim(false);
                                    var succeeded = false;
                                    EventHandler<GroupOperationEventArgs> GroupOperationEventHandler = (s, args) =>
                                    {
                                        if (!args.GroupID.Equals(group.Key))
                                            return;
                                        succeeded = args.Success;
                                        GroupEjectEvent.Set();
                                    };
                                    Locks.ClientInstanceGroupsLock.EnterWriteLock();
                                    Client.Groups.GroupMemberEjected += GroupOperationEventHandler;
                                    Client.Groups.EjectUser(group.Key, o);
                                    if (!GroupEjectEvent.Wait((int) corradeConfiguration.ServicesTimeout))
                                    {
                                        Client.Groups.GroupMemberEjected -= GroupOperationEventHandler;
                                        Locks.ClientInstanceGroupsLock.ExitWriteLock();
                                        Feedback(
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ConsoleMessage.UNABLE_TO_APPLY_SOFT_BAN),
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ScriptError.TIMEOUT_EJECTING_AGENT));
                                        return;
                                    }
                                    Client.Groups.GroupMemberEjected -= GroupOperationEventHandler;
                                    Locks.ClientInstanceGroupsLock.ExitWriteLock();
                                    if (!succeeded)
                                        Feedback(
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ConsoleMessage.UNABLE_TO_APPLY_SOFT_BAN),
                                            Reflection.GetDescriptionFromEnumValue(
                                                Enumerations.ScriptError.COULD_NOT_EJECT_AGENT));
                                });
                    });
                    e.OldItems?.OfType<UUID>().ToList().AsParallel().ForAll(o =>
                    {
                        // Send membership notification if enabled.
                        if (corradeConfiguration.Groups.AsParallel()
                            .Any(
                                p =>
                                    p.UUID.Equals(group.Key) &&
                                    p.NotificationMask.IsMaskFlagSet(Configuration.Notifications.GroupMembership)))
                            CorradeThreadPool[Threading.Enumerations.ThreadType.AUXILIARY].Spawn(
                                () =>
                                {
                                    var agentName = string.Empty;
                                    var groupName = string.Empty;
                                    if (Resolvers.AgentUUIDToName(Client,
                                            o,
                                            corradeConfiguration.ServicesTimeout,
                                            ref agentName) &&
                                        Resolvers.GroupUUIDToName(Client, group.Key,
                                            corradeConfiguration.ServicesTimeout,
                                            ref groupName))
                                        CorradeThreadPool[Threading.Enumerations.ThreadType.NOTIFICATION].Spawn(
                                            () => SendNotification(
                                                Configuration.Notifications.GroupMembership,
                                                new GroupMembershipEventArgs
                                                {
                                                    AgentName = agentName,
                                                    AgentUUID = o,
                                                    Action = Enumerations.Action.PARTED,
                                                    GroupName = groupName,
                                                    GroupUUID = group.Key
                                                }),
                                            corradeConfiguration.MaximumNotificationThreads);
                                });
                    });
                    break;
            }
        }

        public static void HandleGroupSoftBansChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var group =
                GroupSoftBans.FirstOrDefault(
                    o => ReferenceEquals(o.Value, sender as ObservableHashSet<UUID>));
            if (group.Equals(default(KeyValuePair<UUID, ObservableHashSet<UUID>>)))
                return;
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Add:
                case NotifyCollectionChangedAction.Reset:
                    e.NewItems?.OfType<UUID>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o =>
                        {
                            if (corradeConfiguration.EnableHorde)
                                HordeDistributeGroupSoftBan(group.Key, o,
                                    Configuration.HordeDataSynchronizationOption.Add);
                        });
                    e.OldItems?.OfType<UUID>()
                        .ToList()
                        .AsParallel()
                        .ForAll(o =>
                        {
                            if (corradeConfiguration.EnableHorde)
                                HordeDistributeGroupSoftBan(group.Key, o,
                                    Configuration.HordeDataSynchronizationOption.Remove);
                        });
                    break;
            }
        }

        private static void HandleSynBotLearning(object sender, LearningEventArgs e)
        {
            try
            {
                e.Document.Save(Path.Combine(
                    Directory.GetCurrentDirectory(), SIML_BOT_CONSTANTS.ROOT_DIRECTORY,
                    SIML_BOT_CONSTANTS.EVOLVE_DIRECTORY,
                    SIML_BOT_CONSTANTS.LEARNED_FILE));
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SAVING_SIML_BOT_LEARNING_FILE),
                    ex.PrettyPrint());
            }
        }

        private static void HandleSynBotMemorizing(object sender, MemorizingEventArgs e)
        {
            try
            {
                e.Document.Save(Path.Combine(
                    Directory.GetCurrentDirectory(), SIML_BOT_CONSTANTS.ROOT_DIRECTORY,
                    SIML_BOT_CONSTANTS.EVOLVE_DIRECTORY,
                    SIML_BOT_CONSTANTS.MEMORIZED_FILE));
            }
            catch (Exception ex)
            {
                Feedback(
                    Reflection.GetDescriptionFromEnumValue(
                        Enumerations.ConsoleMessage.ERROR_SAVING_SIML_BOT_MEMORIZING_FILE),
                    ex.PrettyPrint());
            }
        }
    }

    public class NativeMethods
    {
        public enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT,
            CTRL_CLOSE_EVENT,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT
        }

        // Console quick edit mode.
        const uint ENABLE_QUICK_EDIT = 0x0040;

        // STD_INPUT_HANDLE (DWORD): -10 is the standard input device.
        const int STD_INPUT_HANDLE = -10;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
        public static bool SetCorradeConsole()
        {
            IntPtr consoleHandle = GetStdHandle(STD_INPUT_HANDLE);

            // get current console mode
            uint consoleMode;
            if (!GetConsoleMode(consoleHandle, out consoleMode))
            {
                // ERROR: Unable to get console mode.
                return false;
            }

            // Clear the quick edit bit in the mode flags
            consoleMode &= ~ENABLE_QUICK_EDIT;

            // set the new mode
            if (!SetConsoleMode(consoleHandle, consoleMode))
            {
                // ERROR: Unable to set console mode
                return false;
            }

            return true;
        }

        // Import SetThreadExecutionState Win32 API and necessary flags
        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;
        const uint ES_AWAYMODE_REQUIRED = 0x00000040;

        public static bool PreventCorradeSuspend()
        {
            var result = SetThreadExecutionState(
                ES_CONTINUOUS |
                ES_SYSTEM_REQUIRED |
                ES_AWAYMODE_REQUIRED);

            return !result.Equals(0);
        }

        /// <summary>
        ///     Import console handler for windows.
        /// </summary>
        [DllImport("Kernel32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetConsoleCtrlHandler(Corrade.EventHandler handler,
            [MarshalAs(UnmanagedType.U1)] bool add);
    }
}