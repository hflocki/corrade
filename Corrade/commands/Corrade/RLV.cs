///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

namespace Corrade
{
    public partial class Corrade
    {
        public partial class CorradeCommands
        {
            public static Action<CorradeCommandParameters, Dictionary<string, string>> rlv =
                (corradeCommandParameters, result) =>
                {
                    if (!HasCorradePermission(corradeCommandParameters.Group.Name, (int) Permissions.System))
                    {
                        throw new ScriptException(ScriptError.NO_CORRADE_PERMISSIONS);
                    }
                    switch (wasGetEnumValueFromDescription<Action>(
                        wasInput(
                            wasKeyValueGet(wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION)),
                                corradeCommandParameters.Message)).ToLowerInvariant()))
                    {
                        case Action.ENABLE:
                            corradeConfiguration.EnableRLV = true;
                            break;
                        case Action.DISABLE:
                            corradeConfiguration.EnableRLV = false;
                            lock (RLVRulesLock)
                            {
                                RLVRules.Clear();
                            }
                            break;
                    }
                };
        }
    }
}