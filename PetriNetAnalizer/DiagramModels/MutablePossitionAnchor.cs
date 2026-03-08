using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models.Base;

namespace PetriNetAnalyzer.DiagramModels
{
    /// <summary>
    /// A position anchor whose position can be updated while dragging.
    /// Wraps PositionAnchor if it has a public setter, otherwise reimplements.
    /// </summary>
    public sealed class MutablePositionAnchor : Anchor
    {
        private Point _position;

        public MutablePositionAnchor(Point position) : base(null)
        {
            _position = position;
        }

        public void SetPosition(Point p) => _position = p;

        public override Point? GetPosition(BaseLinkModel link, Point[] route)
            => _position;

        public override Point? GetPlainPosition()
            => _position;
    }
}