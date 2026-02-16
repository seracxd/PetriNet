using Core.Models;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Geometry;

namespace PetriNetAnalyzer.DiagramModels
{
    public class TransitionNode : NodeModel
    {
        public Transition Data { get; }
        public TransitionNode(Transition transition) : base()
        {
            Data = transition;
            Title = transition.Name;
            Size = new Size(transition.Width, transition.Height);
         

            AddPort(PortAlignment.Left);
            AddPort(PortAlignment.Left);
            AddPort(PortAlignment.Left);

            AddPort(PortAlignment.Right);
            AddPort(PortAlignment.Right);
            AddPort(PortAlignment.Right);

            AddPort(PortAlignment.Top);
            AddPort(PortAlignment.Bottom);
        }
    }
}

