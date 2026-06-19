using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SalaryTelegramBot.Api.Services;

public class EncryptionService
{
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 100_000;

    public byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public byte[] DeriveKey(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    public byte[] Encrypt(decimal amount, byte[] key)
    {
        var bits = decimal.GetBits(amount);
        var plaintext = new byte[sizeof(int) * 4];
        Buffer.BlockCopy(bits, 0, plaintext, 0, plaintext.Length);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        ciphertext.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    public decimal Decrypt(byte[] encrypted, byte[] key)
    {
        if (encrypted.Length < NonceSize + TagSize + sizeof(int) * 4)
            throw new CryptographicException("Invalid encrypted data length.");

        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> tag = stackalloc byte[TagSize];
        var ciphertext = new byte[encrypted.Length - NonceSize - TagSize];

        encrypted.AsSpan(0, NonceSize).CopyTo(nonce);
        encrypted.AsSpan(NonceSize, TagSize).CopyTo(tag);
        Buffer.BlockCopy(encrypted, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        var bits = new int[4];
        Buffer.BlockCopy(plaintext, 0, bits, 0, plaintext.Length);
        return new decimal(bits);
    }
}
