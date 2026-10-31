namespace Corfedo.Troupe;

internal static class DiagnosticCodes
{
    internal const string ClosedDeclaration = "TRP015";
    internal const string InvalidPhase = "TRP016";
    internal const string CallbackFailure = "TRP017";
    internal const string UnappliedPipeline = "TRP105";
}

internal static class Errors
{
    internal static CompositionException Fail(string code, string message, params CompositionOwner[] owners) =>
        new([new(code, DiagnosticSeverity.Error, message, Array.AsReadOnly(owners))]);
}

internal sealed class DiagnosticComparer : IEqualityComparer<CompositionDiagnostic>
{
    internal static readonly DiagnosticComparer Instance = new();
    public bool Equals(CompositionDiagnostic? x, CompositionDiagnostic? y) =>
        ReferenceEquals(x, y) || x is not null && y is not null && x.Code == y.Code &&
        x.Severity == y.Severity && x.Message == y.Message && x.Owners.SequenceEqual(y.Owners);
    public int GetHashCode(CompositionDiagnostic diagnostic)
    {
        var hash = new HashCode();
        hash.Add(diagnostic.Code); hash.Add(diagnostic.Severity); hash.Add(diagnostic.Message);
        foreach (var owner in diagnostic.Owners) hash.Add(owner);
        return hash.ToHashCode();
    }
}
