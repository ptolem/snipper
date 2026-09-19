namespace CoreLib;

using System.Text;

// SNP0028: qualifiers that bind identically without them. Exercised from App/Worker.cs.

public class QualifierScenarios
{
    private int _value;

    public int Positive(int input)
    {
        this._value = input; // redundant: no shadow in scope
        return this._value; // redundant: no shadow in scope
    }

    public int Negative(int _value)
    {
        this._value = _value; // load-bearing: the parameter shadows the field
        return this._value; // load-bearing
    }
}

public static class QualifiedTypeScenarios
{
    public static int Exercise()
    {
        // Both qualifications redundant: 'using System.Text' is in scope above.
        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        var other = new StringBuilder();
        return builder.Length + other.Length;
    }
}
