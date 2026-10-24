using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EmailArchive.Core.Ledger;

/// <summary>Used only by the dotnet-ef tool to generate migrations. The connection string is never opened for that.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EmailArchiveDbContext>
{
    public EmailArchiveDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EmailArchiveDbContext>()
            .UseSqlServer("Server=localhost;Database=EmailArchive;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new EmailArchiveDbContext(options);
    }
}
