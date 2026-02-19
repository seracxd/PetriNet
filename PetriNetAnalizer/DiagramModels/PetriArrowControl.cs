using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Controls.Default;
using Blazor.Diagrams.Core.Events;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

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
            if (model is not PetriLinkModel link) return;

            if (Source)
            {
                link.IsAdjustingSource = true;

                if (link.Vertices.Count > 0)
                {
                    var reversed = link.Vertices.AsEnumerable().Reverse().ToList();
                    link.Vertices.Clear();
                    foreach (var v in reversed) link.Vertices.Add(v);
                }
            }
            else
            {
                link.IsAdjustingSource = false;
            }

            await base.OnPointerDown(diagram, model, e);
        }
    }
}