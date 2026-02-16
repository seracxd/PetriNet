using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Models
{
    public class Transition : PetriNetNode
    {
        public Transition()
        {
            Name = "T";
            Width = 20;
            Height = 60;
        }
    }
}
