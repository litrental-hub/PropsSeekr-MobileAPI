namespace PropSeekr.Authorization;

[AttributeUsage(AttributeTargets.Method)]
public sealed class AppAttestationPurposeAttribute(string purpose) : Attribute
{
    public string Purpose { get; } = purpose;
}
