namespace SecureCloudBackup.Core;

/// <summary>
/// Validates password strength for security-sensitive operations.
/// </summary>
public static class PasswordValidator
{
    /// <summary>
    /// Minimum password length required for new setups.
    /// </summary>
    public const int MinPasswordLength = 12;

    /// <summary>
    /// Validates that a password meets strength requirements.
    /// Requires minimum length and all four character types: uppercase, lowercase, digits, and special characters.
    /// </summary>
    /// <exception cref="SecurityPolicyException">Thrown when the password does not meet requirements.</exception>
    public static void Validate(ReadOnlySpan<char> password)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new SecurityPolicyException(
                $"Password must be at least {MinPasswordLength} characters long.",
                SecurityPolicyType.WeakPassword);
        }

        bool hasUpper = false, hasLower = false, hasDigit = false, hasSpecial = false;
        foreach (var c in password)
        {
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else if (!char.IsLetterOrDigit(c)) hasSpecial = true;
        }

        if (!hasUpper || !hasLower || !hasDigit || !hasSpecial)
        {
            throw new SecurityPolicyException(
                "Password must contain all of: uppercase, lowercase, digits, and special characters.",
                SecurityPolicyType.WeakPassword);
        }
    }

    /// <summary>
    /// Legacy <c>string</c> overload of <see cref="Validate(ReadOnlySpan{char})"/>.
    /// </summary>
    public static void Validate(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        Validate(password.AsSpan());
    }
}
