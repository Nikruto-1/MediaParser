using System.Security.Cryptography;
using System.Text;
using MediaParser.Data;
using MediaParser.Models.DBModels;
using MediaParser.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

var legacyKey = RandomNumberGenerator.GetBytes(32);
var legacyIv = RandomNumberGenerator.GetBytes(16);
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Encryption:Key"] = Convert.ToBase64String(legacyKey),
    ["Encryption:IV"] = Convert.ToBase64String(legacyIv)
}).Build();
var encryption = new EncryptionHelper(configuration, new EphemeralDataProtectionProvider());
const string sampleKey = "local-test-value-Україна";
var encrypted = encryption.Encrypt(sampleKey);
Check(encryption.Decrypt(encrypted) == sampleKey, "API key encryption roundtrips Unicode");
Check(!encrypted.SequenceEqual(encryption.Encrypt(sampleKey)), "Encryption uses a new randomized payload each time");
encrypted[^1] ^= 1;
try
{
    encryption.Decrypt(encrypted);
    throw new Exception("Tampered payload was accepted");
}
catch (CryptographicException)
{
    Console.WriteLine("PASS Modified ciphertext is rejected");
}
using (var aes = Aes.Create())
{
    aes.Key = legacyKey;
    aes.IV = legacyIv;
    using var encryptor = aes.CreateEncryptor();
    var bytes = Encoding.UTF8.GetBytes(sampleKey);
    var legacy = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
    Check(encryption.Decrypt(legacy) == sampleKey, "Legacy SQL Server API keys remain readable");
}

await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var options = new DbContextOptionsBuilder<AppDBContext>().UseSqlite(connection).Options;
await using var database = new AppDBContext(options, encryption);
await database.Database.EnsureCreatedAsync();
await MediaSourceSeed.InitializeAsync(database);
Check(await database.MediaSources.CountAsync() == 19, "Fresh SQLite database receives all 19 sources");
await MediaSourceSeed.InitializeAsync(database);
Check(await database.MediaSources.CountAsync() == 19, "Repeated startup does not duplicate sources");
var source = await database.MediaSources.FirstAsync(s => s.Name == "GitHub");
source.ApiKey = sampleKey;
await database.SaveChangesAsync();
database.ChangeTracker.Clear();
Check((await database.MediaSources.SingleAsync(s => s.Id == source.Id)).ApiKey == sampleKey,
    "SQLite value converter persists and decrypts API keys");
using var command = connection.CreateCommand();
command.CommandText = "SELECT ApiKey FROM MediaSources WHERE Id = $id";
command.Parameters.AddWithValue("$id", source.Id);
var storedBytes = (byte[])(await command.ExecuteScalarAsync())!;
Check(!Encoding.UTF8.GetString(storedBytes).Contains(sampleKey), "Database stores ciphertext instead of the API key");
Console.WriteLine("Passed 8 storage regression tests.");
