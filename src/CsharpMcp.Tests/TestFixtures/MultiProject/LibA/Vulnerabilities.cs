using System;
using System.Data.SqlClient;
using System.IO;

namespace LibA;

/// <summary>Contains code with known ISO 5055 violations for testing.</summary>
public class Vulnerabilities
{
    // CWE-89: SQL Injection — user input concatenated into query
    public void UnsafeQuery(string userInput)
    {
        var connection = new SqlConnection("Server=.;Database=test");
        var command = new SqlCommand("SELECT * FROM Users WHERE Name = '" + userInput + "'", connection);
        connection.Open();
        command.ExecuteReader();
    }

    // CWE-476: Null dereference — no null check before use
    public int GetLength(string? input)
    {
        return input.Length;
    }

    // Deeply nested code — maintainability issue
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

    // Empty catch block — reliability issue
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

    // Magic numbers — maintainability issue
    public double CalculatePrice(double basePrice)
    {
        return basePrice * 1.0825 + 5.99 - 0.15 * basePrice;
    }
}
