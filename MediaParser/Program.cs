using MediaParser.Data;
using MediaParser.IRepositories;
using MediaParser.Models;
using MediaParser.Repositories;
using MediaParser.Services;
using MediaParser.Services.DBServices;
using MediaParser.Services.Captcha;
using MediaParser.Services.Declarations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Machine settings stay out of source control. Environment/CLI settings win.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);
builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.IdleTimeout = TimeSpan.FromHours(2);
});
builder.Services.AddScoped<ResultsRepository>();
builder.Services.AddScoped<MediaService>();
builder.Services.AddScoped<MediaSourceService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<SearchJobOptions>(builder.Configuration.GetSection("SearchJobs"));
builder.Services.AddOptions<CaptchaOptions>().Bind(builder.Configuration.GetSection("Captcha"))
    .Validate(options => options.MaxTasksPerSearch is >= 1 and <= 10, "Captcha MaxTasksPerSearch must be between 1 and 10.")
    .Validate(options => options.TimeoutSeconds is >= 15 and <= 180, "Captcha TimeoutSeconds must be between 15 and 180.")
    .Validate(options => options.PollIntervalSeconds is >= 5 and <= 30, "Captcha PollIntervalSeconds must be between 5 and 30.")
    .ValidateOnStart();
builder.Services.AddHttpClient<ICaptchaSolver, TwoCaptchaClient>().ConfigurePrimaryHttpMessageHandler(() =>
    new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
builder.Services.AddSingleton<ICaptchaAutomation, BrowserCaptchaAutomation>();
builder.Services.AddHttpClient<INazkDeclarationSearch, NazkDeclarationSearch>().ConfigurePrimaryHttpMessageHandler(() =>
    new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
builder.Services.AddSingleton<IAccountChecker, AccountChecker>();
builder.Services.AddSingleton<IYouControlDeclarationSearch, YouControlDeclarationSearch>();
builder.Services.AddSingleton<SearchJobService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SearchJobService>());

var appData = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(appData);
var protection = builder.Services.AddDataProtection()
    .SetApplicationName("MediaParser")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(appData, "keys")));
if (OperatingSystem.IsWindows())
    protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<EncryptionHelper>();

var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
builder.Services.AddDbContext<AppDBContext>(options =>
{
    if (databaseProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        var connection = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Set ConnectionStrings:DefaultConnection to use SQL Server.");
        options.UseSqlServer(connection);
    }
    else if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var connection = builder.Configuration.GetConnectionString("Sqlite")
            ?? $"Data Source={Path.Combine(appData, "mediaparser.db")}";
        options.UseSqlite(connection);
    }
    else
    {
        throw new InvalidOperationException("Database:Provider must be Sqlite or SqlServer.");
    }
});

var app = builder.Build();

// Initialize only the standalone local database. Existing SQL Server DBs are untouched.
if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var database = scope.ServiceProvider.GetRequiredService<AppDBContext>();
    await database.Database.EnsureCreatedAsync();
    await MediaSourceSeed.InitializeAsync(database);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
