using Microsoft.EntityFrameworkCore;
using MediaParser.Models.DBModels;
using System;
using System.Linq;
using MediaParser.Services;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MediaParser.Data
{
    public class AppDBContext : DbContext
    {
        private readonly EncryptionHelper _encryptionHelper;

        public AppDBContext(DbContextOptions<AppDBContext> options, EncryptionHelper encryptionHelper)
            : base(options)
        {
            _encryptionHelper = encryptionHelper;
        }

        public DbSet<MediaSource> MediaSources { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            var converter = new ValueConverter<string?, byte[]?>(
                v => v == null ? null : _encryptionHelper.Encrypt(v),  // `string` -> `byte[]`
                v => v == null ? null : _encryptionHelper.Decrypt(v)   // `byte[]` -> `string`
            );

            modelBuilder.Entity<MediaSource>()
                .Property(m => m.ApiKey)
                .HasConversion(converter);

            if (Database.IsSqlServer())
                modelBuilder.Entity<MediaSource>().Property(m => m.ApiKey).HasColumnType("VARBINARY(MAX)");
        }


    }
}
