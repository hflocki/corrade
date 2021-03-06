///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2016 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;
using OpenMetaverse;

namespace Corrade.Structures
{
    /// <summary>
    ///     A structure for holding local messages.
    /// </summary>
    [Serializable]
    public struct LocalMessage
    {
        public ChatType ChatType;
        public DateTime DateTime;
        public string FirstName;
        public string LastName;
        public string Message;
        public string RegionName;
    }
}
