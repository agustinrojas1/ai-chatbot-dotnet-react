using Chatbot.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chatbot.Infrastructure.Persistence;

internal sealed class BusinessConfiguration : IEntityTypeConfiguration<Business>
{
    public void Configure(EntityTypeBuilder<Business> b)
    {
        b.ToTable("businesses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.PersonaName).HasMaxLength(100).IsRequired();
        b.Property(x => x.PersonaPrompt).IsRequired();
        b.Property(x => x.Greeting).IsRequired();
        b.Property(x => x.QuickReplies).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Timezone).HasMaxLength(64).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.HasIndex(x => x.Niche).IsUnique();
    }
}

internal sealed class OfferingConfiguration : IEntityTypeConfiguration<Offering>
{
    public void Configure(EntityTypeBuilder<Offering> b)
    {
        b.ToTable("offerings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).IsRequired();
        b.Property(x => x.PriceUnit).HasMaxLength(16).IsRequired();
        b.Property(x => x.Attributes).HasColumnType("jsonb").IsRequired();
        b.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.BusinessId);
        b.HasIndex(x => x.Attributes).HasMethod("gin");
    }
}

internal sealed class AvailabilitySlotConfiguration : IEntityTypeConfiguration<AvailabilitySlot>
{
    public const string BookedNonNegative = "ck_availability_slots_booked_non_negative";
    public const string BookedWithinCapacity = "ck_availability_slots_booked_lte_capacity";
    public const string CapacityPositive = "ck_availability_slots_capacity_positive";

    public void Configure(EntityTypeBuilder<AvailabilitySlot> b)
    {
        b.ToTable("availability_slots", t =>
        {
            t.HasCheckConstraint(CapacityPositive, "\"Capacity\" > 0");
            t.HasCheckConstraint(BookedNonNegative, "\"Booked\" >= 0");
            t.HasCheckConstraint(BookedWithinCapacity, "\"Booked\" <= \"Capacity\"");
        });
        b.HasKey(x => x.Id);
        b.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Offering>().WithMany().HasForeignKey(x => x.OfferingId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.BusinessId, x.StartsAt });
        b.HasIndex(x => x.OfferingId);
    }
}

internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> b)
    {
        b.ToTable("bookings");
        b.HasKey(x => x.Id);
        b.Property(x => x.CustomerName).HasMaxLength(200).IsRequired();
        b.Property(x => x.CustomerPhone).HasMaxLength(50).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.Items).HasColumnType("jsonb");
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ChatSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Slot).WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Offering).WithMany().HasForeignKey(x => x.OfferingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.BusinessId, x.CreatedAt });
        b.HasIndex(x => x.SessionId);
        b.HasIndex(x => x.SlotId);
        b.HasIndex(x => x.OfferingId);
    }
}

internal sealed class HandoffRequestConfiguration : IEntityTypeConfiguration<HandoffRequest>
{
    public void Configure(EntityTypeBuilder<HandoffRequest> b)
    {
        b.ToTable("handoff_requests");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).IsRequired();
        b.Property(x => x.Summary).IsRequired();
        b.HasOne<ChatSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SessionId);
        b.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> b)
    {
        b.ToTable("chat_sessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Language).HasMaxLength(8);
        b.Property(x => x.ClientIpHash).HasMaxLength(128).IsRequired();
        b.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BusinessId);
        b.HasIndex(x => x.LastActivityAt);
    }
}

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> b)
    {
        b.ToTable("chat_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Content).IsRequired();
        b.HasOne<ChatSession>().WithMany(s => s.Messages).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.SessionId, x.CreatedAt });
    }
}

internal sealed class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> b)
    {
        b.ToTable("agent_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Model).HasMaxLength(200).IsRequired();
        b.Property(x => x.CostUsd).HasPrecision(18, 8);
        b.HasOne<ChatSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        // NoAction: deleting a session cascades to both messages and runs in the same statement.
        b.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.UserMessageId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.SessionId, x.StartedAt });
        b.HasIndex(x => x.UserMessageId);
    }
}

internal sealed class TraceEventConfiguration : IEntityTypeConfiguration<TraceEvent>
{
    public const string TypeIsClosedSet = "ck_trace_events_type_closed_set";

    public void Configure(EntityTypeBuilder<TraceEvent> b)
    {
        var allowed = string.Join(", ", Enum.GetNames<TraceEventType>().Select(n => $"'{n}'"));
        b.ToTable("trace_events", t => t.HasCheckConstraint(TypeIsClosedSet, $"\"Type\" IN ({allowed})"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Input).HasColumnType("jsonb");
        b.Property(x => x.Output).HasColumnType("jsonb");
        b.HasOne<AgentRun>().WithMany(r => r.Events).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();
    }
}

internal sealed class ModelPricingConfiguration : IEntityTypeConfiguration<ModelPricing>
{
    public void Configure(EntityTypeBuilder<ModelPricing> b)
    {
        b.ToTable("model_pricings");
        b.HasKey(x => x.Model);
        b.Property(x => x.Model).HasMaxLength(200);
    }
}
