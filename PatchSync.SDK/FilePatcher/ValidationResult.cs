namespace PatchSync.SDK;

public record ValidationResult
{
    private ValidationResult()
    {
    }

    public ValidationStatus Status { get; private init; }
    public string? Hash { get; private init; }
    public string? Message { get; private init; }

    public static ValidationResult InProgress(string message) => new()
    {
        Status = ValidationStatus.InProgress,
        Message = message
    };

    public static ValidationResult Valid(string hash) => new()
    {
        Status = ValidationStatus.Valid,
        Hash = hash
    };

    public static ValidationResult Invalid(string? message = null) => new()
    {
        Status = ValidationStatus.Invalid,
        Message = message
    };
}
