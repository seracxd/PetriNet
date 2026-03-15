using Core.Models;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Geometry;
using PetriNetAnalyzer.Services;

namespace PetriNetAnalyzer.DiagramModels
{
    public class TransitionNode : NodeModel
    {
        public Transition Data { get; }

        public TransitionNode(Transition transition, DiagramSettings? settings = null) : base()
        {
            Data = transition;
            Title = transition.Name;

            var w = settings?.TransitionWidth ?? 20.0;
            var h = settings?.TransitionHeight ?? 60.0;

            transition.Width = w;
            transition.Height = h;

            Size = new Size(w, h);

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