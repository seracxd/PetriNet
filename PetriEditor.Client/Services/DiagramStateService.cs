namespace PetriNetAnalyzer.Services
{
    public class DiagramStateService
    {
        public enum ToolType { Select, Arc, Delete }

        private ToolType _selectedTool = ToolType.Select;
        public ToolType SelectedTool
        {
            get => _selectedTool;
            set
            {
                if (_selectedTool != value)
                {
                    _selectedTool = value;
                    OnToolChanged?.Invoke();
                }
            }
        }

        public event Action? OnToolChanged;

        public bool IsArcToolActive => SelectedTool == ToolType.Arc;

        public void SelectTool(ToolType tool) => SelectedTool = tool;

        // ── Simulation lock ───────────────────────────────────────────────

        private bool _isSimulating;
        public bool IsSimulating
        {
            get => _isSimulating;
            set
            {
                if (_isSimulating != value)
                {
                    _isSimulating = value;
                    OnSimulationChanged?.Invoke();
                }
            }
        }

        public event Action? OnSimulationChanged;

        // ── Marking preview (analysis hover) ─────────────────────────────

        private bool _isPreviewingMarking;
        public bool IsPreviewingMarking
        {
            get => _isPreviewingMarking;
            set
            {
                _isPreviewingMarking = value;
                OnMarkingPreviewChanged?.Invoke(); // always fire — tokens may have changed between same hover nodes
            }
        }

        /// <summary>Fired whenever a marking preview starts, updates, or ends. Always fires even if IsPreviewingMarking didn't change.</summary>
        public event Action? OnMarkingPreviewChanged;

        // ── Analysis highlight ────────────────────────────────────────────

        /// <summary>Domain IDs of places/transitions currently highlighted from the analysis panel.</summary>
        public HashSet<string> HighlightedNodeIds { get; } = [];

        /// <summary>
        /// Highlights the given node IDs in the diagram.
        /// Pass empty enumerables to clear all highlights.
        /// </summary>
        public void SetHighlight(IEnumerable<string> nodeIds)
        {
            HighlightedNodeIds.Clear();
            foreach (var id in nodeIds)
                HighlightedNodeIds.Add(id);
            OnHighlightChanged?.Invoke();
        }

        public void ClearHighlight()
        {
            if (HighlightedNodeIds.Count == 0) return;
            HighlightedNodeIds.Clear();
            OnHighlightChanged?.Invoke();
        }

        public event Action? OnHighlightChanged;
    }
}