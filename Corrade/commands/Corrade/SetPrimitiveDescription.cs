///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CorradeConfiguration;
using OpenMetaverse;
using wasOpenMetaverse;
using wasSharp;
using Helpers = wasOpenMetaverse.Helpers;

namespace Corrade
{
    public partial class Corrade
    {
        public partial class CorradeCommands
        {
            public static Action<CorradeCommandParameters, Dictionary<string, string>> setprimitivedescription =
                (corradeCommandParameters, result) =>
                {
                    if (
                        !HasCorradePermission(corradeCommandParameters.Group.Name,
                            (int) Configuration.Permissions.Interact))
                    {
                        throw new ScriptException(ScriptError.NO_CORRADE_PERMISSIONS);
                    }
                    float range;
                    if (
                        !float.TryParse(
                            wasInput(KeyValue.Get(
                                wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.RANGE)),
                                corradeCommandParameters.Message)),
                            out range))
                    {
                        range = corradeConfiguration.Range;
                    }
                    Primitive primitive = null;
                    if (
                        !Services.FindPrimitive(Client,
                            Helpers.StringOrUUID(wasInput(KeyValue.Get(
                                wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.ITEM)),
                                corradeCommandParameters.Message))),
                            range, corradeConfiguration.Range,
                            ref primitive, corradeConfiguration.ServicesTimeout, corradeConfiguration.DataTimeout,
                            new Time.DecayingAlarm(corradeConfiguration.DataDecayType)))
                    {
                        throw new ScriptException(ScriptError.PRIMITIVE_NOT_FOUND);
                    }
                    string description =
                        wasInput(
                            KeyValue.Get(
                                wasOutput(Reflection.GetNameFromEnumValue(ScriptKeys.DESCRIPTION)),
                                corradeCommandParameters.Message));
                    if (string.IsNullOrEmpty(description))
                    {
                        throw new ScriptException(ScriptError.NO_DESCRIPTION_PROVIDED);
                    }
                    if (Helpers.IsSecondLife(Client) &&
                        Encoding.UTF8.GetByteCount(description) >
                        Constants.PRIMITIVES.MAXIMUM_DESCRIPTION_SIZE)
                    {
                        throw new ScriptException(ScriptError.DESCRIPTION_TOO_LARGE);
                    }
                    Client.Objects.SetDescription(
                        Client.Network.Simulators.AsParallel()
                            .FirstOrDefault(o => o.Handle.Equals(primitive.RegionHandle)),
                        primitive.LocalID, description);
                };
        }
    }
}