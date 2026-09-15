namespace PluginLib;

// Referenced only by assembly-name string in App/Worker.cs (plugin-loading stand-in) —
// the name evidence must suppress SNP0011 for this project.
public sealed class PluginType
{
    public int Execute() => 1;
}
