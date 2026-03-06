using LibA;

namespace LibB;

/// <summary>A dog that speaks.</summary>
public class Dog : AnimalBase
{
    public Dog(string name) : base(name) { }

    public override string Speak() => "Woof";
}

/// <summary>Uses Calculator from LibA.</summary>
public class DogMath
{
    private readonly Calculator _calculator = new();

    public int AddLegs(int otherLegs) => _calculator.Add(4, otherLegs);
}
