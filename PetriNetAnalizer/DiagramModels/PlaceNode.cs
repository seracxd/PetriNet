using Core.Models;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Geometry;
using PetriNetAnalyzer.Services;

namespace PetriNetAnalyzer.DiagramModels
{
    public class PlaceNode : NodeModel
    {
        public Place Data { get; }

        public PlaceNode(Place place, DiagramSettings? settings = null) : base()
        {
            Data = place;
            Title = place.Name;

            var size = settings?.PlaceSize ?? 60.0;

            // Write size back onto the domain object so it round-trips correctly
            // if the model is ever serialised/deserialised.
            place.Width = size;
            place.Height = size;

            Size = new Size(size, size);

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