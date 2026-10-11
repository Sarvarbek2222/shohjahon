using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Question> Questions { get; set; }
    public DbSet<Option> Options { get; set; }
    public DbSet<AppUser> Users { get; set; }
    public DbSet<TestAttempt> TestAttempts { get; set; }
    public DbSet<TestAnswer> TestAnswers { get; set; }
    public DbSet<UserMistake> UserMistakes { get; set; }
    public DbSet<DeviceRequest> DeviceRequests { get; set; }
    public DbSet<SubscriptionPlan> SubscriptionPlans { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<QuestionTopic> QuestionTopics { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuestionTopic>(b =>
        {
            b.ToTable("QuestionTopics");
            b.HasIndex(x => new { x.Topic, x.SortOrder });
            b.HasIndex(x => new { x.QuestionId, x.Topic }).IsUnique();
        });

        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("Users");
            b.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<TestAttempt>(b =>
        {
            b.ToTable("TestAttempts");
            b.HasIndex(a => a.AttemptKey).IsUnique();
            b.HasIndex(a => new { a.UserId, a.FinishedAt });
            b.Ignore(a => a.Percent);
            b.HasMany(a => a.Answers).WithOne(x => x.Attempt!).HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestAnswer>(b =>
        {
            b.ToTable("TestAnswers");
            b.HasIndex(x => x.QuestionId);
        });

        modelBuilder.Entity<UserMistake>(b =>
        {
            b.ToTable("UserMistakes");
            b.HasIndex(m => new { m.UserId, m.QuestionId }).IsUnique();
        });

        modelBuilder.Entity<DeviceRequest>(b =>
        {
            b.ToTable("UserDeviceRequests");
            b.HasIndex(r => new { r.Status, r.CreatedAt });
            b.HasIndex(r => new { r.UserId, r.DeviceId });
            b.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubscriptionPlan>(b => b.ToTable("SubscriptionPlans"));

        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("Payments");
            b.HasIndex(p => new { p.UserId, p.CreatedAt });
            b.HasIndex(p => new { p.Status, p.PaidAt });
            b.HasIndex(p => new { p.Provider, p.ProviderTransId });
            b.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(p => p.Plan).WithMany().HasForeignKey(p => p.PlanId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
