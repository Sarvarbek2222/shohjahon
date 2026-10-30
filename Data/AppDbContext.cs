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
    public DbSet<TopicLesson> TopicLessons { get; set; }
    public DbSet<TopicLessonChapter> TopicLessonChapters { get; set; }
    public DbSet<TopicLessonProgress> TopicLessonProgress { get; set; }
    public DbSet<LiveLesson> LiveLessons { get; set; }
    public DbSet<LiveAttendance> LiveAttendance { get; set; }
    public DbSet<LiveQuestion> LiveQuestions { get; set; }
    public DbSet<UserNotification> Notifications { get; set; }
    public DbSet<DailyReport> DailyReports { get; set; }

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

        // Video darslar, jonli darslar, bildirishnomalar, kunlik hisobot (jadvallarni DbUpgrade yaratadi)
        modelBuilder.Entity<TopicLesson>(b =>
        {
            b.ToTable("TopicLessons");
            b.HasIndex(x => x.Topic).IsUnique();
            b.Ignore(x => x.HasVideo);
            b.HasMany(x => x.Chapters).WithOne().HasForeignKey(c => c.LessonId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TopicLessonChapter>(b => b.ToTable("TopicLessonChapters"));
        modelBuilder.Entity<TopicLessonProgress>(b =>
        {
            b.ToTable("TopicLessonProgress");
            b.HasIndex(x => new { x.LessonId, x.UserId }).IsUnique();
            b.Ignore(x => x.WatchedShare);
        });
        modelBuilder.Entity<LiveLesson>(b =>
        {
            b.ToTable("LiveLessons");
            b.Ignore(x => x.PlannedEnd);
        });
        modelBuilder.Entity<LiveAttendance>(b =>
        {
            b.ToTable("LiveAttendance");
            b.HasIndex(x => new { x.LessonId, x.UserId });
        });
        modelBuilder.Entity<LiveQuestion>(b =>
        {
            b.ToTable("LiveQuestions");
            b.HasIndex(x => new { x.LessonId, x.CreatedAt });
        });
        modelBuilder.Entity<UserNotification>(b =>
        {
            b.ToTable("UserNotifications");
            b.HasIndex(x => new { x.UserId, x.ReadAt });
            b.HasIndex(x => new { x.UserId, x.CreatedAt });
        });
        modelBuilder.Entity<DailyReport>(b =>
        {
            b.ToTable("DailyReports");
            b.HasIndex(x => x.Day).IsUnique();
        });

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
