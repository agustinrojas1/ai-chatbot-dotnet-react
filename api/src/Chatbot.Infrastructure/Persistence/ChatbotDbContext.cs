using Chatbot.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Persistence;

public class ChatbotDbContext(DbContextOptions<ChatbotDbContext> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<Offering> Offerings => Set<Offering>();
    public DbSet<AvailabilitySlot> AvailabilitySlots => Set<AvailabilitySlot>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<HandoffRequest> HandoffRequests => Set<HandoffRequest>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<TraceEvent> TraceEvents => Set<TraceEvent>();
    public DbSet<ModelPricing> ModelPricings => Set<ModelPricing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChatbotDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All enums are stored as their names, so rows stay readable and reordering the enum is safe.
        configurationBuilder.Properties<Niche>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<OfferingKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<BookingStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<SessionStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<HandoffStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<MessageRole>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<AgentRunStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TraceEventType>().HaveConversion<string>().HaveMaxLength(32);

        configurationBuilder.Properties<decimal>().HavePrecision(18, 6);
    }
}
