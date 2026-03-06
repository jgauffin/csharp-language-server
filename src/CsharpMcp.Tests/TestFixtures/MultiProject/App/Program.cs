using LibA;
using LibB;

namespace App;

public class Program
{
    public static void Main()
    {
        var dog = new Dog("Rex");
        var sound = dog.Speak();
        var greeting = dog.Greet();

        var calc = new Calculator();
        var result = calc.Add(1, 2);
        var product = calc.Multiply(3, 4);

        var math = new DogMath();
        var legs = math.AddLegs(2);
    }
}
