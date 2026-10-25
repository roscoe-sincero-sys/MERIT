namespace Merit.Application.Errors;

/// <summary>Base for failures the user can be told about in plain words. Messages never carry patient data.</summary>
public abstract class MeritException : Exception
{
    protected MeritException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>The record does not exist or is outside the caller's scope; the two are indistinguishable on purpose.</summary>
public sealed class NotFoundException : MeritException
{
    public NotFoundException(string message = "The record was not found, or you do not have access to it.", Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class PermissionDeniedException : MeritException
{
    public PermissionDeniedException(string permission, Exception? inner = null)
        : base($"You do not have permission to do this ({permission}).", inner)
    {
        Permission = permission;
    }

    public string Permission { get; }
}

/// <summary>Someone else changed the record since it was read (row version mismatch).</summary>
public sealed class ConcurrencyException : MeritException
{
    public ConcurrencyException(string message = "This record was changed by someone else. Reload it and try again.", Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>A business rule refused the change; the message is shown to the user as it is.</summary>
public sealed class BusinessRuleException : MeritException
{
    public BusinessRuleException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Input failed validation; carries field-level messages for the form.</summary>
public sealed class ValidationException : MeritException
{
    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Some fields need attention.")
    {
        Errors = errors;
    }

    public ValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>Collects field errors and throws one <see cref="ValidationException"/>.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> errors = new(StringComparer.Ordinal);

    public bool IsValid => errors.Count == 0;

    public Validator Required(string field, string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(field, $"{label} is required.");
        }

        return this;
    }

    public Validator Required<T>(string field, T? value, string label)
        where T : struct
    {
        if (value is null)
        {
            Add(field, $"{label} is required.");
        }

        return this;
    }

    public Validator MaxLength(string field, string? value, int max, string label)
    {
        if (value is not null && value.Length > max)
        {
            Add(field, $"{label} must be {max} characters or fewer.");
        }

        return this;
    }

    public Validator When(bool condition, string field, string message)
    {
        if (condition)
        {
            Add(field, message);
        }

        return this;
    }

    public void Add(string field, string message)
    {
        if (!errors.TryGetValue(field, out var list))
        {
            list = [];
            errors[field] = list;
        }

        list.Add(message);
    }

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new ValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
        }
    }
}
