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
        /// </summary>
        public int Priority { get; set; } = 0;

        public Transition()
        {
            Name = "T";
            // Width and Height are intentionally NOT set here.
            // They are assigned from DiagramSettings when TransitionNode is constructed,
            // so all nodes (including those restored by undo/redo) use the current
            // configured size rather than a hardcoded default.
        }
    }
}