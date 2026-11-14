using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace PersonalOrderDesk.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DeskDbContext>
{
    public DeskDbContext CreateDbContext(string[] args) { var options = new DbContextOptionsBuilder<DeskDbContext>().UseSqlite("Data Source=design.db;Foreign Keys=True").Options; return new DeskDbContext(options); }
}
