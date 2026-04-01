// PetriWeightControl is no longer used — weight label is rendered directly
// in PetriLinkWidget so it is always visible (not selection-gated).
using Blazor.Diagrams.Core.Controls;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models.Base;

namespace PetriNetAnalyzer.Components.Widgets;

public class PetriWeightControl : Control
{
    public override Point? GetPosition(Model model) => null;
}