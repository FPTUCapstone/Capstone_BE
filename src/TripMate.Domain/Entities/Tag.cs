namespace TripMate.Domain.Entities;

public class Tag
{
    private Tag()
    {
    }

    public int Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public static Tag Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tag name is required.", nameof(name));
        }

        var normalized = name.Trim();
        if (normalized.Length > 60)
        {
            throw new ArgumentException("Tag name cannot exceed 60 characters.", nameof(name));
        }

        return new Tag { Name = normalized };
    }
}