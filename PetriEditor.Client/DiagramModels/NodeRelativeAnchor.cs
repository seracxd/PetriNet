using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace PetriNetAnalyzer.DiagramModels
{
    /// <summary>
    /// An anchor that attaches at a fixed offset relative to the node's position,
    /// so the endpoint follows the node when it is moved.
    /// </summary>
    public sealed class NodeRelativeAnchor : Anchor
    {
        private readonly NodeModel _node;
        private readonly double _offsetX;
        private readonly double _offsetY;

        /// <param name="node">The node this anchor belongs to.</param>
        /// <param name="absolutePos">The absolute canvas position of the click — converted to node-relative internally.</param>
        public NodeRelativeAnchor(NodeModel node, Point absolutePos) : base(node)
        {
            _node = node;
            _offsetX = absolutePos.X - (node.Position?.X ?? 0);
            _offsetY = absolutePos.Y - (node.Position?.Y ?? 0);
        }

        public override Point? GetPosition(BaseLinkModel link, Point[] route)
            => GetPlainPosition();

        public override Point? GetPlainPosition()
        {
            var pos = _node.Position;
            if (pos == null) return null;
            return new Point(pos.X + _offsetX, pos.Y + _offsetY);
        }
    }
}
