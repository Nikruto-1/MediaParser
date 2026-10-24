using MediaParser.IRepositories;

namespace MediaParser.IRepositories
{
    public interface IUnitOfWork
    {
        IMediaSourceRepository MediaSourceRepository { get; }
        Task<int> CommitAsync();
    }
}
