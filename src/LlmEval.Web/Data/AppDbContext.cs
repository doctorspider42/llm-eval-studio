using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<LlmModel> Models => Set<LlmModel>();
    public DbSet<TestCase> TestCases => Set<TestCase>();
    public DbSet<Iteration> Iterations => Set<Iteration>();
    public DbSet<IterationResult> Results => Set<IterationResult>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<JudgeRun> JudgeRuns => Set<JudgeRun>();
    public DbSet<Batch> Batches => Set<Batch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Email).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.JudgeModelId).IsUnique();
            e.HasOne(x => x.JudgeModel).WithMany().HasForeignKey(x => x.JudgeModelId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Provider>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.HasMany(x => x.Models).WithOne(x => x.Provider).HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LlmModel>(e =>
        {
            e.Property(x => x.ModelId).HasMaxLength(200);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.HasIndex(x => new { x.ProviderId, x.ModelId }).IsUnique();
        });

        b.Entity<TestCase>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Iterations).WithOne(x => x.TestCase).HasForeignKey(x => x.TestCaseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Iteration>(e =>
        {
            e.HasIndex(x => new { x.TestCaseId, x.Number }).IsUnique();
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Results).WithOne(x => x.Iteration).HasForeignKey(x => x.IterationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.JudgeRuns).WithOne(x => x.Iteration).HasForeignKey(x => x.IterationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Batch>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Iterations).WithOne(x => x.Batch).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JudgeRun>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IterationResult>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Ignore(x => x.BlindLabel);
            // Restrict: a model with history can't be hard-deleted; disable it instead.
            e.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Ratings).WithOne(x => x.Result).HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Rating>(e =>
        {
            e.HasIndex(x => new { x.ResultId, x.UserId }).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_Rating_Stars", "\"Stars\" BETWEEN 1 AND 5"));
        });
    }
}
