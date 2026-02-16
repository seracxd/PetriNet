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
    }
}
