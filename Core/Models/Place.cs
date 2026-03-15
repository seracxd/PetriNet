using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Models
{
    public class Place : PetriNetNode
    {
        public int Tokens { get; set; } = 0;

        public Place()
        {
            Name = "P";
            // Width and Height are intentionally NOT set here.
            // They are assigned from DiagramSettings when PlaceNode is constructed,
            // so all nodes (including those restored by undo/redo) use the current
            // configured size rather than a hardcoded default.
        }
    }
}