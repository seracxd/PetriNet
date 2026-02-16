using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Controls.Default;
using Blazor.Diagrams.Core.Events;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Blazor.Diagrams.Core.Geometry;

namespace PetriNetAnalyzer.DiagramModels
{
    public class PetriArrowControl : ArrowHeadControl
    {

        public bool IsSourceControl { get; }
        public PetriArrowControl(bool source, LinkMarker? marker = null) : base(source, marker)
        {
            IsSourceControl = source;
        }

        public override async ValueTask OnPointerDown(Diagram diagram, Model model, PointerEventArgs e)
        {
            if (model is not LinkModel link) return;

            if (Source && link.Vertices.Count > 0)
            {
                var reversedVertices = link.Vertices.AsEnumerable().Reverse().ToList();

                link.Vertices.Clear();
                foreach (var v in reversedVertices)
                {
                    link.Vertices.Add(v);
                }
            }

            await base.OnPointerDown(diagram, model, e);
            link.Refresh();
        }
    }
}