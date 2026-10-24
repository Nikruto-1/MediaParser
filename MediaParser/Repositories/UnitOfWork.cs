using MediaParser.Data;
using MediaParser.IRepositories;

namespace MediaParser.Repositories
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly AppDBContext _context;
        private IMediaSourceRepository? _mediaSourceRepository;

        public UnitOfWork(AppDBContext context)
        {
            _context = context;
        }

        public IMediaSourceRepository MediaSourceRepository =>
            _mediaSourceRepository ??= new MediaSourceRepository(_context);

        public async Task<int> CommitAsync()
        {
            return await _context.SaveChangesAsync();
        }
    }
}
