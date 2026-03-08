using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace PetriNetAnalyzer.DiagramModels
{
    public class PetriLinkModel : LinkModel
    {
        public bool IsAdjustingSource { get; set; }
        public int Weight { get; set; } = 1;
        public Point WeightLabelOffset { get; set; } = new(0, -14);


        public PetriLinkModel(Anchor sourceAnchor, Anchor targetAnchor = null)
             : base(sourceAnchor, targetAnchor) { }
     
    }
}
