namespace WebLib.OrdinalEnums;

using System.Text.Json;
using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------
// F3b: an enum member's ordinal is a value on the wire, so a reference count
// cannot prove it dead. Deleting one renumbers every later member, and the next
// in-flight message decodes as a DIFFERENT member - silently.
// ---------------------------------------------------------------------------

/// <summary>Bound as a converter type argument: STJ converts enums by ordinal.</summary>
public enum ConverterBoundCommandType
{
    Alpha,
    [Obsolete("Ordinal 1 is on the wire; removing renumbers Bravo->Charlie")]
    Bravo,
    Charlie,
}

/// <summary>Bound by wholesale reflection: every member is dispatched by value.</summary>
public enum ReflectedBoundCommandType
{
    One,
    [Obsolete("Enumerated at runtime; removing renumbers Three->Four")]
    Two,
    Three,
}

/// <summary>
/// Negative control: nothing binds this enum's ordinals, so the member stays
/// reportable - but only as Advisory, with the renumbering hazard named.
/// </summary>
public enum UnboundCommandType
{
    First,
    [Obsolete("no ordinal contract here")]
    Second,
    Third,
}

public abstract class OrdinalConverterBase<TMessage, TMessageType> : JsonConverter<TMessage>
    where TMessageType : struct, Enum
{
    public override TMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => default;

    public override void Write(Utf8JsonWriter writer, TMessage value, JsonSerializerOptions options)
    {
    }
}

public sealed class ConverterBoundCommandConverter : OrdinalConverterBase<object, ConverterBoundCommandType>;

public static class ReflectedEnumUsage
{
    public static int CountReflected() => Enum.GetValues<ReflectedBoundCommandType>().Length;
}

/// <summary>
/// Negative control for the converter check: an enum used as a generic key is not
/// an ordinal contract. <c>Dictionary&lt;K,V&gt;</c> must not be mistaken for a
/// JsonConverter.
/// </summary>
public static class UnboundEnumLookup
{
    public static readonly IReadOnlyDictionary<UnboundCommandType, string> Map = new Dictionary<UnboundCommandType, string>();
}