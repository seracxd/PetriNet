using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Models
{
    public class Place : PetriNetNode
    {
        public int Tokens { get; set; } = 1;

        public Place()
        {
            Name = "P";
            Width = 60;
            Height = 60;
        }
    }
}
