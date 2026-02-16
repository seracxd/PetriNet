using Core.Models;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Geometry;

namespace PetriNetAnalyzer.DiagramModels
{
    public class PlaceNode : NodeModel
    {
        public Place Data { get; } 
        public PlaceNode(Place place) : base()
        {
            Data = place;            
            Title = place.Name;
            Size = new Size(place.Width, place.Height);

            AddPort(PortAlignment.Top);    // 0
            AddPort(PortAlignment.Right);  // 1 (45)
            AddPort(PortAlignment.Right);  // 2 (90)
            AddPort(PortAlignment.Bottom); // 3 (135)
            AddPort(PortAlignment.Bottom); // 4 (180)
            AddPort(PortAlignment.Left);   // 5 (225)
            AddPort(PortAlignment.Left);   // 6 (270)
            AddPort(PortAlignment.Top);    // 7 (315)
        }
    }
}
