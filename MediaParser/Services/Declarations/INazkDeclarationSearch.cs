using MediaParser.Models;

namespace MediaParser.Services.Declarations;

public interface INazkDeclarationSearch
{
    Task<AccountCheckResult> SearchAsync(string fullName, CancellationToken cancellationToken);
}
