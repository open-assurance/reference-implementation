namespace Cai.Reference.Engine;

/// <summary>Every detector, in the order they run. Each is a plain function over the scan context.</summary>
public static class Detectors
{
    public static readonly IReadOnlyList<Action<ScanContext>> All = new List<Action<ScanContext>>
    {
        CodeHealth.Run,
    };
}
