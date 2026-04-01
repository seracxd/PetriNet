using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace PetriNetAnalyzer.DiagramModels
{
    public class PetriVertexModel : LinkVertexModel
    {
        public PetriVertexModel(LinkModel parent, Point? position = null) : base(parent, position)
        {
        }
    }
}
