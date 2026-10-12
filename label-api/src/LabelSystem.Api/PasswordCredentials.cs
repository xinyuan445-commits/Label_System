using System.Security.Cryptography;

namespace LabelSystem.Api;

public static class PasswordCredentials
{
    public const int CurrentIterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static PasswordCredentialValue Create(string password)
    {
        Validate(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, CurrentIterations, HashAlgorithmName.SHA256, HashSize);
        return new PasswordCredentialValue(hash, salt, CurrentIterations);
    }

    public static bool Verify(string password, byte[] expectedHash, byte[] salt, int iterations)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 128 ||
            expectedHash.Length is < 32 or > 64 || salt.Length is < 16 or > 32 ||
            iterations < 100_000)
            return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static void Validate(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 128)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "PASSWORD_INVALID", "密码不能为空或超过 128 个字符。");
    }
}

public sealed record PasswordCredentialValue(byte[] Hash, byte[] Salt, int Iterations);
