using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LlmEval.Web.Data;

/// <summary>Lets `dotnet ef migrations add` work without spinning up Aspire.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=llmevaldb;Username=postgres;Password=postgres")
            .Options);
}
