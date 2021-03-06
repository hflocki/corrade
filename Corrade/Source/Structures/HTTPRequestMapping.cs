///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2016 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System;

namespace Corrade.Structures
{
    public class HTTPRequestMapping : Attribute
    {
        public string Map;
        public string Method;

        public HTTPRequestMapping(string s, string m)
        {
            Map = s;
            Method = m;
        }
    }
}
