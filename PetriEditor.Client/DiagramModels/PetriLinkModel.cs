using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Core.Models;

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

        /// <summary>
        /// Index of the arc segment the weight label is anchored to.
        /// -1 means "auto" (middle segment). Updated when user drags the label.
        /// </summary>
        public int WeightLabelSegment { get; set; } = -1;

        /// <summary>
        /// Which side of the arc the label sits on.
        /// False = left-hand perpendicular, True = right-hand perpendicular.
        /// </summary>
        public bool WeightLabelFlipped { get; set; } = false;

        /// <summary>Arc type: Normal, Inhibitor, or Cancellation.</summary>
        public ArcType ArcType { get; set; } = ArcType.Normal;

        // Z.Blazor.Diagrams' LinkModel ctor is typed as non-nullable Anchor but accepts
        // null in practice — that's how pending/dragging links are represented before the
        // user drops on a target. The null-forgiving operator suppresses the flow warning.
        public PetriLinkModel(Anchor sourceAnchor, Anchor? targetAnchor = null)
            : base(sourceAnchor, targetAnchor!) { }
    }
}