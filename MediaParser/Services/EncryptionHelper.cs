using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace MediaParser.Services;

public class EncryptionHelper
{
    private static readonly byte[] Prefix = "MPD1"u8.ToArray();
    private readonly IDataProtector _protector;
    private readonly IConfiguration _configuration;

    public EncryptionHelper(IConfiguration configuration, IDataProtectionProvider protectionProvider)
    {
        _configuration = configuration;
        _protector = protectionProvider.CreateProtector("MediaParser.ApiKeys.v1");
    }

    public byte[] Encrypt(string plainText)
    {
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(plainText));
        return [.. Prefix, .. encrypted];
    }

    public string Decrypt(byte[] cipherText)
    {
        if (cipherText.AsSpan().StartsWith(Prefix))
            return Encoding.UTF8.GetString(_protector.Unprotect(cipherText[Prefix.Length..]));

        // Keep access to API keys encrypted by earlier SQL Server installations.
        var key = _configuration["Encryption:Key"];
        var iv = _configuration["Encryption:IV"];
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(iv))
            throw new InvalidOperationException("Legacy API keys require Encryption:Key and Encryption:IV in local configuration.");
        using var aes = Aes.Create();
        aes.Key = Convert.FromBase64String(key);
        aes.IV = Convert.FromBase64String(iv);
        using var decryptor = aes.CreateDecryptor();
        using var input = new MemoryStream(cipherText);
        using var cryptoStream = new CryptoStream(input, decryptor, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream);
        return reader.ReadToEnd();
    }
}
