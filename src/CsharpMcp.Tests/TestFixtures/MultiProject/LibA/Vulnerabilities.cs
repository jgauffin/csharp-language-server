using System;
using System.IO;

namespace LibA;

/// <summary>Contains code with known ISO 5055 violations for testing.</summary>
public class Vulnerabilities
{
    // CWE-242: Unsafe code usage
    public unsafe int UnsafePointer(int value)
    {
        int* p = &value;
        return *p + 1;
    }

    // Reliability: virtual call in constructor
    public class BadBase
    {
        public BadBase()
        {
            Initialize(); // virtual call in ctor
        }
        public virtual void Initialize() { }
    }

    // Reliability: stack trace destruction
    public void DestroyStackTrace()
    {
        try
        {
            File.ReadAllText("nonexistent.txt");
        }
        catch (Exception ex)
        {
            throw ex; // should be 'throw;'
        }
    }

    // Reliability: empty catch block / swallowed exception
    public void SwallowException()
    {
        try
        {
            File.ReadAllText("nonexistent.txt");
        }
        catch (Exception)
        {
        }
    }

    // Maintainability: deeply nested code
    public int DeeplyNested(int a, int b, int c)
    {
        if (a > 0)
        {
            if (b > 0)
            {
                if (c > 0)
                {
                    if (a + b > c)
                    {
                        if (b + c > a)
                        {
                            return a + b + c;
                        }
                    }
                }
            }
        }
        return 0;
    }

    // Maintainability: too many parameters
    public int TooManyParams(int a, int b, int c, int d, int e, int f, int g, int h)
    {
        return a + b + c + d + e + f + g + h;
    }

    // Maintainability: magic numbers
    public double CalculatePrice(double basePrice)
    {
        return basePrice * 1.0825 + 5.99 - 0.15 * basePrice;
    }

    // Maintainability: goto statement
    public int WithGoto(int n)
    {
        int sum = 0;
        int i = 0;
        start:
        if (i >= n) goto end;
        sum += i;
        i++;
        goto start;
        end:
        return sum;
    }
}
