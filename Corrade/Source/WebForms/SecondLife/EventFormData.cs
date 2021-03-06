///////////////////////////////////////////////////////////////////////////
//  Copyright (C) Wizardry and Steamworks 2016 - License: GNU GPLv3      //
//  Please see: http://www.gnu.org/licenses/gpl.html for legal details,  //
//  rights of fair usage, the disclaimer and warranty conditions.        //
///////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

namespace Corrade.WebForms.SecondLife
{
    /// <summary>
    ///     Form data for Second Life events.
    /// </summary>
    public class EventFormData
    {
        public Dictionary<string, uint> Category;
        public Dictionary<string, uint> Duration;
        public Dictionary<string, string> Location;
        public Dictionary<string, string> Time;

        public EventFormData()
        {
            Location = new Dictionary<string, string>();
            Duration = new Dictionary<string, uint>();
            Time = new Dictionary<string, string>();
            Category = new Dictionary<string, uint>();
        }
    }
}
