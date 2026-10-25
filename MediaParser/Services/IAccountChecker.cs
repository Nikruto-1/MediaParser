using MediaParser.Models;
using MediaParser.Models.DBModels;

namespace MediaParser.Services;

public interface IAccountChecker
{
    Task<AccountCheckResult> CheckAsync(MediaSource source, string identifier, CancellationToken cancellationToken);
}
