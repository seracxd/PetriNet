using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace PetriNetAnalyzer.DiagramModels
{
    public class PetriLinkModel : LinkModel
    {
        /// <summary>The node ID that is the canonical arrow-tail (source).</summary>
        public string? CanonicalSourceId { get; set; }

        /// <summary>True while an arrow-head drag is in progress.</summary>
        public bool IsDraggingEndpoint { get; set; }

        /// <summary>
        /// Snapshot of Source anchor taken at drag-start.
        /// Used to restore the link if the drag is cancelled (dropped outside any node).
        /// </summary>
        public Anchor? SnapshotSource { get; set; }

        /// <summary>
        /// Snapshot of Target anchor taken at drag-start.
        /// </summary>
        public Anchor? SnapshotTarget { get; set; }

        public int Weight { get; set; } = 1;
        public Point WeightLabelOffset { get; set; } = new(0, -14);

        public PetriLinkModel(Anchor sourceAnchor, Anchor? targetAnchor = null)
            : base(sourceAnchor, targetAnchor) { }
    }
}