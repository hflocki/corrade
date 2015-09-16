///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2013 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Corrade
{
    public partial class Corrade
    {
        public partial class CorradeCommands
        {
            public static Action<CorradeCommandParameters, Dictionary<string, string>> database =
                (corradeCommandParameters, result) =>
                {
                    if (!HasCorradePermission(corradeCommandParameters.Group.Name, (int) Permissions.Database))
                    {
                        throw new ScriptException(ScriptError.NO_CORRADE_PERMISSIONS);
                    }

                    if (string.IsNullOrEmpty(corradeCommandParameters.Group.DatabaseFile))
                    {
                        throw new ScriptException(ScriptError.NO_DATABASE_FILE_CONFIGURED);
                    }
                    if (!File.Exists(corradeCommandParameters.Group.DatabaseFile))
                    {
                        // create the file and close it
                        File.Create(corradeCommandParameters.Group.DatabaseFile).Close();
                    }
                    switch (
                        wasGetEnumValueFromDescription<Action>(
                            wasInput(
                                wasKeyValueGet(wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.ACTION)),
                                    corradeCommandParameters.Message)).ToLowerInvariant()))
                    {
                        case Action.GET:
                            string databaseGetkey =
                                wasInput(
                                    wasKeyValueGet(wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.KEY)),
                                        corradeCommandParameters.Message));
                            if (string.IsNullOrEmpty(databaseGetkey))
                            {
                                throw new ScriptException(ScriptError.NO_DATABASE_KEY_SPECIFIED);
                            }
                            lock (DatabaseFileLock)
                            {
                                if (!DatabaseLocks.ContainsKey(corradeCommandParameters.Group.Name))
                                {
                                    DatabaseLocks.Add(corradeCommandParameters.Group.Name, new object());
                                }
                            }
                            lock (DatabaseLocks[corradeCommandParameters.Group.Name])
                            {
                                string databaseGetValue = wasKeyValueGet(databaseGetkey,
                                    File.ReadAllText(corradeCommandParameters.Group.DatabaseFile, Encoding.UTF8));
                                if (!string.IsNullOrEmpty(databaseGetValue))
                                {
                                    result.Add(databaseGetkey,
                                        wasInput(databaseGetValue));
                                }
                            }
                            lock (DatabaseFileLock)
                            {
                                if (DatabaseLocks.ContainsKey(corradeCommandParameters.Group.Name))
                                {
                                    DatabaseLocks.Remove(corradeCommandParameters.Group.Name);
                                }
                            }
                            break;
                        case Action.SET:
                            string databaseSetKey =
                                wasInput(
                                    wasKeyValueGet(wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.KEY)),
                                        corradeCommandParameters.Message));
                            if (string.IsNullOrEmpty(databaseSetKey))
                            {
                                throw new ScriptException(ScriptError.NO_DATABASE_KEY_SPECIFIED);
                            }
                            string databaseSetValue =
                                wasInput(
                                    wasKeyValueGet(wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.VALUE)),
                                        corradeCommandParameters.Message));
                            if (string.IsNullOrEmpty(databaseSetValue))
                            {
                                throw new ScriptException(ScriptError.NO_DATABASE_VALUE_SPECIFIED);
                            }
                            lock (DatabaseFileLock)
                            {
                                if (!DatabaseLocks.ContainsKey(corradeCommandParameters.Group.Name))
                                {
                                    DatabaseLocks.Add(corradeCommandParameters.Group.Name, new object());
                                }
                            }
                            lock (DatabaseLocks[corradeCommandParameters.Group.Name])
                            {
                                string contents = File.ReadAllText(corradeCommandParameters.Group.DatabaseFile,
                                    Encoding.UTF8);
                                using (
                                    StreamWriter recreateDatabase =
                                        new StreamWriter(corradeCommandParameters.Group.DatabaseFile,
                                            false, Encoding.UTF8))
                                {
                                    recreateDatabase.Write(wasKeyValueSet(databaseSetKey,
                                        databaseSetValue, contents));
                                    recreateDatabase.Flush();
                                    //recreateDatabase.Close();
                                }
                            }
                            lock (DatabaseFileLock)
                            {
                                if (DatabaseLocks.ContainsKey(corradeCommandParameters.Group.Name))
                                {
                                    DatabaseLocks.Remove(corradeCommandParameters.Group.Name);
                                }
                            }
                            break;
                        case Action.DELETE:
                            string databaseDeleteKey =
                                wasInput(
                                    wasKeyValueGet(wasOutput(wasGetDescriptionFromEnumValue(ScriptKeys.KEY)),
                                        corradeCommandParameters.Message));
                            if (string.IsNullOrEmpty(databaseDeleteKey))
                            {
                                throw new ScriptException(ScriptError.NO_DATABASE_KEY_SPECIFIED);
                            }
                            lock (DatabaseFileLock)
                            {
                                if (!DatabaseLocks.ContainsKey(corradeCommandParameters.Group.Name))
                                {
                                    DatabaseLocks.Add(corradeCommandParameters.Group.Name, new object());
                                }
                            }
                            lock (DatabaseLocks[corradeCommandParameters.Group.Name])
                            {
                                string contents = File.ReadAllText(corradeCommandParameters.Group.DatabaseFile,
                                    Encoding.UTF8);
                                using (
                                    StreamWriter recreateDatabase =
                                        new StreamWriter(corradeCommandParameters.Group.DatabaseFile,
                                            false, Encoding.UTF8))
                                {
                                    recreateDatabase.Write(wasKeyValueDelete(databaseDeleteKey, contents));
                                    recreateDatabase.Flush();
                                    //recreateDatabase.Close();
                                }
                            }
                            lock (DatabaseFileLock)
                            {
                                if (DatabaseLocks.ContainsKey(corradeCommandParameters.Group.Name))
                                {
                                    DatabaseLocks.Remove(corradeCommandParameters.Group.Name);
                                }
                            }
                            break;
                        default:
                            throw new ScriptException(ScriptError.UNKNOWN_DATABASE_ACTION);
                    }
                };
        }
    }
}