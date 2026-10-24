#nullable enable
#if !NET9_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis;

// Pre-.NET 9 stand-in; ILLink matches the attribute by name, so the switch stays trimmable on net8.0.
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class FeatureSwitchDefinitionAttribute(string switchName) : Attribute
{
    public string SwitchName { get; } = switchName;
}
#endif
