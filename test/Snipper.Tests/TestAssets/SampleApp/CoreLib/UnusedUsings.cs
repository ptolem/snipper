using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System;

namespace CoreLib;

// SNP0019 fixture: System.Text.Json and System.Xml.Linq are never used (CS8019);
// System duplicates the ImplicitUsings-generated global using (CS8933);
// System.Text is genuinely used (StringBuilder) and must not be flagged.
public static class UnusedUsingsProbe
{
    public static int Count() => new StringBuilder().Append("abc").Length;
}
