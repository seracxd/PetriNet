using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;

namespace PetriNetAnalyzer.DiagramModels
{
    public class PetriLinkModel : LinkModel
    {
        public PetriLinkModel(Anchor sourceAnchor, Anchor targetAnchor = null)
             : base(sourceAnchor, targetAnchor) { }
        public bool IsAdjustingSource { get; set; }
        public int Weight { get; set; } = 1;
    }
}
