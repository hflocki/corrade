///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenMetaverse;

namespace Corrade
{
    public partial class Corrade
    {
        public partial class CorradeCommands
        {
            public static Action<CorradeCommandParameters, Dictionary<string, string>> getgroupaccountsummarydata =
                (corradeCommandParameters, result) =>
                {
                    if (!HasCorradePermission(corradeCommandParameters.Group.Name, (int) Permissions.Group))
                    {
                        throw new ScriptException(ScriptError.NO_CORRADE_PERMISSIONS);
                    }

                    int days;
                    if (
                        !int.TryParse(
                            wasInput(wasKeyValueGet(
                                wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.DAYS)),
                                corradeCommandParameters.Message)),
                            out days))
                    {
                        throw new ScriptException(ScriptError.INVALID_DAYS);
                    }
                    int interval;
                    if (
                        !int.TryParse(
                            wasInput(
                                wasKeyValueGet(wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.INTERVAL)),
                                    corradeCommandParameters.Message)),
                            out interval))
                    {
                        throw new ScriptException(ScriptError.INVALID_INTERVAL);
                    }
                    ManualResetEvent RequestGroupAccountSummaryEvent = new ManualResetEvent(false);
                    GroupAccountSummary summary = new GroupAccountSummary();
                    EventHandler<GroupAccountSummaryReplyEventArgs> RequestGroupAccountSummaryEventHandler =
                        (sender, args) =>
                        {
                            summary = args.Summary;
                            RequestGroupAccountSummaryEvent.Set();
                        };
                    lock (ClientInstanceGroupsLock)
                    {
                        Client.Groups.GroupAccountSummaryReply += RequestGroupAccountSummaryEventHandler;
                        Client.Groups.RequestGroupAccountSummary(corradeCommandParameters.Group.UUID, days, interval);
                        if (
                            !RequestGroupAccountSummaryEvent.WaitOne((int) corradeConfiguration.ServicesTimeout,
                                false))
                        {
                            Client.Groups.GroupAccountSummaryReply -= RequestGroupAccountSummaryEventHandler;
                            throw new ScriptException(ScriptError.TIMEOUT_GETTING_GROUP_ACCOUNT_SUMMARY);
                        }
                        Client.Groups.GroupAccountSummaryReply -= RequestGroupAccountSummaryEventHandler;
                    }
                    List<string> data = new List<string>(GetStructuredData(summary,
                        wasInput(wasKeyValueGet(wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.DATA)),
                            corradeCommandParameters.Message)))
                        );
                    if (data.Any())
                    {
                        result.Add(wasGetDescriptionFromEnumValue(ResultKeys.DATA),
                            wasEnumerableToCSV(data));
                    }
                };
        }
    }
}