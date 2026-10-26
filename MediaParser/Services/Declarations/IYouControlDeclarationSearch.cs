using MediaParser.Models;

namespace MediaParser.Services.Declarations;

public interface IYouControlDeclarationSearch
{
    Task<AccountCheckResult> SearchAsync(string fullName, CancellationToken cancellationToken, bool interactive = false);
}
