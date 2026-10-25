using System.Diagnostics;
using System.Reflection;
using OpenQA.Selenium;

public class FakeDriver : DispatchProxy
{
    public Func<TimeSpan, string> Content { get; set; } = _ => "";
    public Stopwatch Elapsed { get; } = new();
    public string CurrentUrl { get; set; } = "about:blank";
    public bool NavigationTimeout { get; set; }
    public IWebDriver Driver { get; private set; } = null!;

    public static FakeDriver Create(Func<TimeSpan, string> content, bool navigationTimeout = false)
    {
        var driver = DispatchProxy.Create<IWebDriver, FakeDriver>();
        var fixture = (FakeDriver)driver;
        fixture.Content = content;
        fixture.NavigationTimeout = navigationTimeout;
        fixture.Driver = driver;
        return fixture;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "get_PageSource": return Content(Elapsed.Elapsed);
            case "get_Url": return CurrentUrl;
            case "Navigate":
                var navigation = DispatchProxy.Create<INavigation, FakeNavigation>();
                ((FakeNavigation)navigation).Owner = this;
                return navigation;
            default: throw new NotSupportedException(method?.Name);
        }
    }
}

public class FakeNavigation : DispatchProxy
{
    public FakeDriver Owner { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name != "GoToUrl") throw new NotSupportedException(method?.Name);
        Owner.CurrentUrl = args![0]!.ToString()!;
        Owner.Elapsed.Restart();
        if (Owner.NavigationTimeout) throw new WebDriverTimeoutException("Synthetic navigation timeout.");
        return null;
    }
}
