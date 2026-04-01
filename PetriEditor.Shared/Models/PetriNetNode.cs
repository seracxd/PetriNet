namespace Core.Models
{
    public abstract class PetriNetNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }

        public double Width { get; set; } = 60;
        public double Height { get; set; } = 60;
    }
}
