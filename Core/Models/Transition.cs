using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Models
{
    public class Transition : PetriNetNode
    {
        /// <summary>
        /// Firing priority. 0 = default (no priority badge shown).
        /// Higher value = higher priority over transitions with lower values.
        /// Any transition with a priority set fires before those without one.
        /// </summary>
        public int Priority { get; set; } = 0;

        public Transition()
        {
            Name = "T";
            Width = 20;
            Height = 60;
        }
    }
}