using System.Security.Cryptography;
using System.Text;
using MediaParser.Data;
using MediaParser.Models.DBModels;
using MediaParser.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    passed++;
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
    passed++;
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
Check(await database.MediaSources.CountAsync() == 20, "Fresh SQLite database receives all 20 sources");
Check(await database.MediaSources.CountAsync(item => item.LinkType == "User") == 18 &&
    await database.MediaSources.CountAsync(item => item.LinkType == "FullName") == 2,
    "Default sources contain 18 nickname providers and two full-name providers");
var nazk = await database.MediaSources.SingleAsync(item => item.Name == "НАЗК — декларації");
Check(nazk.LinkType == "FullName" && string.IsNullOrEmpty(nazk.ApiKey) &&
    Uri.TryCreate(nazk.ApiUrl?.Replace("{0}", "test"), UriKind.Absolute, out var nazkApi) &&
    nazkApi.Scheme == "https" && nazkApi.Host == "public-api.nazk.gov.ua",
    "NAZK uses the official HTTPS public API without an API key");
await MediaSourceSeed.InitializeAsync(database);
Check(await database.MediaSources.CountAsync() == 20 &&
    await database.MediaSources.CountAsync(item => item.Name == "НАЗК — декларації") == 1,
    "Repeated startup does not duplicate default sources or NAZK");

database.MediaSources.Remove(nazk);
await database.SaveChangesAsync();
Check(await database.MediaSources.CountAsync() == 19, "The upgrade fixture represents a database created before NAZK was added");
await MediaSourceSeed.InitializeAsync(database);
nazk = await database.MediaSources.SingleAsync(item => item.Name == "НАЗК — декларації");
Check(await database.MediaSources.CountAsync() == 20 && nazk.LinkType == "FullName",
    "Startup adds NAZK exactly once to an existing 19-source database");

nazk.ApiUrl = "https://custom.example/declarations?name={0}";
nazk.SearchUrl = "https://custom.example/people/{0}";
nazk.ApiKey = "custom-nazk-test-key";
nazk.ErrorKeywordsRaw = "[\"custom error\"]";
var removedDefault = await database.MediaSources.SingleAsync(item => item.Name == "Instagram");
database.MediaSources.Remove(removedDefault);
var custom = new MediaSource
{
    Name = "Custom storage fixture", LinkType = "User", SearchUrl = "https://custom.example/{0}",
    ApiKey = "custom-source-test-key", ErrorKeywordsRaw = "[]"
};
database.MediaSources.Add(custom);
await database.SaveChangesAsync();
await MediaSourceSeed.InitializeAsync(database);
database.ChangeTracker.Clear();
nazk = await database.MediaSources.SingleAsync(item => item.Name == "НАЗК — декларації");
Check(nazk.ApiUrl == "https://custom.example/declarations?name={0}" &&
    nazk.SearchUrl == "https://custom.example/people/{0}" && nazk.ApiKey == "custom-nazk-test-key" &&
    nazk.ErrorKeywordsRaw == "[\"custom error\"]", "Startup preserves a user-customized NAZK source and its credentials");
Check(!await database.MediaSources.AnyAsync(item => item.Name == "Instagram"),
    "Adding NAZK does not recreate another default source the user deleted");
Check((await database.MediaSources.SingleAsync(item => item.Id == custom.Id)).ApiKey == "custom-source-test-key" &&
    await database.MediaSources.CountAsync() == 20, "Startup preserves custom sources without duplicates");
await MediaSourceSeed.InitializeAsync(database);
Check(await database.MediaSources.CountAsync() == 20 &&
    await database.MediaSources.CountAsync(item => item.Name == "НАЗК — декларації") == 1,
    "Reseeding the customized database remains idempotent");
var rumble = await database.MediaSources.SingleAsync(source => source.Name == "Rumble");
Check(rumble.SearchUrl == "https://rumble.com/user/{0}", "Rumble defaults to the user profile namespace");
rumble.SearchUrl = "https://rumble.com/{0}";
await database.SaveChangesAsync();
await MediaSourceSeed.InitializeAsync(database);
Check(rumble.SearchUrl == "https://rumble.com/user/{0}", "Startup repairs only the old bundled Rumble URL");
rumble.SearchUrl = "https://rumble.com/c/{0}";
await database.SaveChangesAsync();
await MediaSourceSeed.InitializeAsync(database);
Check(rumble.SearchUrl == "https://rumble.com/c/{0}", "Startup preserves a user-edited Rumble channel URL");
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

await using var customOnlyConnection = new SqliteConnection("Data Source=:memory:");
await customOnlyConnection.OpenAsync();
var customOnlyOptions = new DbContextOptionsBuilder<AppDBContext>().UseSqlite(customOnlyConnection).Options;
await using var customOnly = new AppDBContext(customOnlyOptions, encryption);
await customOnly.Database.EnsureCreatedAsync();
customOnly.MediaSources.Add(new MediaSource
{
    Name = "Existing custom-only database", LinkType = "User", SearchUrl = "https://custom.example/{0}", ErrorKeywordsRaw = "[]"
});
await customOnly.SaveChangesAsync();
await MediaSourceSeed.InitializeAsync(customOnly);
Check(await customOnly.MediaSources.CountAsync() == 2 &&
    await customOnly.MediaSources.AnyAsync(item => item.Name == "Existing custom-only database") &&
    await customOnly.MediaSources.CountAsync(item => item.Name == "НАЗК — декларації") == 1,
    "Existing custom-only databases gain NAZK without restoring all deleted default providers");
await MediaSourceSeed.InitializeAsync(customOnly);
Check(await customOnly.MediaSources.CountAsync() == 2,
    "NAZK upgrade remains idempotent in an existing custom-only database");
var renamedRegistry = await customOnly.MediaSources.SingleAsync(item => item.Name == "НАЗК — декларації");
renamedRegistry.Name = "User label for the official registry";
await customOnly.SaveChangesAsync();
await MediaSourceSeed.InitializeAsync(customOnly);
Check(await customOnly.MediaSources.CountAsync() == 2 &&
    await customOnly.MediaSources.AnyAsync(item => item.Name == "User label for the official registry") &&
    !await customOnly.MediaSources.AnyAsync(item => item.Name == "НАЗК — декларації"),
    "Renaming an existing official registry source does not create a duplicate default NAZK source");
Console.WriteLine($"Passed {passed} storage regression tests.");
