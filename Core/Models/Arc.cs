using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Models
{
    public enum ArcType
    {
        Normal,
        Inhibitor,
        Cancellation
    }


    public class Arc
    {
        public string SourceId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public int Weight { get; set; } = 1;
    }
}
