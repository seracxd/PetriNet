namespace Core.Models
{
    public enum ArcType
    {
        Normal,
        Inhibitor,
        Reset
    }

    public class Arc
    {
        public string SourceId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public int Weight { get; set; } = 1;
        public ArcType ArcType { get; set; } = ArcType.Normal;
    }
}
