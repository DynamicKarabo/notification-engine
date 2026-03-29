using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NotificationEngine.Infrastructure.Persistence;

namespace NotificationEngine.Infrastructure.Persistence;

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        var connectionString = args.Length > 0 
            ? args[0] 
            : "Server=localhost;Database=NotificationEngine;User=sa;Password=YourPassword;TrustServerCertificate=True";

        optionsBuilder.UseSqlServer(connectionString);

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
