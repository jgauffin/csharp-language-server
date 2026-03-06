namespace LibA;

/// <summary>Base animal abstraction.</summary>
public interface IAnimal
{
    /// <summary>Makes the animal speak.</summary>
    /// <returns>The sound the animal makes.</returns>
    string Speak();

    string Name { get; }
}

/// <summary>Abstract base with shared name logic.</summary>
public abstract class AnimalBase : IAnimal
{
    public string Name { get; }

    protected AnimalBase(string name)
    {
        Name = name;
    }

    public abstract string Speak();

    /// <summary>Returns a formatted greeting.</summary>
    public string Greet() => $"Hi, I'm {Name}";
}
