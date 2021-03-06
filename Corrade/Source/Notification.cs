///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2016 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Xml.Serialization;
using CorradeConfigurationSharp;
using OpenMetaverse;
using wasSharp;
using wasSharp.Collections.Generic;

namespace Corrade
{
    /// <summary>
    ///     A Corrade notification.
    /// </summary>
    [Serializable]
    public class Notifications
    {
        public SerializableDictionary<string, string> Afterburn;

        public HashSet<string> Data;
        public string GroupName;
        public UUID GroupUUID;

        /// <summary>
        ///     Holds TCP notification destinations.
        /// </summary>
        /// <remarks>These are state dependant so they do not have to be serialized.</remarks>
        [XmlIgnore]
        public Dictionary<Configuration.Notifications, HashSet<IPEndPoint>> NotificationTCPDestination;

        //public SerializableDictionary<Configuration.Notifications, SerializableDictionary<string, string>>
        //    NotificationHTTPDestination;

        public SerializableDictionary<Configuration.Notifications,
            SerializableDictionary<string, HashSet<string>>> HTTPNotifications;

        public Configuration.Notifications NotificationMask
            => (HTTPNotifications != null && HTTPNotifications.Any()
                ? HTTPNotifications.Keys.CreateMask()
                : Configuration.Notifications.NONE) |
               (NotificationTCPDestination != null && NotificationTCPDestination.Any()
                   ? NotificationTCPDestination.Keys.CreateMask()
                   : Configuration.Notifications.NONE);
    }

    /// <summary>
    ///     Notification parameters.
    /// </summary>
    public struct NotificationParameters
    {
        [Reflection.NameAttribute("event")]
        public object Event;

        [Reflection.NameAttribute("notification")]
        public Notifications Notification;

        [Reflection.NameAttribute("type")]
        public Configuration.Notifications Type;
    }

    /// <summary>
    ///     An element from the notification queue waiting to be dispatched.
    /// </summary>
    public struct NotificationQueueElement
    {
        public IEnumerable<KeyValuePair<string, string>> Message;
        public string URL;
        public UUID GroupUUID;
    }

    public struct NotificationTCPQueueElement
    {
        public IPEndPoint IPEndPoint;
        public IEnumerable<KeyValuePair<string, string>> Message;
    }
}
